using Inventory.Application.Dashboard;
using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Reorder;
using Inventory.Domain.Reporting.Dashboard;
using InventoryApi.Tests.Application.Products;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Dashboard;

/// <summary>
/// The home Dashboard summary use case (issue #459). These tests cover the orchestration: which
/// periods the sales card is resolved over, that each card's figure comes from the existing
/// authority rather than a second formula, that a zero is distinguishable from missing data, and that
/// the live fleet is read exactly once with bounded, cancellable calls. Real
/// <c>Australia/Sydney</c>/daylight-saving boundaries are covered relationally in
/// <c>InventoryApi.Tests.Application.Dashboard.GetDashboardSummarySydneyPeriodTests</c>; here the
/// calendar conversion is deliberately an identity so the orchestration is what is under test.
/// </summary>
public class GetDashboardSummaryTests
{
    // A Wednesday, so week-to-date is a genuinely partial week.
    private static readonly DateTime NowUtc = new(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);

    private static Mock<IOutstandingSupplierOrderQuantityStore> OutstandingOrders(
        params (long ProductId, decimal Quantity)[] quantities)
    {
        var store = new Mock<IOutstandingSupplierOrderQuantityStore>();
        store.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(quantities.ToDictionary(x => x.ProductId, x => x.Quantity));
        return store;
    }

    private static Mock<INayaxLynxClient> Fleet(
        params (long MachineId, NayaxMachineProduct[] Products)[] machines)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(machines.Select(m => new NayaxMachine { MachineID = m.MachineId }).ToList());
        foreach (var machine in machines)
        {
            nayax.Setup(x => x.GetMachineProductsAsync(machine.MachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(machine.Products.ToList());
        }

        return nayax;
    }

    private static NayaxMachineProduct Selection(long productId, int par, int missing, int threshold) =>
        new() { NayaxProductID = productId, PAR = par, MissingStockByMDB = missing, VendOutAlertThreshold = threshold };

    private static GetDashboardSummary UseCase(
        IDashboardSummarySalesFactsProvider salesFacts,
        IProductCatalogStore catalog,
        INayaxLynxClient nayax,
        IOutstandingSupplierOrderQuantityStore outstandingOrders) =>
        new(new FakeClock(NowUtc),
            new FakeBusinessCalendar(NowUtc),
            salesFacts,
            catalog,
            new CalculateReorderNeeds(nayax, outstandingOrders));

    private static GetDashboardSummary UseCase(
        IDashboardSummarySalesFactsProvider? salesFacts = null,
        IProductCatalogStore? catalog = null,
        Mock<INayaxLynxClient>? nayax = null) =>
        UseCase(
            salesFacts ?? new FakeDashboardSummarySalesFactsProvider(),
            catalog ?? new FakeProductCatalogStore(),
            (nayax ?? Fleet()).Object,
            OutstandingOrders().Object);

    private static ProductRecord Product(
        long id,
        string name,
        int quantityInStock = 0,
        int lowStockThreshold = 0,
        int restockTo = 0,
        bool isActive = true,
        decimal? inventoryValue = 0m) =>
        FakeProductCatalogStore.Product(
            id, name, quantityInStock, lowStockThreshold, restockTo, isActive: isActive) with
        {
            InventoryValue = inventoryValue,
        };

    [Fact]
    public async Task The_summary_is_stamped_with_the_one_instant_and_business_date_every_metric_used()
    {
        var summary = await UseCase().Handle(CancellationToken.None);

        Assert.Equal(NowUtc, summary.AsOfUtc);
        Assert.Equal(NowUtc.Date, summary.BusinessDate);
    }

    /// <summary>
    /// The sales card must be resolved over the same week-to-date and previous comparable period the
    /// site and machine dashboards already use, not over a range of its own: the comparison is "the
    /// same elapsed trading time last week", which is what makes a partial week comparable.
    /// </summary>
    [Fact]
    public async Task Sales_are_read_over_the_resolved_week_to_date_and_its_previous_comparable_period()
    {
        var salesFacts = new FakeDashboardSummarySalesFactsProvider(currentPeriodSales: 100m);
        var window = MachineDashboardWindow.Resolve(new FakeClock(NowUtc), new FakeBusinessCalendar(NowUtc));

        var summary = await UseCase(salesFacts).Handle(CancellationToken.None);

        Assert.Equal(1, salesFacts.Calls);
        Assert.Equal(window.CurrentWeek, salesFacts.CurrentPeriod!.Value);
        Assert.Equal(window.PreviousComparableWeek, salesFacts.PriorPeriod!.Value);
        Assert.Equal(window.CurrentWeek.StartUtc, summary.SalesThisWeek.Period.StartUtc);
        Assert.Equal(window.CurrentWeek.EndUtc, summary.SalesThisWeek.Period.EndUtc);
        Assert.Equal(window.PreviousComparableWeek.StartUtc, summary.SalesThisWeek.ComparisonPeriod.StartUtc);
        Assert.Equal(window.PreviousComparableWeek.EndUtc, summary.SalesThisWeek.ComparisonPeriod.EndUtc);
    }

    [Fact]
    public async Task Sales_report_the_period_revenue_transaction_count_and_comparison()
    {
        var salesFacts = new FakeDashboardSummarySalesFactsProvider(
            currentPeriodSales: 300m,
            currentPeriodTransactionCount: 40,
            priorPeriodSales: 240m,
            priorPeriodTransactionCount: 30,
            earliestRecordedSaleUtc: NowUtc.AddYears(-1));

        var sales = (await UseCase(salesFacts).Handle(CancellationToken.None)).SalesThisWeek;

        Assert.Equal(300m, sales.Sales);
        Assert.Equal(40, sales.TransactionCount);
        Assert.True(sales.IsComparisonAvailable);
        Assert.Equal(240m, sales.ComparisonSales);
        Assert.Equal(30, sales.ComparisonTransactionCount);
        Assert.Equal(60m, sales.ChangeAmount);
        Assert.Equal(25m, sales.ChangePercent);
        Assert.Null(sales.ComparisonNote);
    }

    [Fact]
    public async Task A_zero_prior_period_reports_no_percentage_change_and_says_why()
    {
        var salesFacts = new FakeDashboardSummarySalesFactsProvider(
            currentPeriodSales: 180m,
            priorPeriodSales: 0m,
            earliestRecordedSaleUtc: NowUtc.AddYears(-1));

        var sales = (await UseCase(salesFacts).Handle(CancellationToken.None)).SalesThisWeek;

        Assert.True(sales.IsComparisonAvailable);
        Assert.Equal(0m, sales.ComparisonSales);
        Assert.Equal(180m, sales.ChangeAmount);
        Assert.Null(sales.ChangePercent);
        Assert.Equal(PeriodRevenueComparisonPolicy.ZeroPriorPeriodNote, sales.ComparisonNote);
    }

    /// <summary>
    /// A business whose recorded sales start after the comparable period has no comparison at all -
    /// nothing about it may be returned as a real figure, or the card would read as a collapse in
    /// trade rather than as data that does not go back that far.
    /// </summary>
    [Fact]
    public async Task A_comparable_period_the_recorded_sales_do_not_cover_is_reported_unavailable()
    {
        var salesFacts = new FakeDashboardSummarySalesFactsProvider(
            currentPeriodSales: 180m,
            earliestRecordedSaleUtc: NowUtc.AddHours(-1));

        var sales = (await UseCase(salesFacts).Handle(CancellationToken.None)).SalesThisWeek;

        Assert.False(sales.IsComparisonAvailable);
        Assert.Null(sales.ComparisonSales);
        Assert.Null(sales.ComparisonTransactionCount);
        Assert.Null(sales.ChangeAmount);
        Assert.Null(sales.ChangePercent);
        Assert.Equal(PeriodRevenueComparisonPolicy.PriorPeriodNotCoveredNote, sales.ComparisonNote);
        Assert.Equal(180m, sales.Sales);
    }

    [Fact]
    public async Task A_business_with_no_recorded_sales_reports_a_known_zero_week_and_no_comparison()
    {
        var sales = (await UseCase(new FakeDashboardSummarySalesFactsProvider()).Handle(CancellationToken.None))
            .SalesThisWeek;

        Assert.Equal(0m, sales.Sales);
        Assert.Equal(0, sales.TransactionCount);
        Assert.False(sales.IsComparisonAvailable);
    }

    [Fact]
    public async Task Needs_refill_counts_distinct_machines_with_a_low_or_empty_selection()
    {
        var catalog = new FakeProductCatalogStore(Product(100, "Coke"), Product(200, "Chips"));
        var nayax = Fleet(
            (1, [Selection(100, par: 10, missing: 10, threshold: 2), Selection(200, par: 10, missing: 9, threshold: 2)]),
            (2, [Selection(100, par: 10, missing: 9, threshold: 2)]),
            (3, [Selection(100, par: 10, missing: 0, threshold: 2)]));

        var refill = (await UseCase(catalog: catalog, nayax: nayax).Handle(CancellationToken.None)).NeedsRefill;

        Assert.Equal(2, refill.MachinesNeedingRefill);
        Assert.Equal(1, refill.MachinesWithEmptySelections);
        Assert.Equal(2, refill.MachinesWithLowSelections);
        Assert.Equal(1, refill.EmptySelectionCount);
        Assert.Equal(2, refill.LowSelectionCount);
        Assert.Equal(3, refill.MachinesEvaluated);
        Assert.Equal(4, refill.SelectionsEvaluated);
    }

    /// <summary>
    /// A selection whose product is not in the caller's catalogue - another business's product, or an
    /// unmapped MDB slot - must not invent a refill alert. The tenant boundary is the catalogue read,
    /// so "not found there" is a boundary, not a data gap to fill in.
    /// </summary>
    [Fact]
    public async Task A_selection_with_no_product_in_the_callers_catalogue_is_not_evaluated()
    {
        var catalog = new FakeProductCatalogStore(Product(100, "Coke"));
        var nayax = Fleet((1, [Selection(100, 10, 0, 2), Selection(999, par: 10, missing: 10, threshold: 2)]));

        var refill = (await UseCase(catalog: catalog, nayax: nayax).Handle(CancellationToken.None)).NeedsRefill;

        Assert.Equal(0, refill.MachinesNeedingRefill);
        Assert.Equal(1, refill.SelectionsEvaluated);
    }

    [Fact]
    public async Task An_inactive_products_selection_never_alerts()
    {
        var catalog = new FakeProductCatalogStore(Product(100, "Discontinued", isActive: false));
        var nayax = Fleet((1, [Selection(100, par: 10, missing: 10, threshold: 2)]));

        var refill = (await UseCase(catalog: catalog, nayax: nayax).Handle(CancellationToken.None)).NeedsRefill;

        Assert.Equal(0, refill.MachinesNeedingRefill);
        Assert.Equal(0, refill.SelectionsEvaluated);
        Assert.Equal(1, refill.MachinesEvaluated);
    }

    [Fact]
    public async Task An_empty_fleet_reports_zero_machines_evaluated_rather_than_a_silent_zero()
    {
        var refill = (await UseCase().Handle(CancellationToken.None)).NeedsRefill;

        Assert.Equal(0, refill.MachinesNeedingRefill);
        Assert.Equal(0, refill.MachinesEvaluated);
        Assert.Equal(0, refill.SelectionsEvaluated);
    }

    /// <summary>
    /// The count must be exactly the set the reorder-alert list shows, so the card and the list can
    /// never disagree: both come from <see cref="ListLowStockProducts.SelectReorderAlerts"/>.
    /// </summary>
    [Fact]
    public async Task Needs_ordering_counts_exactly_the_products_the_reorder_alert_list_returns()
    {
        var products = new[]
        {
            Product(100, "Coke", quantityInStock: 1, lowStockThreshold: 5, restockTo: 24),
            Product(200, "Chips", quantityInStock: 50, lowStockThreshold: 5, restockTo: 24),
            Product(300, "Water", quantityInStock: 0, lowStockThreshold: 5, restockTo: 24, isActive: false),
        };
        var catalog = new FakeProductCatalogStore(products);
        var nayax = Fleet((1, [Selection(100, par: 10, missing: 4, threshold: 2)]));
        var outstandingOrders = OutstandingOrders();
        var salesFacts = new FakeDashboardSummarySalesFactsProvider();

        var summary = await UseCase(salesFacts, catalog, nayax.Object, outstandingOrders.Object)
            .Handle(CancellationToken.None);

        var expected = await new ListLowStockProducts(
                new FakeProductCatalogStore(products),
                new CalculateReorderNeeds(nayax.Object, outstandingOrders.Object))
            .Handle(ProductCatalogFilter.None, CancellationToken.None);

        Assert.Equal(expected.Count, summary.NeedsOrdering.ProductsNeedingOrdering);
        Assert.Equal(1, summary.NeedsOrdering.ProductsNeedingOrdering);
        Assert.Equal(3, summary.NeedsOrdering.ProductsEvaluated);
    }

    /// <summary>
    /// Stock already on an outstanding supplier order is part of the authoritative reorder formula:
    /// a product whose order covers the shortfall is not counted as needing ordering again.
    /// </summary>
    [Fact]
    public async Task A_product_already_covered_by_an_outstanding_supplier_order_does_not_need_ordering()
    {
        var catalog = new FakeProductCatalogStore(
            Product(100, "Coke", quantityInStock: 1, lowStockThreshold: 5, restockTo: 24));
        var nayax = Fleet((1, [Selection(100, par: 10, missing: 0, threshold: 2)]));

        var covered = await UseCase(
                new FakeDashboardSummarySalesFactsProvider(), catalog, nayax.Object,
                OutstandingOrders((100, 40m)).Object)
            .Handle(CancellationToken.None);
        var uncovered = await UseCase(
                new FakeDashboardSummarySalesFactsProvider(), catalog, nayax.Object,
                OutstandingOrders().Object)
            .Handle(CancellationToken.None);

        Assert.Equal(0, covered.NeedsOrdering.ProductsNeedingOrdering);
        Assert.Equal(1, uncovered.NeedsOrdering.ProductsNeedingOrdering);
    }

    [Fact]
    public async Task Inventory_reports_the_cost_valuation_product_count_and_storage_units()
    {
        var catalog = new FakeProductCatalogStore(
            Product(100, "Coke", quantityInStock: 12, inventoryValue: 30m),
            Product(200, "Chips", quantityInStock: 5, inventoryValue: 12.50m));

        var inventory = (await UseCase(catalog: catalog).Handle(CancellationToken.None)).Inventory;

        Assert.Equal(42.50m, inventory.InventoryValueAtCost);
        Assert.True(inventory.IsInventoryValueComplete);
        Assert.Equal(0, inventory.ProductsWithUnknownCost);
        Assert.Equal(2, inventory.ProductCount);
        Assert.Equal(17, inventory.UnitsInStorage);
    }

    /// <summary>
    /// A product that has never had a cost rebuild run for it has an unknown inventory value, which
    /// makes the whole total unavailable - exactly as the existing Inventory Value tile already
    /// behaves. The product count and storage units stay known: only the valuation is incomplete.
    /// </summary>
    [Fact]
    public async Task An_unknown_product_cost_makes_the_valuation_unavailable_not_zero()
    {
        var catalog = new FakeProductCatalogStore(
            Product(100, "Coke", quantityInStock: 12, inventoryValue: 30m),
            Product(200, "Chips", quantityInStock: 5, inventoryValue: null));

        var inventory = (await UseCase(catalog: catalog).Handle(CancellationToken.None)).Inventory;

        Assert.Null(inventory.InventoryValueAtCost);
        Assert.False(inventory.IsInventoryValueComplete);
        Assert.Equal(1, inventory.ProductsWithUnknownCost);
        Assert.Equal(2, inventory.ProductCount);
        Assert.Equal(17, inventory.UnitsInStorage);
    }

    [Fact]
    public async Task An_inactive_product_still_counts_toward_the_catalogue_valuation_and_storage_units()
    {
        var catalog = new FakeProductCatalogStore(
            Product(100, "Discontinued", quantityInStock: 4, inventoryValue: 8m, isActive: false));

        var inventory = (await UseCase(catalog: catalog).Handle(CancellationToken.None)).Inventory;

        Assert.Equal(8m, inventory.InventoryValueAtCost);
        Assert.Equal(1, inventory.ProductCount);
        Assert.Equal(4, inventory.UnitsInStorage);
    }

    /// <summary>
    /// The whole summary is built from one unnarrowed catalogue read: a second read could disagree
    /// with the first, and the cards are meant to describe one snapshot.
    /// </summary>
    [Fact]
    public async Task The_catalogue_is_read_once_for_the_whole_summary()
    {
        var catalog = new FakeProductCatalogStore(Product(100, "Coke"));

        await UseCase(catalog: catalog).Handle(CancellationToken.None);

        Assert.Equal(1, catalog.ListUnorderedCalls);
        Assert.Equal(0, catalog.ListOrderedByNameCalls);
        Assert.Equal(ProductCatalogFilter.None, catalog.LastFilter);
    }

    /// <summary>
    /// The refill and ordering cards share one live fleet read: one machine listing plus one product
    /// request per machine, and no more. A second fan-out for the cards is exactly what the issue
    /// forbids.
    /// </summary>
    [Fact]
    public async Task The_live_fleet_is_read_once_with_one_request_per_machine()
    {
        var catalog = new FakeProductCatalogStore(Product(100, "Coke"));
        var nayax = Fleet(
            (1, [Selection(100, par: 10, missing: 10, threshold: 2)]),
            (2, [Selection(100, par: 10, missing: 10, threshold: 2)]));

        await UseCase(catalog: catalog, nayax: nayax).Handle(CancellationToken.None);

        nayax.Verify(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()), Times.Once);
        nayax.Verify(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        nayax.Verify(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()), Times.Once);
        nayax.Verify(
            x => x.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        nayax.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A failing machine request propagates instead of being absorbed, so a partial aggregate is
    /// never presented as a complete summary - the behaviour the reorder-alert list and the Pick List
    /// already have.
    /// </summary>
    [Fact]
    public async Task A_failing_machine_request_propagates_rather_than_producing_a_partial_summary()
    {
        var catalog = new FakeProductCatalogStore(Product(100, "Coke"));
        var nayax = Fleet((1, []));
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Nayax unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UseCase(catalog: catalog, nayax: nayax).Handle(CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_request_is_cancelled_rather_than_answered()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => UseCase().Handle(cancellation.Token));
    }
}
