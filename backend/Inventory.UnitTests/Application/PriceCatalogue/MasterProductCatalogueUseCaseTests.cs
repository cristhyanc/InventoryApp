using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Application.PriceCatalogue;

/// <summary>
/// The use cases behind the Super Admin Master products page (issue #1023): the one thing they decide
/// themselves, which is which bounded page of the shared catalogue a request actually gets, and what
/// reaches the port as a search term.
///
/// <para>Over a fake store, because everything else - the ordering, the case-insensitive match, the
/// listing counts - is a statement about SQL and is therefore
/// <c>MasterProductCatalogueStoreTests</c>'. What is asserted here is that the page size is the
/// server's answer and not the client's, that the page actually served is the one reported back, and
/// that a blank search narrows nothing.</para>
/// </summary>
public class MasterProductCatalogueUseCaseTests
{
    private static readonly DateTime CreatedAt = new(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A request that asks for nothing gets the first page at the default size, and the port is asked
    /// for exactly that slice.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_paging_reads_the_first_default_sized_page()
    {
        var store = new FakeStore();

        var page = await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(),
            CancellationToken.None);

        Assert.Equal(0, store.Filter!.Skip);
        Assert.Equal(MasterProductCataloguePaging.DefaultPageSize, store.Filter.Take);
        Assert.Equal(1, page.Page);
        Assert.Equal(MasterProductCataloguePaging.DefaultPageSize, page.PageSize);
    }

    /// <summary>
    /// The bound the issue states: a page size above the maximum is served at the maximum rather than
    /// refused, and the response says which size it was - so a client cannot ask this surface for the
    /// whole catalogue in one response, and is not left guessing what it received.
    /// </summary>
    [Theory]
    [InlineData(201, MasterProductCataloguePaging.MaxPageSize)]
    [InlineData(10_000, MasterProductCataloguePaging.MaxPageSize)]
    [InlineData(200, 200)]
    [InlineData(25, 25)]
    public async Task A_requested_page_size_is_bounded_and_the_served_size_is_reported(
        int requested,
        int served)
    {
        var store = new FakeStore();

        var page = await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(PageSize: requested),
            CancellationToken.None);

        Assert.Equal(served, store.Filter!.Take);
        Assert.Equal(served, page.PageSize);
    }

    /// <summary>
    /// A page number or size that is not a positive number is the absence of one: zero and negative
    /// values take the first page at the default size rather than a negative skip, which would be a
    /// refusal the page could do nothing with - and an empty slice the database would have had to
    /// answer.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, -7)]
    public async Task A_page_or_size_that_is_not_positive_falls_back_to_the_first_default_page(
        int page,
        int pageSize)
    {
        var store = new FakeStore();

        var result = await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(Page: page, PageSize: pageSize),
            CancellationToken.None);

        Assert.Equal(0, store.Filter!.Skip);
        Assert.Equal(MasterProductCataloguePaging.DefaultPageSize, store.Filter.Take);
        Assert.Equal(1, result.Page);
    }

    /// <summary>The third page of twenty is a skip of forty, which is what server-side paging means.</summary>
    [Fact]
    public async Task A_later_page_is_read_as_a_skip_of_the_pages_before_it()
    {
        var store = new FakeStore();

        await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(Page: 3, PageSize: 20),
            CancellationToken.None);

        Assert.Equal(40, store.Filter!.Skip);
        Assert.Equal(20, store.Filter.Take);
    }

    /// <summary>
    /// The total is the number of products the search matches, not the number returned, and whether
    /// anything is left after this page is derived from that - so a page of fifty out of two hundred
    /// knows it is not the end.
    /// </summary>
    [Fact]
    public async Task The_total_is_the_match_count_and_decides_whether_more_remain()
    {
        var store = new FakeStore
        {
            Result = new MasterProductCatalogueResult([Product(1, "Coca-Cola")], TotalCount: 200),
        };

        var page = await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(Page: 1, PageSize: 50),
            CancellationToken.None);

        Assert.Equal(200, page.TotalCount);
        Assert.Single(page.Products);
        Assert.True(page.HasMore);

        var last = new MasterProductCataloguePage(page.Products, 200, Page: 4, PageSize: 50);
        Assert.False(last.HasMore);
    }

    /// <summary>
    /// A search term is trimmed and a blank one is dropped: a search box somebody cleared must narrow
    /// the catalogue to nothing at all rather than to the products whose brand contains a space.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" cola ", "cola")]
    [InlineData("Coca-Cola", "Coca-Cola")]
    public async Task A_blank_search_narrows_nothing_and_a_typed_one_is_trimmed(
        string? typed,
        string? expected)
    {
        var store = new FakeStore();

        await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(Search: typed),
            CancellationToken.None);

        Assert.Equal(expected, store.Filter!.Search);
    }

    /// <summary>
    /// The detail read is the port's answer, unchanged: an id no master product has is
    /// <see langword="null"/>, which the endpoint reports as <c>404</c> rather than as an empty
    /// product.
    /// </summary>
    [Fact]
    public async Task An_unknown_master_product_is_not_found()
    {
        var store = new FakeStore();

        Assert.Null(await new GetMasterProduct(store).Handle(404, CancellationToken.None));
        Assert.Equal(404, store.RequestedId);
    }

    /// <summary>A known master product comes back with the listings the port answered.</summary>
    [Fact]
    public async Task A_known_master_product_comes_back_with_its_listings()
    {
        var store = new FakeStore
        {
            Detail = new MasterProductDetail(
                Product(7, "Coca-Cola"),
                [
                    new CataloguedSupplierListing(
                        11,
                        "Alpha Grocer",
                        "SKU-COLA-30",
                        "Alpha Grocer Coca-Cola Classic 375mL 30 pack",
                        375m,
                        MasterProductSizeUnit.Millilitre,
                        30,
                        SupplierListingMatchStatus.Confirmed,
                        "https://alpha.example/cola",
                        CreatedAt,
                        new CataloguedObservedPrice(
                            42.50m,
                            "AUD",
                            SupplierPriceBasis.InclusiveOfGst,
                            CreatedAt)),
                ]),
        };

        var detail = await new GetMasterProduct(store).Handle(7, CancellationToken.None);

        Assert.Equal(7, detail!.Product.Id);
        var listing = Assert.Single(detail.Listings);
        Assert.Equal("Alpha Grocer", listing.SupplierName);
        Assert.Equal(42.50m, listing.LatestPrice!.PackPrice);
    }

    /// <summary>
    /// The counts are counts of supplier listings in each match status, and the total is their sum -
    /// including the refused ones, which are kept rather than deleted.
    /// </summary>
    [Fact]
    public void The_listing_counts_total_every_status_including_the_refused_ones()
    {
        var counts = new MasterProductListingCounts(Proposed: 2, Confirmed: 3, NeedsReview: 1, Rejected: 4);

        Assert.Equal(10, counts.Total);
    }

    private static CataloguedMasterProduct Product(long id, string brand) =>
        new(
            id,
            brand,
            "Classic",
            375m,
            MasterProductSizeUnit.Millilitre,
            CreatedAt,
            CreatedAt,
            new MasterProductListingCounts(0, 0, 0, 0));

    private sealed class FakeStore : IMasterProductCatalogueStore
    {
        public MasterProductCatalogueFilter? Filter { get; private set; }

        public long? RequestedId { get; private set; }

        public MasterProductCatalogueResult Result { get; init; } = new([], 0);

        public MasterProductDetail? Detail { get; init; }

        public Task<MasterProductCatalogueResult> ListAsync(
            MasterProductCatalogueFilter filter,
            CancellationToken cancellationToken)
        {
            Filter = filter;
            return Task.FromResult(Result);
        }

        public Task<MasterProductDetail?> FindAsync(long masterProductId, CancellationToken cancellationToken)
        {
            RequestedId = masterProductId;
            return Task.FromResult(Detail);
        }
    }
}
