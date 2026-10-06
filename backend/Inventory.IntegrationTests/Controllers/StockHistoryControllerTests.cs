using Inventory.Application.Stock;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Stock;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Tests.Swagger;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The HTTP boundary of the global stock-history query (issue #384): it binds the transport filter,
/// invokes the use case, and maps the bounded page onto the API-owned
/// <see cref="StockHistoryPageResponse"/>. No business decision belongs here - the page size bound
/// and the Sydney-day conversion are the use case's, proved by
/// <c>InventoryApi.Tests.Application.Stock.ListStockHistoryTests</c>.
/// </summary>
public class StockHistoryControllerTests
{
    private static readonly DateTime CreatedAt = new(2024, 6, 15, 2, 0, 0, DateTimeKind.Utc);

    private static StockHistoryController CreateController(FakeStockAdjustmentStore store) =>
        new(new ListStockHistory(store, new FakeBusinessCalendar(new DateTime(2024, 6, 15))));

    private static StockHistoryEntry Entry(string productName = "Coke") =>
        new(
            new StockAdjustmentRecord(
                11, 1, 7, null, -4, 6, 1.25m, 5m, 6, 1.25m, 7.5m,
                DomainStock.StockAdjustmentReason.MachineRefill, DomainStock.StockAdjustmentSource.Nayax,
                9, "refill", null, CreatedAt, CreatedAt),
            productName);

    [Fact]
    public async Task Get_returns_the_api_owned_page_with_the_movement_and_its_product_name()
    {
        var store = new FakeStockAdjustmentStore { QueryResult = new StockHistoryResult([Entry()], 1) };

        var result = await CreateController(store).Get(new StockHistoryRequest(), CancellationToken.None);

        var page = Assert.IsType<StockHistoryPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(StockHistoryPaging.DefaultPageSize, page.PageSize);
        Assert.False(page.HasMore);

        var row = Assert.Single(page.Items);
        Assert.Equal(11, row.Id);
        Assert.Equal(7, row.ProductId);
        Assert.Equal("Coke", row.ProductName);
        Assert.Equal(-4, row.QuantityChange);
        Assert.Equal(6, row.QuantityAfter);
        Assert.Equal(1.25m, row.UnitCost);
        Assert.Equal(StockAdjustmentReason.MachineRefill, row.Reason);
        Assert.Equal(StockAdjustmentSource.Nayax, row.Source);
        Assert.Equal(9, row.MachineId);
        Assert.Equal("refill", row.Notes);
        Assert.Equal(CreatedAt, row.CreatedAt);
    }

    [Fact]
    public async Task Get_passes_every_bound_filter_to_the_use_case()
    {
        var store = new FakeStockAdjustmentStore();

        await CreateController(store).Get(
            new StockHistoryRequest(
                ProductId: 7,
                From: new DateTime(2024, 6, 1),
                To: new DateTime(2024, 6, 30),
                Reason: StockAdjustmentReason.Correction,
                MachineId: 9,
                Source: StockAdjustmentSource.Manual,
                Page: 2,
                PageSize: 10),
            CancellationToken.None);

        var filter = store.LastQueryFilter!;
        Assert.Equal(7, filter.ProductId);
        Assert.Equal(DomainStock.StockAdjustmentReason.Correction, filter.Reason);
        Assert.Equal(DomainStock.StockAdjustmentSource.Manual, filter.Source);
        Assert.Equal(9, filter.MachineId);
        Assert.Equal(10, filter.Skip);
        Assert.Equal(10, filter.Take);
        Assert.NotNull(filter.CreatedFromUtc);
        Assert.NotNull(filter.CreatedBeforeUtc);
    }

    /// <summary>
    /// An empty history is an empty page, not a 404: the global page has no single product whose
    /// existence could be in question, and "no movements match these filters" is a normal result the
    /// page renders as its empty state.
    /// </summary>
    [Fact]
    public async Task Get_returns_an_empty_page_rather_than_not_found_when_nothing_matches()
    {
        var store = new FakeStockAdjustmentStore();

        var result = await CreateController(store).Get(
            new StockHistoryRequest(ProductId: 404), CancellationToken.None);

        var page = Assert.IsType<StockHistoryPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.False(page.HasMore);
    }

    /// <summary>
    /// The new route is additive: the product-specific stock endpoints keep their exact templates
    /// (pinned by <see cref="StockControllerRouteTests"/>), and the global query is one more GET.
    /// </summary>
    [Fact]
    public void The_global_stock_history_endpoint_exposes_exactly_one_route()
    {
        var routes = ApiContractTestHost.GetApiDescriptionsFor<StockHistoryController>()
            .Select(description => $"{description.HttpMethod?.ToUpperInvariant()} {description.RelativePath}")
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["GET api/stock-history"], routes);
    }
}
