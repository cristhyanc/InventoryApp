using System.Text.Json;
using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The Super Admin's read of the shared master-product catalogue (issue #1023, parent #817) over the
/// real adapter, the real migration history and <strong>two</strong> businesses, on the
/// <strong>denied</strong> business scope a platform-admin request carries.
///
/// <para>Two businesses throughout, as AGENTS.md § Tenant ownership requires of any change that
/// touches a cross-business surface: both of them track products here, so the decisive property - that
/// a catalogue read publishes supplier-listing counts and nothing whatever about a business - is
/// asserted against a database where there <em>is</em> tenant data to leak. Relational SQLite over the
/// real migrations rather than InMemory, because what is under test is SQL: the ordering, the
/// case-insensitive <c>LIKE</c>, the slice and the counts.</para>
///
/// <para>The denied scope is the shape that matters. Unlike the review store beside it, this adapter
/// reads only platform tables, so it needs no filter-ignoring statement and no write lease: a denied
/// scope sees the whole catalogue because a platform table carries no business key, and sees none of
/// either business's own rows because the central filters still match none of them.</para>
/// </summary>
public class MasterProductCatalogueStoreTests
{
    private static readonly DateTime Observed = new(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc);

    #region The listing

    /// <summary>
    /// The issue's own ordering: brand, then name, then size, then the row's own key - which is what
    /// makes paging over the catalogue stable, because two products that agree on the first three
    /// still come out in the same order on every read.
    /// </summary>
    [Fact]
    public async Task The_catalogue_is_ordered_by_brand_then_name_then_size_then_key()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var pepsi = await fixture.SeedMasterProductAsync("Pepsi", "Max");
        var colaLarge = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic", 600m);
        var colaSmall = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic", 375m);
        var colaZero = await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar", 375m);

        var page = await ListAsync(fixture);

        Assert.Equal(
            [colaSmall, colaLarge, colaZero, pepsi],
            page.Products.Select(product => product.Id).ToArray());
    }

    /// <summary>
    /// The search is a case-insensitive substring of the brand <em>or</em> the name, which is what a
    /// person typing "cola" or "zero" expects - and the total is how many it matched, not how many the
    /// page returned.
    /// </summary>
    [Theory]
    [InlineData("coca", 2)]
    [InlineData("COCA-COLA", 2)]
    [InlineData("zero", 1)]
    [InlineData("SUGAR", 1)]
    [InlineData("pepsi", 1)]
    [InlineData("nothing here", 0)]
    public async Task A_search_matches_the_brand_or_the_name_ignoring_case(string search, int matches)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar");
        await fixture.SeedMasterProductAsync("Pepsi", "Max");

        var page = await ListAsync(fixture, search);

        Assert.Equal(matches, page.TotalCount);
        Assert.Equal(matches, page.Products.Count);
    }

    /// <summary>
    /// A character <c>LIKE</c> treats specially is matched literally: a person who types <c>%</c> is
    /// searching for a product whose description contains a percent sign, not asking for the whole
    /// catalogue.
    /// </summary>
    [Fact]
    public async Task A_typed_wildcard_is_matched_literally()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var extra = await fixture.SeedMasterProductAsync("Bundaberg", "Ginger Beer 10% Extra");

        var page = await ListAsync(fixture, "10%");

        Assert.Equal(extra, Assert.Single(page.Products).Id);
    }

    /// <summary>
    /// A page is the slice the filter asked for, and the total is the whole match: three products
    /// read two at a time are two pages, and the second knows it is the last.
    /// </summary>
    [Fact]
    public async Task A_page_is_the_requested_slice_and_the_total_is_the_whole_match()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var first = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var second = await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar");
        var third = await fixture.SeedMasterProductAsync("Pepsi", "Max");

        var firstPage = await fixture.DeniedMasterProductStore().ListAsync(
            new MasterProductCatalogueFilter(null, Skip: 0, Take: 2),
            CancellationToken.None);
        var secondPage = await fixture.DeniedMasterProductStore().ListAsync(
            new MasterProductCatalogueFilter(null, Skip: 2, Take: 2),
            CancellationToken.None);

        Assert.Equal([first, second], firstPage.Products.Select(product => product.Id).ToArray());
        Assert.Equal([third], secondPage.Products.Select(product => product.Id).ToArray());
        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal(3, secondPage.TotalCount);
    }

    /// <summary>
    /// The counts the issue asks for: how many supplier listings the product has in each match
    /// status, the refused ones included - a refusal is kept rather than deleted, and a product with
    /// nothing but refusals must not read as a product nobody has ever looked up.
    ///
    /// <para>A listing still held under a waiting key belongs to no master product and is counted
    /// against none, which is the exclusive-ownership rule of #923 showing through the count.</para>
    /// </summary>
    [Fact]
    public async Task Each_product_reports_its_supplier_listing_counts_by_match_status()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var pepsi = await fixture.SeedMasterProductAsync("Pepsi", "Max");

        await fixture.SeedDescribedListingAsync(cola, "SKU-1", SupplierListingMatchStatus.Proposed);
        await fixture.SeedDescribedListingAsync(cola, "SKU-2", SupplierListingMatchStatus.Proposed);
        await fixture.SeedDescribedListingAsync(cola, "SKU-3", SupplierListingMatchStatus.Confirmed);
        await fixture.SeedDescribedListingAsync(cola, "SKU-4", SupplierListingMatchStatus.NeedsReview);
        await fixture.SeedDescribedListingAsync(cola, "SKU-5", SupplierListingMatchStatus.Rejected);
        await fixture.SeedWaitingListingAsync(
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"),
            "SKU-WAITING");

        var page = await ListAsync(fixture);

        var colaCounts = page.Products.Single(product => product.Id == cola).ListingCounts;
        Assert.Equal(2, colaCounts.Proposed);
        Assert.Equal(1, colaCounts.Confirmed);
        Assert.Equal(1, colaCounts.NeedsReview);
        Assert.Equal(1, colaCounts.Rejected);
        Assert.Equal(5, colaCounts.Total);

        // A product nothing has listed yet reports zeros rather than being left out of the catalogue.
        var pepsiCounts = page.Products.Single(product => product.Id == pepsi).ListingCounts;
        Assert.Equal(0, pepsiCounts.Total);
    }

    /// <summary>
    /// The four values that say what a product is, and both of its instants - including the
    /// <c>UpdatedAtUtc</c> that equals the creation instant until the first edit, which is how "never
    /// edited" is readable without a nullable column.
    /// </summary>
    [Fact]
    public async Task A_product_reports_its_description_and_both_of_its_instants()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre);

        var product = Assert.Single((await ListAsync(fixture)).Products);

        Assert.Equal("Coca-Cola", product.Brand);
        Assert.Equal("Classic", product.Name);
        Assert.Equal(375m, product.SizeValue);
        Assert.Equal(MasterProductSizeUnit.Millilitre, product.SizeUnit);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, product.CreatedAtUtc);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, product.UpdatedAtUtc);
    }

    #endregion

    #region One product and its listings

    /// <summary>
    /// The detail the issue asks for: the supplier's own name, its product id, its wording, the pack
    /// it describes, the match status, the page it is on, when it was last checked and the pack price
    /// of the reading it currently points at - with its currency, its tax basis and the instant it was
    /// observed, because an amount without those three is not a price anybody can read.
    ///
    /// <para>A listing that has never been checked carries no price at all, which is a state and not a
    /// gap: <see langword="null"/> rather than zero, for the reason the whole catalogue keeps unknown
    /// unknown.</para>
    /// </summary>
    [Fact]
    public async Task The_detail_reports_each_listing_with_its_supplier_and_its_current_price()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var runId = await fixture.SeedRunAsync(Observed, Observed.AddHours(6));

        var priced = await fixture.SeedDescribedListingAsync(
            cola,
            "SKU-COLA-30",
            SupplierListingMatchStatus.Confirmed,
            title: "Alpha Grocer Coca-Cola Classic 375mL 30 pack",
            url: "https://alpha.example/cola",
            sizeValue: 375m,
            sizeUnit: MasterProductSizeUnit.Millilitre,
            unitsPerPack: 30);
        await fixture.SeedAcceptedPriceAsync(priced, runId, 42.50m, Observed);

        var neverChecked = await fixture.SeedDescribedListingAsync(
            cola,
            "SKU-COLA-SINGLE",
            SupplierListingMatchStatus.Proposed);

        var detail = await fixture.DeniedMasterProductStore().FindAsync(cola, CancellationToken.None);

        Assert.Equal("Coca-Cola", detail!.Product.Brand);
        Assert.Equal(2, detail.Listings.Count);

        var listing = detail.Listings.Single(candidate => candidate.Id == priced);
        Assert.Equal("Alpha Grocer", listing.SupplierName);
        Assert.Equal("SKU-COLA-30", listing.SupplierProductId);
        Assert.Equal("Alpha Grocer Coca-Cola Classic 375mL 30 pack", listing.Title);
        Assert.Equal(375m, listing.SizeValue);
        Assert.Equal(MasterProductSizeUnit.Millilitre, listing.SizeUnit);
        Assert.Equal(30, listing.UnitsPerPack);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
        Assert.Equal("https://alpha.example/cola", listing.Url);
        Assert.Equal(Observed, listing.LastCheckedAtUtc);
        Assert.Equal(42.50m, listing.LatestPrice!.PackPrice);
        Assert.Equal(SupplierPriceObservationPolicy.Currency, listing.LatestPrice.Currency);
        Assert.Equal(SupplierPriceBasis.InclusiveOfGst, listing.LatestPrice.PriceBasis);
        Assert.Equal(Observed, listing.LatestPrice.ObservedAtUtc);

        var never = detail.Listings.Single(candidate => candidate.Id == neverChecked);
        Assert.Null(never.LatestPrice);
        Assert.Null(never.LastCheckedAtUtc);
    }

    /// <summary>
    /// The listings of one product come out in a fixed order - the supplier's name, then the
    /// supplier's own product id, then the row's key - so the detail reads the same way on every
    /// request rather than in whatever order the database answered.
    /// </summary>
    [Fact]
    public async Task The_listings_are_ordered_by_supplier_then_supplier_product_id()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var zeta = await fixture.SeedSupplierPresetAsync("Zeta Wholesale");

        var zetaListing = await fixture.SeedDescribedListingAsync(
            cola,
            "SKU-A",
            supplierPresetId: zeta);
        var alphaSecond = await fixture.SeedDescribedListingAsync(cola, "SKU-B");
        var alphaFirst = await fixture.SeedDescribedListingAsync(cola, "SKU-A");

        var detail = await fixture.DeniedMasterProductStore().FindAsync(cola, CancellationToken.None);

        Assert.Equal(
            [alphaFirst, alphaSecond, zetaListing],
            detail!.Listings.Select(listing => listing.Id).ToArray());
    }

    /// <summary>
    /// The detail carries the same counts the listing does, so a page that opened one product does not
    /// have to re-read the catalogue to say how its listings stand.
    /// </summary>
    [Fact]
    public async Task The_detail_carries_the_products_own_listing_counts()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedDescribedListingAsync(cola, "SKU-1", SupplierListingMatchStatus.Confirmed);
        await fixture.SeedDescribedListingAsync(cola, "SKU-2", SupplierListingMatchStatus.Rejected);

        var detail = await fixture.DeniedMasterProductStore().FindAsync(cola, CancellationToken.None);

        Assert.Equal(1, detail!.Product.ListingCounts.Confirmed);
        Assert.Equal(1, detail.Product.ListingCounts.Rejected);
        Assert.Equal(2, detail.Product.ListingCounts.Total);
    }

    /// <summary>An id no master product has is nothing, which the endpoint reports as <c>404</c>.</summary>
    [Fact]
    public async Task An_unknown_master_product_is_not_found()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        Assert.Null(await fixture.DeniedMasterProductStore().FindAsync(9_999, CancellationToken.None));
    }

    #endregion

    #region Nothing about a business

    /// <summary>
    /// The decisive assertion of this issue, over a database where both businesses track products:
    /// <strong>no business id, business name or business product id appears in either answer, and
    /// nothing tenant-owned is touched at all.</strong>
    ///
    /// <para>It is asserted over the serialized answers rather than property by property, because what
    /// the issue forbids is the data travelling at all - and over the tenant-owned fingerprint as
    /// well, so a read that quietly counted mappings would show up as a read of rows this surface has
    /// no business reading. There is no mapping count in this contract and no statement behind it that
    /// reads one.</para>
    /// </summary>
    [Fact]
    public async Task Neither_answer_carries_anything_about_a_business()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedDescribedListingAsync(cola, "SKU-COLA-30", SupplierListingMatchStatus.Confirmed);

        // Both businesses want that product priced, so there is cross-business tenant data to leak.
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var before = await fixture.TenantOwnedFingerprintAsync();

        var page = await ListAsync(fixture);
        var detail = await fixture.DeniedMasterProductStore().FindAsync(cola, CancellationToken.None);

        Assert.Equal(before, await fixture.TenantOwnedFingerprintAsync());

        foreach (var body in new[] { JsonSerializer.Serialize(page), JsonSerializer.Serialize(detail) })
        {
            Assert.DoesNotContain("Coastal Vending", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Vending Co", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BusinessId", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ProductPriceTracking", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BusinessesWaiting", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WaitingKey", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    private static async Task<MasterProductCatalogueResult> ListAsync(
        PriceCatalogueFixture fixture,
        string? search = null) =>
        await fixture.DeniedMasterProductStore().ListAsync(
            new MasterProductCatalogueFilter(
                search,
                Skip: 0,
                Take: MasterProductCataloguePaging.MaxPageSize),
            CancellationToken.None);
}
