using Inventory.Application.Access;
using Inventory.Application.Machines;
using Inventory.Application.Products;
using Inventory.Application.Reorder;
using Inventory.Application.Tenancy;
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
///
/// <para><b>The financial figures are the caller's capability's decision</b> (issue #527). The
/// endpoint itself needs <c>Dashboard.View</c>, which every role holds - the Dashboard is the
/// landing page - so the part of the summary that is restricted is restricted here rather than by an
/// endpoint policy: a caller whose role does not grant
/// <see cref="Capability.DashboardViewFinancials"/> is answered
/// <see cref="RedactedDashboardSummaryDto"/>, which carries neither the "Sales this week" card nor
/// the inventory valuation. The role comes from <see cref="ICurrentBusinessProvider"/> - the same
/// per-request memoised membership resolution the capability policies read - and the decision from
/// <see cref="RoleCapabilities"/>, the one role → capability table, so what this use case redacts
/// and what <c>GET /api/me/access</c> reports cannot disagree. A caller whose membership does not
/// resolve is refused outright rather than redacted, which is also what the endpoint's own policy
/// already did.</para>
///
/// <para><b>A redacted figure is not calculated.</b> The sales facts are not read at all for such a
/// caller - there is no sales query, and nothing to drop on the way out - so the restriction cannot
/// be reduced to a serialization detail by a later change.</para>
/// </summary>
public sealed class GetDashboardSummary
{
    private readonly IClock _clock;
    private readonly IBusinessCalendar _businessCalendar;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IDashboardSummarySalesFactsProvider _salesFacts;
    private readonly IProductCatalogStore _catalog;
    private readonly CalculateReorderNeeds _calculateReorderNeeds;

    public GetDashboardSummary(
        IClock clock,
        IBusinessCalendar businessCalendar,
        ICurrentBusinessProvider currentBusiness,
        IDashboardSummarySalesFactsProvider salesFacts,
        IProductCatalogStore catalog,
        CalculateReorderNeeds calculateReorderNeeds)
    {
        _clock = clock;
        _businessCalendar = businessCalendar;
        _currentBusiness = currentBusiness;
        _salesFacts = salesFacts;
        _catalog = catalog;
        _calculateReorderNeeds = calculateReorderNeeds;
    }

    /// <exception cref="Inventory.Application.Tenancy.BusinessAccessDeniedException">
    /// The caller has no usable membership, so there is no role to read the financial capability
    /// from. Nothing is read and nothing is reported: an unresolved caller is never treated as the
    /// least-privileged role (AGENTS.md § Tenant ownership and data isolation).
    /// </exception>
    public async Task<IDashboardSummaryDto> Handle(CancellationToken cancellationToken)
    {
        // Resolved once, before anything is read, so every card describes the same instant and the
        // same business week rather than whichever moment its own read happened to start at.
        var window = MachineDashboardWindow.Resolve(_clock, _businessCalendar);

        var role = await _currentBusiness.RequireRoleAsync(cancellationToken);
        var mayViewFinancials = RoleCapabilities.Allows(role, Capability.DashboardViewFinancials);

        // Not read at all for a caller who may not see it, which is what makes "the sales figures
        // are not calculated for them" a property of this method rather than of the response shape.
        var salesFacts = mayViewFinancials
            ? await _salesFacts.GetSalesFactsAsync(
                window.CurrentWeek, window.PreviousComparableWeek, cancellationToken)
            : null;
        var products = await _catalog.ListUnorderedAsync(ProductCatalogFilter.None, cancellationToken);
        var reorderNeeds = await _calculateReorderNeeds.Handle(cancellationToken);

        // Facts exist only for a caller who holds the capability, so the full shape cannot be built
        // without them - and the redacted one has nowhere to put them.
        return salesFacts is { } sales
            ? new DashboardSummaryDto(
                window.NowUtc,
                window.BusinessToday,
                SalesThisWeek(window, sales),
                NeedsRefill(reorderNeeds, products),
                NeedsOrdering(reorderNeeds, products),
                Inventory(products))
            : new RedactedDashboardSummaryDto(
                window.NowUtc,
                window.BusinessToday,
                NeedsRefill(reorderNeeds, products),
                NeedsOrdering(reorderNeeds, products),
                RedactedInventory(products));
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

    /// <summary>
    /// The same card without its valuation (issue #527). It is projected from the same catalogue
    /// snapshot and the same <see cref="InventoryValuationPolicy"/> result as
    /// <see cref="Inventory"/>, so the counts and the storage units an Operator reads are the
    /// figures every other caller reads; only the total is left out.
    /// </summary>
    private static RedactedDashboardInventorySummaryDto RedactedInventory(
        IReadOnlyList<ProductRecord> products)
    {
        var inventory = Inventory(products);

        return new RedactedDashboardInventorySummaryDto(
            inventory.IsInventoryValueComplete,
            inventory.ProductsWithUnknownCost,
            inventory.ProductCount,
            inventory.UnitsInStorage);
    }
}
