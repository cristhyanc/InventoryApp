using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Application.PriceCatalogue;

/// <summary>
/// The business lookup of master products (issue #1049, parent #817), over a fake store: the bounds
/// it enforces on a request, the slice it asks the catalogue for, and the prefill it puts on each
/// row.
///
/// <para>Over a fake store, because the match itself - case-insensitive, over the brand and the name,
/// ordered by brand, name, size and key - is a statement about SQL and is asserted in
/// <c>MasterProductSuggestionStoreTests</c> against relational SQLite. What is asserted here is what
/// the use case decides: that a search term is required and bounded <em>before</em> anything is read,
/// that the page size is the server's answer and not the client's, that the page actually served is
/// the one reported back, and that each row's Name and unit label are the Domain formatter's.</para>
/// </summary>
public class MasterProductSuggestionUseCaseTests
{
    /// <summary>
    /// The criterion: a search term is required. Nothing, whitespace and one character are the same
    /// refusal, and the store is never asked anything - a request that named nothing must not read a
    /// page of the shared catalogue.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("z")]
    [InlineData("  z  ")]
    public async Task A_search_term_that_is_absent_or_too_short_is_refused_before_anything_is_read(
        string? search)
    {
        var store = new FakeStore();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => new SuggestMasterProducts(store).Handle(
                new MasterProductSuggestionQuery(search),
                CancellationToken.None));

        Assert.Equal(MasterProductSuggestionPaging.SearchTooShortMessage, refusal.Message);
        Assert.Null(store.Filter);
    }

    /// <summary>
    /// And one longer than the documented maximum is refused rather than truncated, for the reason a
    /// product name is: a truncated search is a search for something else.
    /// </summary>
    [Fact]
    public async Task A_search_term_longer_than_the_maximum_is_refused_before_anything_is_read()
    {
        var store = new FakeStore();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => new SuggestMasterProducts(store).Handle(
                new MasterProductSuggestionQuery(
                    new string('z', MasterProductSuggestionPaging.MaxSearchLength + 1)),
                CancellationToken.None));

        Assert.Equal(MasterProductSuggestionPaging.SearchTooLongMessage, refusal.Message);
        Assert.Null(store.Filter);
    }

    /// <summary>
    /// The term that reaches the port is the trimmed one, and the shortest and longest accepted terms
    /// are both accepted - so the bounds are the documented ones rather than one either side of them.
    /// </summary>
    [Fact]
    public async Task The_accepted_term_reaches_the_port_trimmed()
    {
        var store = new FakeStore();

        _ = await new SuggestMasterProducts(store).Handle(
            new MasterProductSuggestionQuery("  Coca-Cola  "),
            CancellationToken.None);

        Assert.Equal("Coca-Cola", store.Filter!.Search);
    }

    [Theory]
    [InlineData(MasterProductSuggestionPaging.MinSearchLength)]
    [InlineData(MasterProductSuggestionPaging.MaxSearchLength)]
    public async Task A_term_of_exactly_a_bound_is_accepted(int length)
    {
        var store = new FakeStore();

        _ = await new SuggestMasterProducts(store).Handle(
            new MasterProductSuggestionQuery(new string('z', length)),
            CancellationToken.None);

        Assert.Equal(length, store.Filter!.Search.Length);
    }

    /// <summary>
    /// A request that asks for no page gets the first one at the default size, and the port is asked
    /// for exactly that slice - one row more than the page, which is how the answer says whether more
    /// matches remain without a count of the shared catalogue being read.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_paging_reads_the_first_default_sized_page()
    {
        var store = new FakeStore();

        var page = await new SuggestMasterProducts(store).Handle(
            new MasterProductSuggestionQuery("cola"),
            CancellationToken.None);

        Assert.Equal(0, store.Filter!.Skip);
        Assert.Equal(MasterProductSuggestionPaging.DefaultPageSize + 1, store.Filter.Take);
        Assert.Equal(1, page.Page);
        Assert.Equal(MasterProductSuggestionPaging.DefaultPageSize, page.PageSize);
    }

    /// <summary>
    /// The bound the issue states: a page size above the maximum is served at the maximum rather than
    /// refused, and the response says which size it was - so a client cannot ask this surface for the
    /// whole catalogue in one response, and is not left guessing what it received.
    /// </summary>
    [Theory]
    [InlineData(21, MasterProductSuggestionPaging.MaxPageSize)]
    [InlineData(10_000, MasterProductSuggestionPaging.MaxPageSize)]
    [InlineData(20, 20)]
    [InlineData(5, 5)]
    public async Task A_requested_page_size_is_bounded_and_the_served_size_is_reported(
        int requested,
        int served)
    {
        var store = new FakeStore();

        var page = await new SuggestMasterProducts(store).Handle(
            new MasterProductSuggestionQuery("cola", PageSize: requested),
            CancellationToken.None);

        Assert.Equal(served + 1, store.Filter!.Take);
        Assert.Equal(served, page.PageSize);
    }

    /// <summary>
    /// A page or size that is not a positive number is the absence of one: the first page at the
    /// default size, rather than a negative skip the database would have to answer.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, -7)]
    public async Task A_page_or_size_that_is_not_positive_falls_back_to_the_first_default_page(
        int page,
        int pageSize)
    {
        var store = new FakeStore();

        var result = await new SuggestMasterProducts(store).Handle(
            new MasterProductSuggestionQuery("cola", page, pageSize),
            CancellationToken.None);

        Assert.Equal(0, store.Filter!.Skip);
        Assert.Equal(MasterProductSuggestionPaging.DefaultPageSize + 1, store.Filter.Take);
        Assert.Equal(1, result.Page);
        Assert.Equal(MasterProductSuggestionPaging.DefaultPageSize, result.PageSize);
    }

    /// <summary>The requested page becomes the skip the port is given.</summary>
    [Fact]
    public async Task A_later_page_skips_the_pages_before_it()
    {
        var store = new FakeStore();

        _ = await new SuggestMasterProducts(store).Handle(
            new MasterProductSuggestionQuery("cola", Page: 4, PageSize: 5),
            CancellationToken.None);

        Assert.Equal(15, store.Filter!.Skip);
    }

    /// <summary>
    /// A page number no catalogue could reach does not overflow into a negative offset: the product
    /// of a page a client chose and a page size does not fit a 32-bit integer, and a negative skip is
    /// not an empty page at all.
    /// </summary>
    [Fact]
    public async Task A_page_number_past_every_catalogue_still_skips_forwards()
    {
        var store = new FakeStore();

        _ = await new SuggestMasterProducts(store).Handle(
            new MasterProductSuggestionQuery("cola", Page: int.MaxValue, PageSize: 20),
            CancellationToken.None);

        Assert.True(
            store.Filter!.Skip > 0,
            $"A page past the end of the catalogue skipped {store.Filter.Skip} rows.");
    }

    /// <summary>
    /// The extra row the port was asked for is the answer to "is there more?" and is never published:
    /// a full page with one more match behind it reports the page size it was asked for and
    /// <c>hasMore</c>.
    /// </summary>
    [Fact]
    public async Task A_page_with_more_behind_it_reports_the_page_size_and_that_there_is_more()
    {
        var store = new FakeStore { Rows = Rows(4) };

        var page = await new SuggestMasterProducts(store).Handle(
            new MasterProductSuggestionQuery("cola", PageSize: 3),
            CancellationToken.None);

        Assert.True(page.HasMore);
        Assert.Equal(3, page.Products.Count);
        Assert.Equal([1L, 2L, 3L], page.Products.Select(product => product.Id).ToArray());
    }

    /// <summary>And a page the matches do not fill says there is nothing more.</summary>
    [Fact]
    public async Task A_page_the_matches_do_not_fill_reports_no_more()
    {
        var store = new FakeStore { Rows = Rows(2) };

        var page = await new SuggestMasterProducts(store).Handle(
            new MasterProductSuggestionQuery("cola", PageSize: 3),
            CancellationToken.None);

        Assert.False(page.HasMore);
        Assert.Equal(2, page.Products.Count);
    }

    /// <summary>
    /// Each row carries the Domain formatter's Name and unit label, computed once here: the lookup,
    /// the create of #1051 and the autocomplete of #1052 must not spell one product three ways.
    /// </summary>
    [Fact]
    public async Task Each_row_carries_the_domain_formatters_prefill()
    {
        var store = new FakeStore
        {
            Rows =
            [
                new MasterProductIdentity(7, "CC's", "Tasty Cheese Corn Chips", 45m, MasterProductSizeUnit.Gram),
                new MasterProductIdentity(8, "Coca-Cola", "Classic", 1.250m, MasterProductSizeUnit.Litre),
            ],
        };

        var page = await new SuggestMasterProducts(store).Handle(
            new MasterProductSuggestionQuery("cola"),
            CancellationToken.None);

        Assert.Collection(
            page.Products,
            chips =>
            {
                Assert.Equal("CC's Tasty Cheese Corn Chips", chips.ProductName);
                Assert.Equal("45g", chips.UnitLabel);
            },
            cola =>
            {
                Assert.Equal("Coca-Cola Classic", cola.ProductName);
                Assert.Equal("1.25l", cola.UnitLabel);
            });
    }

    /// <summary>
    /// And the identity it carries is the catalogue's own four values and the key, unchanged: the
    /// prefill is added beside them rather than instead of them, because #1051 links by the key and a
    /// person reads the brand, the name and the size.
    /// </summary>
    [Fact]
    public async Task Each_row_carries_the_master_products_own_identity()
    {
        var store = new FakeStore
        {
            Rows = [new MasterProductIdentity(42, "Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre)],
        };

        var product = Assert.Single(
            (await new SuggestMasterProducts(store).Handle(
                new MasterProductSuggestionQuery("cola"),
                CancellationToken.None)).Products);

        Assert.Equal(42, product.Id);
        Assert.Equal("Coca-Cola", product.Brand);
        Assert.Equal("Classic", product.Name);
        Assert.Equal(375m, product.SizeValue);
        Assert.Equal(MasterProductSizeUnit.Millilitre, product.SizeUnit);
    }

    private static List<MasterProductIdentity> Rows(int count) =>
        Enumerable.Range(1, count)
            .Select(index => new MasterProductIdentity(
                index,
                "Coca-Cola",
                $"Variant {index}",
                375m,
                MasterProductSizeUnit.Millilitre))
            .ToList();

    /// <summary>
    /// A store that records the filter it was given and answers whatever rows a test set, so what is
    /// asserted is the use case's own decisions rather than a query.
    /// </summary>
    private sealed class FakeStore : IMasterProductSuggestionStore
    {
        public MasterProductSuggestionFilter? Filter { get; private set; }

        public IReadOnlyList<MasterProductIdentity> Rows { get; init; } = [];

        public Task<IReadOnlyList<MasterProductIdentity>> SearchAsync(
            MasterProductSuggestionFilter filter,
            CancellationToken cancellationToken)
        {
            Filter = filter;

            return Task.FromResult<IReadOnlyList<MasterProductIdentity>>(
                Rows.Take(filter.Take).ToList());
        }
    }
}
