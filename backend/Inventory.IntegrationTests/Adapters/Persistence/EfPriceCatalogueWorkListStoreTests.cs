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
/// <para><strong>What is read, and by what.</strong> Since issue #924 the adapter makes the reviewed
/// cross-business read of the mappings' business-free text columns, so the deduplication is asserted
/// over the scope a collector request actually carries - a <em>denied</em> one, which the fixture
/// names <c>DeniedWorkListStore</c>. The last test here is the other half of that claim: what the
/// read may project is a product description and nothing that says whose product it is, and the
/// answer is the same whatever scope the context happens to carry, because what bounds this read is
/// the authorization on the one endpoint that reaches it (docs/architecture.md § Collector work
/// list).</para>
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
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessPepsiId);

        var waiting = await fixture.DeniedWorkListStore()
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
    /// The other direction of the same criterion: two products whose search text differs only in
    /// which side of the part boundary a separator character falls on are two searches, not one.
    ///
    /// <para>A product name is free text, so <c>"Cola|"</c> in <c>"375mL"</c> and <c>"Cola"</c> in
    /// <c>"|375mL"</c> are both text a business may legitimately hold. Under the escaping rule
    /// replaced on PR #945 both folded to one waiting key, so this work list would have sent one
    /// search and priced one of the two products as the other.</para>
    /// </summary>
    [Fact]
    public async Task Search_texts_differing_only_at_a_part_boundary_are_two_searches()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        // Two products of this business whose own names and labels straddle the part boundary. The
        // search text is the product's since issue #938, so the case is expressed by seeding the
        // products that hold it rather than by passing text to the write.
        var trailing = await fixture.SeedProductAsync(fixture.FirstBusinessId, "Cola|", "375mL");
        var leading = await fixture.SeedProductAsync(fixture.FirstBusinessId, "Cola", "|375mL");
        await fixture.TrackAsync(fixture.FirstBusinessId, trailing);
        await fixture.TrackAsync(fixture.FirstBusinessId, leading);

        var waiting = await fixture.DeniedWorkListStore()
            .ListProductsAwaitingMatchAsync(CancellationToken.None);

        Assert.Equal(
            [
                PriceCatalogueWaitingKey.For("Cola", "|375mL"),
                PriceCatalogueWaitingKey.For("Cola|", "375mL"),
            ],
            waiting.Select(entry => entry.WaitingKey).Order(StringComparer.Ordinal));
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
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MarkNotFoundAsync(mapping.Id);

        Assert.Empty(
            await fixture.DeniedWorkListStore()
                .ListProductsAwaitingMatchAsync(CancellationToken.None));
    }

    /// <summary>And nor is one that has been matched: it belongs to the other read.</summary>
    [Fact]
    public async Task A_matched_mapping_is_not_waiting()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master);

        Assert.Empty(
            await fixture.DeniedWorkListStore()
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
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(
            mapping.Id,
            master,
            ProductPriceTrackingStatus.AutoMatched,
            ProductPriceTrackingMatchSource.CollectorAutomaticMatch);

        Assert.Empty(
            await fixture.DeniedWorkListStore()
                .ListProductsAwaitingMatchAsync(CancellationToken.None));
    }

    /// <summary>
    /// And nor is a mapping that was <strong>suspended</strong> because its product's unit label was
    /// replaced by the generic one (issue #938, repaired on review): it still holds the text it was
    /// matched or waiting under - the size the product no longer has - and sending that would be
    /// asking the collector to price a product the business does not have. It is waiting on its own
    /// product's details, not on the catalogue.
    ///
    /// <para>The suspension is arranged through the <strong>real</strong> product store, because that
    /// is where it happens: writing the status directly would prove only that this query filters on
    /// it.</para>
    /// </summary>
    [Fact]
    public async Task A_suspended_mapping_is_sent_to_neither_collector_read()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedListingAsync(master, "SKU-SUSPENDED");
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master);

        await fixture.UpdateProductAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "unit");

        var store = fixture.UnscopedWorkListStore();
        Assert.Empty(await store.ListProductsAwaitingMatchAsync(CancellationToken.None));
        Assert.Empty(await store.ListTrackedMasterProductsAsync(CancellationToken.None));
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

        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        await fixture.MatchAsync(mine.Id, master);
        await fixture.MatchAsync(theirs.Id, master);

        var tracked = Assert.Single(
            await fixture.DeniedWorkListStore()
                .ListTrackedMasterProductsAsync(CancellationToken.None));

        Assert.Equal(master, tracked.Id);
        Assert.Equal("Coca-Cola", tracked.Brand);
        Assert.Equal("Classic", tracked.Name);
        Assert.Equal(375m, tracked.SizeValue);
        Assert.Equal(MasterProductSizeUnit.Millilitre, tracked.SizeUnit);

        // And its one listing exactly once, with the pack size and the barcode as published - read
        // for the products a run is issued for, which is where a listing travels from since the
        // response bounds of #924.
        var listing = Assert.Single(await ListingsOfAsync(fixture, master));
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

        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master);

        Assert.Single(
            await fixture.DeniedWorkListStore()
                .ListTrackedMasterProductsAsync(CancellationToken.None));

        Assert.Equal(
            [
                (fixture.SupplierPresetId, "SKU-1"),
                (fixture.SupplierPresetId, "SKU-2"),
                (otherSupplier, "SKU-1"),
            ],
            (await ListingsOfAsync(fixture, master))
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
            await fixture.DeniedWorkListStore()
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
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master);

        Assert.Single(
            await fixture.DeniedWorkListStore()
                .ListTrackedMasterProductsAsync(CancellationToken.None));

        Assert.Empty(await ListingsOfAsync(fixture, master));
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
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master);
        await fixture.SeedWaitingListingAsync(mapping.WaitingKey, "SKU-WAITING");

        Assert.Single(
            await fixture.DeniedWorkListStore()
                .ListTrackedMasterProductsAsync(CancellationToken.None));

        Assert.Empty(await ListingsOfAsync(fixture, master));
    }

    /// <summary>
    /// The tracked products are ordered by their own key, which is what a bounded run's rotation
    /// continues from (issue #924): the cursor the next run starts after is a stored master product
    /// id, so "the product after the one we stopped at" only means something while the issue order is
    /// the id order.
    /// </summary>
    [Fact]
    public async Task Tracked_master_products_are_ordered_by_their_own_key()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        // Seeded so the row order and the comparison-key order disagree: "Zephyr" is created first
        // and sorts last by brand.
        var last = await fixture.SeedMasterProductAsync("Zephyr", "Cola");
        var first = await fixture.SeedMasterProductAsync("Alpine", "Water");

        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var theirs = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessPepsiId);
        await fixture.MatchAsync(mine.Id, last);
        await fixture.MatchAsync(theirs.Id, first);

        Assert.Equal(
            [last, first],
            (await fixture.DeniedWorkListStore().ListTrackedMasterProductsAsync(CancellationToken.None))
                .Select(product => product.Id));
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

        Assert.Equal(["Brand", "Id", "Name", "SizeUnit", "SizeValue"], published);
    }

    #endregion

    #region The context reads are bounded per product (issue #924)

    /// <summary>
    /// A product holding more listings than the bound allows comes back as "beyond the bound" with
    /// <strong>none</strong> of its listings, and a product inside the bound comes back with all of
    /// them. The prefix is deliberately not sent: an unflagged prefix would read as the product's
    /// whole catalogue entry.
    /// </summary>
    [Fact]
    public async Task A_product_with_more_listings_than_the_bound_is_answered_as_beyond_it()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var crowded = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var ordinary = await fixture.SeedMasterProductAsync("Pepsi", "Max");
        await fixture.SeedListingsAsync(crowded, count: 4);
        await fixture.SeedListingsAsync(ordinary, count: 3);

        var bounded = await fixture.DeniedWorkListStore().ListListingsForMasterProductsAsync(
            [crowded, ordinary],
            perProductLimit: 3,
            CancellationToken.None);

        Assert.Equal([crowded], bounded.MasterProductsBeyondLimit);
        Assert.DoesNotContain(crowded, bounded.ByMasterProduct.Keys);
        Assert.Equal(3, bounded.ByMasterProduct[ordinary].Count);
    }

    /// <summary>The same bound over a waiting key's proposals.</summary>
    [Fact]
    public async Task A_waiting_key_with_more_proposals_than_the_bound_is_answered_as_beyond_it()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var crowded = PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL");
        var ordinary = PriceCatalogueWaitingKey.For("Pepsi Max", "375mL");

        for (var index = 0; index < 4; index++)
        {
            await fixture.SeedWaitingListingAsync(crowded, $"SKU-CROWDED-{index}");
        }

        await fixture.SeedWaitingListingAsync(ordinary, "SKU-ORDINARY");

        var bounded = await fixture.DeniedWorkListStore().ListWaitingListingsAsync(
            [crowded, ordinary],
            perKeyLimit: 3,
            CancellationToken.None);

        Assert.Equal([crowded], bounded.WaitingKeysBeyondLimit);
        Assert.DoesNotContain(crowded, bounded.ByWaitingKey.Keys);
        Assert.Single(bounded.ByWaitingKey[ordinary]);
    }

    /// <summary>
    /// And over a key's refusals, where the stakes are different: a refusal list is the safeguard
    /// that stops a matcher re-proposing a pair somebody rejected, so a key beyond the bound comes
    /// back with none of its refusals and is never answered with a prefix of them.
    ///
    /// <para>One pair refused twice - rejected once and undone once - counts once, which is why the
    /// count is taken over the distinct pairs: counting it twice could put a key beyond a bound it is
    /// inside.</para>
    /// </summary>
    [Fact]
    public async Task A_waiting_key_with_more_refusals_than_the_bound_is_answered_as_beyond_it()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var crowded = PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL");
        var ordinary = PriceCatalogueWaitingKey.For("Pepsi Max", "375mL");

        foreach (var index in Enumerable.Range(0, 4))
        {
            var refused = await fixture.SeedMasterProductAsync("Refused", $"Variant {index}");
            await fixture.SeedMatchSuggestionAsync(
                crowded, refused, ProductMatchSuggestionStatus.Rejected);
        }

        // Refused twice, which is one refusal: the only key inside the bound stays inside it.
        var twice = await fixture.SeedMasterProductAsync("Refused", "Twice");
        await fixture.SeedMatchSuggestionAsync(ordinary, twice, ProductMatchSuggestionStatus.Rejected);
        await fixture.SeedMatchSuggestionAsync(ordinary, twice, ProductMatchSuggestionStatus.Undone);

        var bounded = await fixture.DeniedWorkListStore().ListRefusedMasterProductsAsync(
            [crowded, ordinary],
            perKeyLimit: 3,
            CancellationToken.None);

        Assert.Equal([crowded], bounded.WaitingKeysBeyondLimit);
        Assert.Equal(
            [new RefusedMasterProduct(ordinary, twice)],
            bounded.Refusals);
    }

    private static async Task<IReadOnlyList<TrackedSupplierListing>> ListingsOfAsync(
        PriceCatalogueFixture fixture,
        long masterProductId)
    {
        var bounded = await fixture.DeniedWorkListStore().ListListingsForMasterProductsAsync(
            [masterProductId],
            PriceCollectorWorkListPolicy.MaxListingsPerProduct,
            CancellationToken.None);

        return bounded.ByMasterProduct.TryGetValue(masterProductId, out var own) ? own : [];
    }

    #endregion

    #region The scope the adapter reads under (issue #924)

    /// <summary>
    /// The reviewed cross-business read of issue #924, stated as the property that matters: the work
    /// list answers the same thing under the <em>denied</em> scope a collector request carries as it
    /// does on a context with no filter at all.
    ///
    /// <para>Before #924 the mappings were read through the ordinary tenant filters, which under a
    /// denied scope match nothing - so the work list a collector was handed would have been silently
    /// empty, which is the one failure mode a bounded response must not have. The two mapping reads
    /// therefore ignore the filter, over the projection the port declares and no wider: a waiting key,
    /// a search name, a unit label and a master product id. What bounds the read is the authorization
    /// on the one endpoint that reaches it, not the context it was given, and that is exactly what
    /// this asserts.</para>
    /// </summary>
    [Fact]
    public async Task The_work_list_answers_across_businesses_under_the_denied_scope_a_collector_carries()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var theirMaster = await fixture.SeedMasterProductAsync("Pepsi", "Max");
        await fixture.SeedListingAsync(theirMaster, "SKU-9");

        // One waiting product in each business, spelled differently, and one matched product in the
        // second - so an answer that respected the filters could only ever be a subset of this.
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        var theirPepsi = await fixture.SeedProductAsync(fixture.SecondBusinessId, "Pepsi Max", "375mL");
        var theirMapping = await fixture.TrackAsync(fixture.SecondBusinessId, theirPepsi);
        await fixture.MatchAsync(theirMapping.Id, theirMaster);

        var denied = fixture.DeniedWorkListStore();
        var unfiltered = fixture.UnscopedWorkListStore();

        // One waiting entry, because the two spellings fold to one key, and one tracked product.
        var waitingUnderDeniedScope = await denied.ListProductsAwaitingMatchAsync(CancellationToken.None);
        var trackedUnderDeniedScope = await denied.ListTrackedMasterProductsAsync(CancellationToken.None);

        Assert.Single(waitingUnderDeniedScope);
        Assert.Single(trackedUnderDeniedScope);

        Assert.Equal(
            await unfiltered.ListProductsAwaitingMatchAsync(CancellationToken.None),
            waitingUnderDeniedScope);
        Assert.Equal(
            (await unfiltered.ListTrackedMasterProductsAsync(CancellationToken.None))
                .Select(product => product.Id),
            trackedUnderDeniedScope.Select(product => product.Id));
    }

    /// <summary>
    /// A business's own scope reads the same work list, which is the companion fact to the one above
    /// and is what makes the read's safety a property of the <em>projection</em> rather than of the
    /// scope it happens to run under.
    ///
    /// <para>It is deliberately not a leak: no business endpoint reaches this adapter at all - the one
    /// endpoint that does is <c>GET api/price-collector/work</c>, which is class 5 and refuses every
    /// delegated user token - and what the answer carries is a product description with nowhere for a
    /// business to be, which the two shape tests above assert.</para>
    /// </summary>
    [Fact]
    public async Task The_work_list_is_the_same_whatever_scope_the_context_carries()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var theirs = fixture.WorkListStoreFor(fixture.SecondBusinessId);
        var someoneElses = fixture.WorkListStoreFor(fixture.FirstBusinessId);

        var waiting = await theirs.ListProductsAwaitingMatchAsync(CancellationToken.None);

        Assert.Single(waiting);
        Assert.Equal(
            waiting,
            await someoneElses.ListProductsAwaitingMatchAsync(CancellationToken.None));
    }

    #endregion
}
