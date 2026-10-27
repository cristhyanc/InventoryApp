using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the two reads the collector work list of #924 will be built from (issue #923,
/// parent #817): what is still waiting for a match, and what is matched and has listings.
///
/// <para>These are the issue's deduplication criteria, and they are the business outcome of the
/// parent: "we can't search Coca-Cola 375 five times because we have five businesses" (the owner,
/// 27 October 2026). Two businesses with differently spelled copies of one product therefore run
/// through every test here.</para>
///
/// <para><strong>What is read, and by what.</strong> The adapter reads the mappings through the
/// ordinary tenant query filters and adds no cross-business read path of its own, so the
/// cross-business deduplication is asserted over a context that already sees both businesses - which
/// the fixture names <c>UnscopedWorkListStore</c> so it cannot be mistaken for a request. The last
/// test here is the other half of that claim: under a business's own scope the same adapter answers
/// for that business and nothing else.</para>
/// </summary>
public class EfPriceCatalogueWorkListStoreTests
{
    #region Products awaiting a match

    /// <summary>
    /// The criterion: distinct <c>(search name, unit label)</c> pairs with their waiting key,
    /// deduplicated across businesses ignoring case and spacing, carrying no business id.
    ///
    /// <para>Three mappings go in - two spellings of one cola in two businesses, and one Pepsi - and
    /// two entries come out.</para>
    /// </summary>
    [Fact]
    public async Task Awaiting_match_pairs_deduplicate_across_businesses_ignoring_case_and_spacing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
        await fixture.TrackAsync(
            fixture.SecondBusinessId,
            fixture.SecondBusinessColaId,
            "coca-cola  classic",
            "375 ML");
        await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessPepsiId,
            "Pepsi Max",
            "375mL");

        var waiting = await fixture.UnscopedWorkListStore()
            .ListProductsAwaitingMatchAsync(CancellationToken.None);

        Assert.Equal(
            [
                PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"),
                PriceCatalogueWaitingKey.For("Pepsi Max", "375mL"),
            ],
            waiting.Select(entry => entry.WaitingKey).Order(StringComparer.Ordinal));

        // One spelling per key, chosen deterministically, so two reads of the work list send the same
        // search text rather than whichever business's row came back first.
        var cola = Assert.Single(
            waiting,
            entry => entry.WaitingKey == PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"));
        Assert.Equal("Coca-Cola Classic", cola.SearchName);
        Assert.Equal("375mL", cola.UnitLabel);
    }

    /// <summary>
    /// And the answer carries nothing else at all. Asserted on the record's own shape, because "no
    /// business identity" is a property of the contract rather than of one query: the only way a
    /// business id could reach the collector is if this type had somewhere to put one.
    /// </summary>
    [Fact]
    public void An_awaiting_match_entry_has_nowhere_to_carry_a_business_or_a_product()
    {
        var published = typeof(ProductAwaitingMatch)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["SearchName", "UnitLabel", "WaitingKey"], published);
    }

    /// <summary>
    /// A mapping that was looked for and not found is not sent again. Recording that answer is what
    /// stops the collector searching for it every run, so treating it as "still waiting" would make
    /// the record pointless.
    /// </summary>
    [Fact]
    public async Task A_mapping_that_was_looked_for_and_not_found_is_not_sent_again()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var mapping = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
        await fixture.MarkNotFoundAsync(mapping.Id);

        Assert.Empty(
            await fixture.UnscopedWorkListStore()
                .ListProductsAwaitingMatchAsync(CancellationToken.None));
    }

    /// <summary>And nor is one that has been matched: it belongs to the other read.</summary>
    [Fact]
    public async Task A_matched_mapping_is_not_waiting()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mapping = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
        await fixture.MatchAsync(mapping.Id, master);

        Assert.Empty(
            await fixture.UnscopedWorkListStore()
                .ListProductsAwaitingMatchAsync(CancellationToken.None));
    }

    /// <summary>
    /// An auto-matched mapping is not waiting either - it has a master product, however it got one -
    /// so the two matched states behave the same way here.
    /// </summary>
    [Fact]
    public async Task An_auto_matched_mapping_is_not_waiting()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mapping = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
        await fixture.MatchAsync(
            mapping.Id,
            master,
            ProductPriceTrackingStatus.AutoMatched,
            ProductPriceTrackingMatchSource.CollectorAutomaticMatch);

        Assert.Empty(
            await fixture.UnscopedWorkListStore()
                .ListProductsAwaitingMatchAsync(CancellationToken.None));
    }

    #endregion

    #region Tracked master products and their listings

    /// <summary>
    /// The criterion, stated as the issue states it: two businesses tracking the same master product
    /// produce <strong>one</strong> entry. The read names the master products the mappings point at
    /// rather than joining the mappings to the listings, which is what makes that true for the
    /// listings as well as for the product.
    /// </summary>
    [Fact]
    public async Task Two_businesses_tracking_one_master_product_produce_one_entry()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedListingAsync(master, "SKU-1", unitsPerPack: 30, barcode: "0012345678905", barcodeScope: BarcodeScope.Carton);

        var mine = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
        var theirs = await fixture.TrackAsync(
            fixture.SecondBusinessId,
            fixture.SecondBusinessColaId,
            "coca-cola  classic",
            "375 ML");
        await fixture.MatchAsync(mine.Id, master);
        await fixture.MatchAsync(theirs.Id, master);

        var tracked = Assert.Single(
            await fixture.UnscopedWorkListStore()
                .ListTrackedMasterProductsAsync(CancellationToken.None));

        Assert.Equal(master, tracked.Id);
        Assert.Equal("Coca-Cola", tracked.Brand);
        Assert.Equal("Classic", tracked.Name);
        Assert.Equal(375m, tracked.SizeValue);
        Assert.Equal(MasterProductSizeUnit.Millilitre, tracked.SizeUnit);

        // And its one listing exactly once, with the pack size and the barcode as published.
        var listing = Assert.Single(tracked.Listings);
        Assert.Equal("SKU-1", listing.SupplierProductId);
        Assert.Equal(30, listing.UnitsPerPack);
        Assert.Equal("0012345678905", listing.Barcode);
        Assert.Equal(BarcodeScope.Carton, listing.BarcodeScope);
    }

    /// <summary>
    /// Listings are deduplicated by <c>(supplier master record, supplier product id)</c>, which is
    /// what the unique index on those two columns means: two suppliers listing one product are two
    /// listings, and one supplier cannot list it twice.
    /// </summary>
    [Fact]
    public async Task A_tracked_master_product_carries_one_listing_per_supplier_and_supplier_product_id()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var otherSupplier = await fixture.SeedSupplierPresetAsync("Beta Grocer");
        await fixture.SeedListingAsync(master, "SKU-1");
        await fixture.SeedListingAsync(master, "SKU-1", supplierPresetId: otherSupplier);
        await fixture.SeedListingAsync(master, "SKU-2");

        var mapping = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
        await fixture.MatchAsync(mapping.Id, master);

        var tracked = Assert.Single(
            await fixture.UnscopedWorkListStore()
                .ListTrackedMasterProductsAsync(CancellationToken.None));

        Assert.Equal(
            [
                (fixture.SupplierPresetId, "SKU-1"),
                (fixture.SupplierPresetId, "SKU-2"),
                (otherSupplier, "SKU-1"),
            ],
            tracked.Listings
                .Select(listing => (listing.SupplierPresetId, listing.SupplierProductId))
                .Order());
    }

    /// <summary>
    /// A master product nobody tracks is not in the list: the work list is the products businesses
    /// asked about, not the whole catalogue.
    /// </summary>
    [Fact]
    public async Task A_master_product_no_business_tracks_is_not_in_the_list()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var untracked = await fixture.SeedMasterProductAsync("Pepsi", "Max");
        await fixture.SeedListingAsync(untracked, "SKU-9");

        Assert.Empty(
            await fixture.UnscopedWorkListStore()
                .ListTrackedMasterProductsAsync(CancellationToken.None));
    }

    /// <summary>
    /// A tracked master product with no listings yet is still an entry, with none - which is what the
    /// collector of #924 has to see in order to go and find one.
    /// </summary>
    [Fact]
    public async Task A_tracked_master_product_with_no_listings_is_an_entry_with_none()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mapping = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
        await fixture.MatchAsync(mapping.Id, master);

        var tracked = Assert.Single(
            await fixture.UnscopedWorkListStore()
                .ListTrackedMasterProductsAsync(CancellationToken.None));

        Assert.Empty(tracked.Listings);
    }

    /// <summary>
    /// A listing that is still held under a waiting key belongs to no master product and appears
    /// under none: it is a candidate the matching of #941 and #928 has yet to decide.
    /// </summary>
    [Fact]
    public async Task A_listing_still_held_under_a_waiting_key_is_attached_to_no_master_product()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mapping = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
        await fixture.MatchAsync(mapping.Id, master);
        await fixture.SeedWaitingListingAsync(mapping.WaitingKey, "SKU-WAITING");

        var tracked = Assert.Single(
            await fixture.UnscopedWorkListStore()
                .ListTrackedMasterProductsAsync(CancellationToken.None));

        Assert.Empty(tracked.Listings);
    }

    /// <summary>
    /// And the answer says nothing about who was tracking. Asserted on the record's shape for the
    /// reason the awaiting-match one is: a business id can only travel where a type has room for it.
    /// </summary>
    [Fact]
    public void A_tracked_master_product_has_nowhere_to_carry_a_business()
    {
        var published = typeof(TrackedMasterProduct)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Brand", "Id", "Listings", "Name", "SizeUnit", "SizeValue"], published);
    }

    #endregion

    #region The scope the adapter reads under

    /// <summary>
    /// The other half of this adapter's boundary claim: it reads the mappings through the ordinary
    /// tenant query filters and nothing else, so under a business's own scope it answers for that
    /// business alone - the other business's waiting products and tracked master products are not
    /// in either answer.
    ///
    /// <para>That is what makes the cross-business reads above a property of the <em>context</em>
    /// rather than of a second read path this issue added: there is no <c>IgnoreQueryFilters</c>, no
    /// raw SQL and no unscoped registration anywhere in it, and which caller may read the work list
    /// across businesses is #924's own reviewed decision.</para>
    /// </summary>
    [Fact]
    public async Task Under_one_business_s_scope_the_work_list_answers_for_that_business_alone()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var theirMaster = await fixture.SeedMasterProductAsync("Pepsi", "Max");
        await fixture.SeedListingAsync(theirMaster, "SKU-9");

        // The second business has one waiting product and one matched one; the first has nothing.
        await fixture.TrackAsync(
            fixture.SecondBusinessId,
            fixture.SecondBusinessColaId,
            "coca-cola  classic",
            "375 ML");
        var theirPepsi = await fixture.SeedProductAsync(fixture.SecondBusinessId, "Pepsi Max", "375mL");
        var theirMapping = await fixture.TrackAsync(
            fixture.SecondBusinessId,
            theirPepsi,
            "Pepsi Max",
            "375mL");
        await fixture.MatchAsync(theirMapping.Id, theirMaster);

        var mine = fixture.WorkListStoreFor(fixture.FirstBusinessId);
        Assert.Empty(await mine.ListProductsAwaitingMatchAsync(CancellationToken.None));
        Assert.Empty(await mine.ListTrackedMasterProductsAsync(CancellationToken.None));

        // And the second business sees its own, which is what proves the emptiness above is the
        // filter and not an empty database.
        var theirs = fixture.WorkListStoreFor(fixture.SecondBusinessId);
        Assert.Single(await theirs.ListProductsAwaitingMatchAsync(CancellationToken.None));
        Assert.Single(await theirs.ListTrackedMasterProductsAsync(CancellationToken.None));
    }

    #endregion
}
