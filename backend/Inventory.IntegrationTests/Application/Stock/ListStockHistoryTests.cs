using Inventory.Application.Stock;
using Inventory.Domain.Stock;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Stock;

/// <summary>
/// The global, filterable stock-history use case (issue #384). Two things are decided here and
/// nowhere else, so they are tested here and not through the adapter:
///
/// <list type="bullet">
///   <item><b>The result is bounded.</b> A client-supplied page size is resolved through
///   <see cref="StockHistoryPaging"/>, so no request can ask the database for an unbounded number
///   of movements, and the page the caller actually got is reported back.</item>
///   <item><b>A date filter is a pair of <c>Australia/Sydney</c> calendar days, and the store only
///   ever sees UTC instants.</b> The conversion runs through <see cref="Inventory.Application.Time.IBusinessCalendar"/>
///   with the real <c>Inventory.Infrastructure.Time.SydneyBusinessCalendar</c> behind it (via
///   <see cref="FixedSydneyTime"/>), because the daylight-saving cases below are the whole point:
///   a Sydney day is 23 hours long when AEDT starts and 25 hours long when it ends, and a
///   hand-rolled +10/+11 offset would silently drop or double-count an hour of movements.</item>
/// </list>
///
/// The window is closed on <c>StockAdjustment.CreatedAt</c> (start inclusive, end exclusive) - the
/// same instant the product-specific history orders by - never on <c>EffectiveAt</c>.
/// </summary>
public class ListStockHistoryTests
{
    private const long ProductId = 7;

    private static ListStockHistory UseCase(FakeStockAdjustmentStore store, FixedSydneyTime time) =>
        new(store, time.Calendar);

    /// <summary>Any instant; only the calendar it is converted with matters to these tests.</summary>
    private static FixedSydneyTime SydneyTime() => new(new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task An_unfiltered_query_asks_the_store_for_the_first_default_sized_page()
    {
        var store = new FakeStockAdjustmentStore();

        var page = await UseCase(store, SydneyTime()).Handle(new StockHistoryQuery(), CancellationToken.None);

        var filter = Assert.IsType<StockHistoryFilter>(store.LastQueryFilter);
        Assert.Null(filter.ProductId);
        Assert.Null(filter.CreatedFromUtc);
        Assert.Null(filter.CreatedBeforeUtc);
        Assert.Null(filter.Reason);
        Assert.Null(filter.Source);
        Assert.Null(filter.MachineId);
        Assert.Equal(0, filter.Skip);
        Assert.Equal(StockHistoryPaging.DefaultPageSize, filter.Take);
        Assert.Equal(1, page.Page);
        Assert.Equal(StockHistoryPaging.DefaultPageSize, page.PageSize);
    }

    [Theory]
    [InlineData(null, StockHistoryPaging.DefaultPageSize)]
    [InlineData(0, StockHistoryPaging.DefaultPageSize)]
    [InlineData(-5, StockHistoryPaging.DefaultPageSize)]
    [InlineData(10, 10)]
    [InlineData(StockHistoryPaging.MaxPageSize, StockHistoryPaging.MaxPageSize)]
    [InlineData(StockHistoryPaging.MaxPageSize + 1, StockHistoryPaging.MaxPageSize)]
    [InlineData(100_000, StockHistoryPaging.MaxPageSize)]
    public async Task The_requested_page_size_is_bounded_by_the_server_maximum(int? requested, int expected)
    {
        var store = new FakeStockAdjustmentStore();

        var page = await UseCase(store, SydneyTime())
            .Handle(new StockHistoryQuery(PageSize: requested), CancellationToken.None);

        Assert.Equal(expected, store.LastQueryFilter!.Take);
        Assert.Equal(expected, page.PageSize);
    }

    [Theory]
    [InlineData(null, 1, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(-3, 1, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(3, 3, 20)]
    public async Task The_requested_page_number_resolves_to_a_skip_over_whole_pages(int? requested, int expectedPage, int expectedSkip)
    {
        var store = new FakeStockAdjustmentStore();

        var page = await UseCase(store, SydneyTime())
            .Handle(new StockHistoryQuery(Page: requested, PageSize: 10), CancellationToken.None);

        Assert.Equal(expectedSkip, store.LastQueryFilter!.Skip);
        Assert.Equal(expectedPage, page.Page);
    }

    [Fact]
    public async Task Product_reason_source_and_machine_filters_reach_the_store_unchanged()
    {
        var store = new FakeStockAdjustmentStore();

        await UseCase(store, SydneyTime()).Handle(
            new StockHistoryQuery(
                ProductId: ProductId,
                Reason: StockAdjustmentReason.MachineRefill,
                MachineId: 42,
                Source: StockAdjustmentSource.Nayax),
            CancellationToken.None);

        var filter = store.LastQueryFilter!;
        Assert.Equal(ProductId, filter.ProductId);
        Assert.Equal(StockAdjustmentReason.MachineRefill, filter.Reason);
        Assert.Equal(StockAdjustmentSource.Nayax, filter.Source);
        Assert.Equal(42, filter.MachineId);
    }

    /// <summary>
    /// A Sydney calendar day outside daylight saving (AEST, UTC+10): the range starts at the
    /// instant the first day begins in Sydney and ends at the instant the day after the last one
    /// begins, so the last day is included whole and the next one is not included at all.
    /// </summary>
    [Fact]
    public async Task A_date_range_becomes_the_UTC_boundaries_of_those_Sydney_days()
    {
        var store = new FakeStockAdjustmentStore();

        await UseCase(store, SydneyTime()).Handle(
            new StockHistoryQuery(From: new DateTime(2024, 6, 15), To: new DateTime(2024, 6, 16)),
            CancellationToken.None);

        var filter = store.LastQueryFilter!;
        Assert.Equal(new DateTime(2024, 6, 14, 14, 0, 0, DateTimeKind.Utc), filter.CreatedFromUtc);
        Assert.Equal(new DateTime(2024, 6, 16, 14, 0, 0, DateTimeKind.Utc), filter.CreatedBeforeUtc);
    }

    /// <summary>
    /// 6 October 2024 is the day AEDT starts in Sydney (02:00 becomes 03:00), so that Sydney day is
    /// 23 hours long: it begins at 2024-10-05T14:00Z (+10) and the next day begins at
    /// 2024-10-06T13:00Z (+11). A fixed offset would either miss the last hour of the day or spill
    /// into the next one.
    /// </summary>
    [Fact]
    public async Task A_single_day_range_spans_twenty_three_hours_when_daylight_saving_starts()
    {
        var store = new FakeStockAdjustmentStore();

        await UseCase(store, SydneyTime()).Handle(
            new StockHistoryQuery(From: new DateTime(2024, 10, 6), To: new DateTime(2024, 10, 6)),
            CancellationToken.None);

        var filter = store.LastQueryFilter!;
        Assert.Equal(new DateTime(2024, 10, 5, 14, 0, 0, DateTimeKind.Utc), filter.CreatedFromUtc);
        Assert.Equal(new DateTime(2024, 10, 6, 13, 0, 0, DateTimeKind.Utc), filter.CreatedBeforeUtc);
        Assert.Equal(23, (filter.CreatedBeforeUtc!.Value - filter.CreatedFromUtc!.Value).TotalHours);
    }

    /// <summary>
    /// 7 April 2024 is the day AEDT ends in Sydney (03:00 becomes 02:00), so that Sydney day is 25
    /// hours long: it begins at 2024-04-06T13:00Z (+11) and the next day begins at
    /// 2024-04-07T14:00Z (+10).
    /// </summary>
    [Fact]
    public async Task A_single_day_range_spans_twenty_five_hours_when_daylight_saving_ends()
    {
        var store = new FakeStockAdjustmentStore();

        await UseCase(store, SydneyTime()).Handle(
            new StockHistoryQuery(From: new DateTime(2024, 4, 7), To: new DateTime(2024, 4, 7)),
            CancellationToken.None);

        var filter = store.LastQueryFilter!;
        Assert.Equal(new DateTime(2024, 4, 6, 13, 0, 0, DateTimeKind.Utc), filter.CreatedFromUtc);
        Assert.Equal(new DateTime(2024, 4, 7, 14, 0, 0, DateTimeKind.Utc), filter.CreatedBeforeUtc);
        Assert.Equal(25, (filter.CreatedBeforeUtc!.Value - filter.CreatedFromUtc!.Value).TotalHours);
    }

    [Fact]
    public async Task An_open_ended_range_converts_only_the_boundary_it_was_given()
    {
        var store = new FakeStockAdjustmentStore();

        await UseCase(store, SydneyTime())
            .Handle(new StockHistoryQuery(From: new DateTime(2024, 6, 15)), CancellationToken.None);
        Assert.Equal(new DateTime(2024, 6, 14, 14, 0, 0, DateTimeKind.Utc), store.LastQueryFilter!.CreatedFromUtc);
        Assert.Null(store.LastQueryFilter.CreatedBeforeUtc);

        await UseCase(store, SydneyTime())
            .Handle(new StockHistoryQuery(To: new DateTime(2024, 6, 16)), CancellationToken.None);
        Assert.Null(store.LastQueryFilter!.CreatedFromUtc);
        Assert.Equal(new DateTime(2024, 6, 16, 14, 0, 0, DateTimeKind.Utc), store.LastQueryFilter.CreatedBeforeUtc);
    }

    /// <summary>
    /// A client may legitimately send a date with a time component (a browser date input normalised
    /// to midnight, for example). Only the calendar day is meaningful, so the time is dropped before
    /// the boundary is resolved rather than shifting the window.
    /// </summary>
    [Fact]
    public async Task The_time_part_of_a_filter_date_is_ignored()
    {
        var store = new FakeStockAdjustmentStore();

        await UseCase(store, SydneyTime()).Handle(
            new StockHistoryQuery(
                From: new DateTime(2024, 6, 15, 23, 59, 0),
                To: new DateTime(2024, 6, 16, 11, 30, 0)),
            CancellationToken.None);

        var filter = store.LastQueryFilter!;
        Assert.Equal(new DateTime(2024, 6, 14, 14, 0, 0, DateTimeKind.Utc), filter.CreatedFromUtc);
        Assert.Equal(new DateTime(2024, 6, 16, 14, 0, 0, DateTimeKind.Utc), filter.CreatedBeforeUtc);
    }

    [Fact]
    public async Task The_page_reports_the_stores_rows_its_total_and_whether_more_remain()
    {
        var entry = new StockHistoryEntry(
            new StockAdjustmentRecord(
                1, 1, ProductId, null, 4, 4, 1.5m, 6m, null, null, null,
                StockAdjustmentReason.Restock, StockAdjustmentSource.Manual, null, null, null,
                DateTime.UtcNow, DateTime.UtcNow),
            "Coke");
        var store = new FakeStockAdjustmentStore { QueryResult = new StockHistoryResult([entry], 120) };

        var page = await UseCase(store, SydneyTime())
            .Handle(new StockHistoryQuery(Page: 1, PageSize: 50), CancellationToken.None);

        Assert.Same(entry, Assert.Single(page.Entries));
        Assert.Equal(120, page.TotalCount);
        Assert.True(page.HasMore);

        var lastPage = await UseCase(store, SydneyTime())
            .Handle(new StockHistoryQuery(Page: 3, PageSize: 50), CancellationToken.None);
        Assert.False(lastPage.HasMore);
    }
}
