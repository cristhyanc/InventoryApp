using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The relational half of the cheapest-supplier summary read (issue #930, parent #817): which rows
/// the adapter may reach, and which it may not.
///
/// <para>Relational SQLite over the real migration history and two businesses throughout, as
/// AGENTS.md § Tenant ownership requires: the claims here are that a product of the other business
/// is not in the answer, that a caller whose business did not resolve reads nothing, and that the
/// platform catalogue is reached only through the caller's <em>own</em> matched mapping. None of
/// those can be stated on a one-business fixture, and none of them is provable on an InMemory
/// provider, which has neither the central query filters' SQL nor the foreign keys underneath
/// them.</para>
///
/// <para>The comparison itself is not under test here: that is
/// <c>CheapestUnitOfferPolicyTests</c> and <c>CheapestSupplierSummaryTests</c>, and the whole
/// pipeline is <c>CheapestSupplierSummaryApiTests</c>. What this file is about is the statements.</para>
/// </summary>
public class EfCheapestSupplierSummaryStoreTests
{
    [Fact]
    public async Task Only_the_callers_own_products_are_answered()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var facts = await fixture.SummaryStoreFor(fixture.FirstBusinessId).ReadAsync(
            BusinessId.From(fixture.FirstBusinessId),
            [fixture.FirstBusinessColaId, fixture.SecondBusinessColaId],
            CancellationToken.None);

        Assert.Equal(
            [fixture.FirstBusinessColaId],
            facts.Products.Select(product => product.ProductId));
    }

    /// <summary>
    /// Fail closed: a caller whose business did not resolve reads no product, no mapping and no
    /// supplier - the central filters match none of their rows, and this adapter adds no way round
    /// them.
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_resolved_business_reads_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.SeedSupplierAsync(
            fixture.FirstBusinessId, "Alpha Grocer", fixture.SupplierPresetId);

        var facts = await fixture.DeniedSummaryStore().ReadAsync(
            BusinessId.From(fixture.FirstBusinessId),
            [fixture.FirstBusinessColaId],
            CancellationToken.None);

        Assert.Empty(facts.Products);
        Assert.Empty(facts.Suppliers);
    }

    /// <summary>
    /// The criterion: only the business's own linked, not-hidden suppliers take part. A supplier it
    /// typed in itself has no catalogue record to follow, and one it has switched out of use is one
    /// it has said it does not buy from.
    /// </summary>
    [Fact]
    public async Task Only_linked_not_hidden_suppliers_are_read()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var hiddenPreset = await fixture.SeedSupplierPresetAsync("Beta Grocer");
        await fixture.SeedSupplierAsync(
            fixture.FirstBusinessId, "Alpha Grocer", fixture.SupplierPresetId);
        await fixture.SeedSupplierAsync(
            fixture.FirstBusinessId, "Beta Grocer", hiddenPreset, hidden: true);
        await fixture.SeedSupplierAsync(fixture.FirstBusinessId, "Corner Shop");

        // The other business's own linked copy of the same platform record, which must not appear.
        await fixture.SeedSupplierAsync(
            fixture.SecondBusinessId, "Their Alpha Grocer", fixture.SupplierPresetId);

        var facts = await fixture.SummaryStoreFor(fixture.FirstBusinessId).ReadAsync(
            BusinessId.From(fixture.FirstBusinessId),
            [fixture.FirstBusinessColaId],
            CancellationToken.None);

        Assert.Equal(["Alpha Grocer"], facts.Suppliers.Select(supplier => supplier.Name));
    }

    /// <summary>
    /// The way into the shared catalogue is the caller's own matched mapping: a listing is read
    /// only for a master product that mapping names, with the listing's current price and its
    /// supplier's own freshness limit beside it.
    /// </summary>
    [Fact]
    public async Task A_matched_mapping_reads_its_master_products_listings_with_their_prices()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedSupplierAsync(
            fixture.FirstBusinessId, "Alpha Grocer", fixture.SupplierPresetId);

        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var masterProductId = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.MatchAsync(mapping.Id, masterProductId);

        var listingId = await fixture.SeedListingAsync(
            masterProductId, "ALPHA-COLA-24", unitsPerPack: 24);
        await fixture.ConfirmListingAsync(listingId);
        var runId = await fixture.SeedCollectorRunAsync();
        await fixture.SeedObservationAsync(listingId, runId, packPrice: 22.80m);

        var facts = await fixture.SummaryStoreFor(fixture.FirstBusinessId).ReadAsync(
            BusinessId.From(fixture.FirstBusinessId),
            [fixture.FirstBusinessColaId],
            CancellationToken.None);

        var listing = Assert.Single(Assert.Single(facts.Products).Listings);
        Assert.Equal(listingId, listing.SupplierListingId);
        Assert.True(listing.ListingConfirmed);
        Assert.Equal(24, listing.UnitsPerPack);
        Assert.Equal(22.80m, listing.Price!.PackPrice);
        Assert.Equal(SupplierPriceBasis.InclusiveOfGst, listing.Price.PriceBasis);
        Assert.Equal(
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            listing.Price.FreshnessLimitHours);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, listing.LastCheckedAtUtc);
    }

    /// <summary>
    /// A listing nobody has checked yet is read with no price at all. A missing check is not a
    /// missing price and never a price of zero (issue #925).
    /// </summary>
    [Fact]
    public async Task A_listing_with_no_observation_is_read_with_no_price()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedSupplierAsync(
            fixture.FirstBusinessId, "Alpha Grocer", fixture.SupplierPresetId);

        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var masterProductId = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.MatchAsync(mapping.Id, masterProductId);
        await fixture.SeedListingAsync(masterProductId, "ALPHA-COLA-24", unitsPerPack: 24);

        var facts = await fixture.SummaryStoreFor(fixture.FirstBusinessId).ReadAsync(
            BusinessId.From(fixture.FirstBusinessId),
            [fixture.FirstBusinessColaId],
            CancellationToken.None);

        var listing = Assert.Single(Assert.Single(facts.Products).Listings);
        Assert.Null(listing.Price);
        Assert.Null(listing.LastCheckedAtUtc);
    }

    /// <summary>
    /// The price read is the one the listing <em>points at</em>, not the newest row in the
    /// append-only table: the cursor is what a successful check moves, and a later observation that
    /// was never accepted must not become this listing's price.
    /// </summary>
    [Fact]
    public async Task The_price_read_is_the_observation_the_listing_points_at()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedSupplierAsync(
            fixture.FirstBusinessId, "Alpha Grocer", fixture.SupplierPresetId);

        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var masterProductId = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.MatchAsync(mapping.Id, masterProductId);
        var listingId = await fixture.SeedListingAsync(
            masterProductId, "ALPHA-COLA-24", unitsPerPack: 24);
        var runId = await fixture.SeedCollectorRunAsync();

        await fixture.SeedObservationAsync(listingId, runId, packPrice: 22.80m);
        await fixture.SeedObservationAsync(
            listingId,
            runId,
            packPrice: 19.00m,
            observedAtUtc: PriceCatalogueFixture.CreatedAt.AddHours(1),
            pointListingAtIt: false);

        var facts = await fixture.SummaryStoreFor(fixture.FirstBusinessId).ReadAsync(
            BusinessId.From(fixture.FirstBusinessId),
            [fixture.FirstBusinessColaId],
            CancellationToken.None);

        Assert.Equal(22.80m, Assert.Single(Assert.Single(facts.Products).Listings).Price!.PackPrice);
    }

    /// <summary>
    /// A mapping that is not matched names no master product, so no listing is read for it - the
    /// catalogue is unreachable until the caller's own mapping points into it.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.Requested)]
    [InlineData(ProductPriceTrackingStatus.NotFound)]
    public async Task An_unmatched_mapping_reads_no_listings(ProductPriceTrackingStatus status)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedSupplierAsync(
            fixture.FirstBusinessId, "Alpha Grocer", fixture.SupplierPresetId);

        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var masterProductId = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var listingId = await fixture.SeedListingAsync(
            masterProductId, "ALPHA-COLA-24", unitsPerPack: 24);
        await fixture.ConfirmListingAsync(listingId);
        var runId = await fixture.SeedCollectorRunAsync();
        await fixture.SeedObservationAsync(listingId, runId, packPrice: 22.80m);

        if (status == ProductPriceTrackingStatus.NotFound)
        {
            await fixture.MarkNotFoundAsync(mapping.Id);
        }

        var facts = await fixture.SummaryStoreFor(fixture.FirstBusinessId).ReadAsync(
            BusinessId.From(fixture.FirstBusinessId),
            [fixture.FirstBusinessColaId],
            CancellationToken.None);

        var product = Assert.Single(facts.Products);
        Assert.Equal(status, product.TrackingStatus);
        Assert.Empty(product.Listings);
    }

    /// <summary>
    /// The basis fact is the caller's own business row, read by the key tenancy resolution
    /// produced. A business that has recorded nothing is not registered, which shows it the price
    /// it actually hands over rather than one a tenth lower.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public async Task The_businesss_own_gst_registration_is_read(bool? recorded, bool expected)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SetGstRegisteredAsync(fixture.FirstBusinessId, recorded);
        await fixture.SetGstRegisteredAsync(fixture.SecondBusinessId, !expected);

        var facts = await fixture.SummaryStoreFor(fixture.FirstBusinessId).ReadAsync(
            BusinessId.From(fixture.FirstBusinessId),
            [fixture.FirstBusinessColaId],
            CancellationToken.None);

        Assert.Equal(expected, facts.BusinessIsGstRegistered);
    }
}
