using Inventory.Application.Machines;
using Inventory.Application.Products;
using Inventory.Application.Reorder;
using Inventory.Application.Time;
using Inventory.Domain.Machines;
using Inventory.Domain.Reporting.Dashboard;

namespace Inventory.Application.Dashboard;

/// <summary>
/// The home Dashboard summary use case (issue #459): the one authoritative source for the sales,
/// refill, ordering and inventory figures the Dashboard cards show. It composes the existing
/// authorities rather than re-deriving anything -
/// <see cref="MachineDashboardWindow"/> for the business week and its comparable period,
/// <see cref="IDashboardSummarySalesFactsProvider"/> for completed-sale revenue over those periods,
/// <see cref="CalculateReorderNeeds"/> for the live fleet read, <see cref="IProductCatalogStore"/>
/// for the catalogue, and the Domain
/// <see cref="PeriodRevenueComparisonPolicy"/>/<see cref="MachineRefillAlertPolicy"/>/
/// <see cref="InventoryValuationPolicy"/>/<see cref="Inventory.Domain.Products.ProductReorderPolicy"/>
/// rules for every decision. No figure here is a new formula, and no caller recalculates one.
///
/// <para><b>One live fleet read.</b> The refill and ordering cards both need the machines' current
/// Nayax selections, and they take them from a single <see cref="CalculateReorderNeeds"/> call: one
/// <c>GetMachinesAsync</c> plus one <c>GetMachineProductsAsync</c> per machine, bounded at
/// <see cref="CalculateReorderNeeds.MaxConcurrentMachineRequests"/>. No second fan-out is added for
/// these cards, and a failing machine request or a cancelled request propagates out unchanged, as it
/// already does for the reorder-alert list and the Pick List, rather than being absorbed into a
/// partial aggregate presented as a complete one.</para>
///
/// <para><b>One catalogue read.</b> The ordering count, the product count, the units in storage and
/// the inventory valuation all come from the same <see cref="IProductCatalogStore"/> snapshot, so
/// they cannot disagree with one another, and the Dashboard reads the catalogue once where it
/// previously listed products and reorder alerts separately.</para>
///
/// <para><b>Reads are serialized.</b> The two persistence reads are awaited one at a time because
/// the scoped EF adapters share a single <c>AppDbContext</c>, which supports one operation at a
/// time - the same constraint <c>GetSiteSummaries</c> documents.</para>
/// </summary>
public sealed class GetDashboardSummary
{
    private readonly IClock _clock;
    private readonly IBusinessCalendar _businessCalendar;
    private readonly IDashboardSummarySalesFactsProvider _salesFacts;
    private readonly IProductCatalogStore _catalog;
    private readonly CalculateReorderNeeds _calculateReorderNeeds;

    public GetDashboardSummary(
        IClock clock,
        IBusinessCalendar businessCalendar,
        IDashboardSummarySalesFactsProvider salesFacts,
        IProductCatalogStore catalog,
        CalculateReorderNeeds calculateReorderNeeds)
    {
        _clock = clock;
        _businessCalendar = businessCalendar;
        _salesFacts = salesFacts;
        _catalog = catalog;
        _calculateReorderNeeds = calculateReorderNeeds;
    }

    public async Task<DashboardSummaryDto> Handle(CancellationToken cancellationToken)
    {
        // Resolved once, before anything is read, so every card describes the same instant and the
        // same business week rather than whichever moment its own read happened to start at.
        var window = MachineDashboardWindow.Resolve(_clock, _businessCalendar);

        var salesFacts = await _salesFacts.GetSalesFactsAsync(
            window.CurrentWeek, window.PreviousComparableWeek, cancellationToken);
        var products = await _catalog.ListUnorderedAsync(ProductCatalogFilter.None, cancellationToken);
        var reorderNeeds = await _calculateReorderNeeds.Handle(cancellationToken);

        return new DashboardSummaryDto(
            window.NowUtc,
            window.BusinessToday,
            SalesThisWeek(window, salesFacts),
            NeedsRefill(reorderNeeds, products),
            NeedsOrdering(reorderNeeds, products),
            Inventory(products));
    }

    private static DashboardSalesThisWeekDto SalesThisWeek(
        MachineDashboardWindow window, DashboardSummarySalesFacts facts)
    {
        var comparison = PeriodRevenueComparisonPolicy.Compare(
            facts.CurrentPeriodSales,
            facts.PriorPeriodSales,
            window.PreviousComparableWeek.StartUtc,
            facts.EarliestRecordedSaleUtc);

        return new DashboardSalesThisWeekDto(
            facts.CurrentPeriodSales,
            facts.CurrentPeriodTransactionCount,
            Period(window.CurrentWeek),
            Period(window.PreviousComparableWeek),
            comparison.IsAvailable,
            comparison.PriorPeriodSales,
            comparison.IsAvailable ? facts.PriorPeriodTransactionCount : null,
            comparison.ChangeAmount,
            comparison.ChangePercent,
            comparison.Note);
    }

    private static DashboardSummaryPeriodDto Period(MachineDashboardPeriodUtc period) =>
        new(period.StartUtc, period.EndUtc, period.FirstBusinessDate, period.LastBusinessDate);

    /// <summary>
    /// The refill card. Each selection the fleet read returned is paired with the catalogue product
    /// it maps to before the Domain rules see it: a selection whose product the caller's business
    /// does not own is simply not in the catalogue snapshot, so it is reported unmapped and never
    /// alerts - the tenant boundary, not a data gap (the same decision the Pick List already makes).
    /// </summary>
    private static DashboardRefillSummaryDto NeedsRefill(
        ReorderNeedsResult reorderNeeds, IReadOnlyList<ProductRecord> products)
    {
        var activeById = products.ToDictionary(product => product.Id, product => product.IsActive);

        // Mirrors how GetSiteSummaries builds the site policy's facts: the selection keeps its Nayax
        // product id and is marked inactive when the caller's catalogue holds no such product, which
        // is what makes the Domain policy skip it.
        var summary = MachineRefillAlertPolicy.Summarize(reorderNeeds.MachineSelections.Select(selection =>
            new MachineSelectionStockFact(
                selection.MachineId,
                selection.ProductId,
                selection.ProductId.HasValue && activeById.GetValueOrDefault(selection.ProductId.Value),
                selection.Par,
                selection.MissingStockByMdb,
                selection.VendOutAlertThreshold)));

        return new DashboardRefillSummaryDto(
            summary.MachinesNeedingRefill,
            summary.MachinesWithEmptySelections,
            summary.MachinesWithLowSelections,
            summary.EmptySelectionCount,
            summary.LowSelectionCount,
            reorderNeeds.MachineIds.Count,
            summary.SelectionsEvaluated);
    }

    private static DashboardOrderingSummaryDto NeedsOrdering(
        ReorderNeedsResult reorderNeeds, IReadOnlyList<ProductRecord> products) =>
        new(ListLowStockProducts.SelectReorderAlerts(products, reorderNeeds).Count, products.Count);

    private static DashboardInventorySummaryDto Inventory(IReadOnlyList<ProductRecord> products)
    {
        var valuation = InventoryValuationPolicy.Summarize(
            products.Select(product => product.InventoryValue).ToList());

        return new DashboardInventorySummaryDto(
            valuation.TotalInventoryValue,
            valuation.IsComplete,
            valuation.ProductsWithUnknownCost,
            valuation.TotalProducts,
            products.Sum(product => product.QuantityInStock));
    }
}
