using Inventory.Application.Dashboard;
using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Reorder;
using InventoryApi.Controllers;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Dashboard summary endpoint's HTTP contract (issue #459): it takes no input at all - no
/// business, machine or site identifier a caller could supply - and returns the use case's result
/// unchanged, so the figures a client reads are the ones the backend calculated.
/// </summary>
public class DashboardControllerTests
{
    private static readonly DateTime NowUtc = new(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);

    private static DashboardController Controller(
        decimal currentPeriodSales, decimal priorPeriodSales, params ProductRecord[] products)
    {
        var salesFacts = new Mock<IDashboardSummarySalesFactsProvider>();
        salesFacts.Setup(x => x.GetSalesFactsAsync(
                It.IsAny<MachineDashboardPeriodUtc>(),
                It.IsAny<MachineDashboardPeriodUtc>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardSummarySalesFacts(
                currentPeriodSales, 3, priorPeriodSales, 2, NowUtc.AddYears(-1)));

        var catalog = new Mock<IProductCatalogStore>();
        catalog.Setup(x => x.ListUnorderedAsync(It.IsAny<ProductCatalogFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(products);

        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var outstandingOrders = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstandingOrders.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());

        return new DashboardController(new GetDashboardSummary(
            new FakeClock(NowUtc),
            new FakeBusinessCalendar(NowUtc),
            salesFacts.Object,
            catalog.Object,
            new CalculateReorderNeeds(nayax.Object, outstandingOrders.Object)));
    }

    private static ProductRecord Product(long id, string name, int quantityInStock, decimal? inventoryValue) => new()
    {
        Id = id,
        Name = name,
        UnitPrice = 0m,
        AverageUnitCost = 0m,
        InventoryValue = inventoryValue,
        QuantityInStock = quantityInStock,
        LowStockThreshold = 0,
        RestockTo = 0,
        IsActive = true,
        CreatedAt = NowUtc,
        UpdatedAt = NowUtc,
    };

    [Fact]
    public async Task Summary_returns_every_card_the_dashboard_needs()
    {
        var controller = Controller(300m, 240m, Product(100, "Coke", 12, 30m));

        var summary = await controller.Summary(CancellationToken.None);

        Assert.Equal(NowUtc, summary.AsOfUtc);
        Assert.Equal(NowUtc.Date, summary.BusinessDate);
        Assert.Equal(300m, summary.SalesThisWeek.Sales);
        Assert.Equal(240m, summary.SalesThisWeek.ComparisonSales);
        Assert.Equal(25m, summary.SalesThisWeek.ChangePercent);
        Assert.Equal(0, summary.NeedsRefill.MachinesEvaluated);
        Assert.Equal(0, summary.NeedsOrdering.ProductsNeedingOrdering);
        Assert.Equal(1, summary.NeedsOrdering.ProductsEvaluated);
        Assert.Equal(30m, summary.Inventory.InventoryValueAtCost);
        Assert.Equal(12, summary.Inventory.UnitsInStorage);
    }

    /// <summary>
    /// The incomplete-valuation state reaches the client as it is: a <c>null</c> total with the
    /// unknown-cost count, never a real-looking zero.
    /// </summary>
    [Fact]
    public async Task Summary_returns_an_unavailable_valuation_rather_than_a_zero_total()
    {
        var controller = Controller(0m, 0m, Product(100, "Coke", 12, null));

        var summary = await controller.Summary(CancellationToken.None);

        Assert.Null(summary.Inventory.InventoryValueAtCost);
        Assert.False(summary.Inventory.IsInventoryValueComplete);
        Assert.Equal(1, summary.Inventory.ProductsWithUnknownCost);
    }
}
