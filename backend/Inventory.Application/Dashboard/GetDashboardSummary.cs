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
/// <para><b>The financial figures depend on the caller's role (issue #527).</b> The Dashboard is the
/// landing page, so every role holds <c>Dashboard.View</c> and reaches this use case; the week's
/// revenue and the inventory valuation additionally need <c>Dashboard.ViewFinancials</c>. A caller
/// whose role does not grant it is answered <see cref="RedactedDashboardSummaryDto"/>, which simply
/// has no such properties, and <see cref="IDashboardSummarySalesFactsProvider"/> is not called at
/// all for them - the figures are withheld because they were never calculated, not stripped on the
/// way out. The decision is made here rather than at the HTTP boundary because it decides what is
/// <em>read</em>, and it is made through <see cref="CurrentCapabilities"/>, the one place that
/// answers "what may this request do" - <see cref="RoleCapabilities"/> for a member, from the
/// request's memoised membership resolution, and <see cref="SupportSessionAccessPolicy"/>'s fixed
/// set for a support session (issue #551), which grants <c>Dashboard.ViewFinancials</c>. So it
/// cannot disagree with the capability the endpoint itself enforces, or with what
/// <c>GET /api/me/access</c> reported. No role name appears in it.</para>
///
/// <para><b>The fleet sections are degraded in a support view (issue #552).</b> A support session
/// resolves no Nayax credentials at all (issue #551), so the one live fleet read behind the refill
/// and ordering cards cannot be made - and the rest of the summary is local data the session was
/// opened to look at. Such a request is therefore answered
/// <see cref="SupportViewDashboardSummaryDto"/>: the local sections as any member of that business
/// is given them, with those two replaced by <see cref="UnavailableDashboardSectionDto"/>, and
/// <see cref="CalculateReorderNeeds"/> is not called at all. The decision is taken from
/// <see cref="IBusinessScope.State"/>, which is the very state
/// <c>NayaxRequestCredentialProvider</c> refuses in, so "the dashboard degrades" and "Nayax is
/// blocked" cannot come to mean different things; it is not a second rule about who the caller
/// is.</para>
/// </summary>
public sealed class GetDashboardSummary
{
    private readonly IClock _clock;
    private readonly IBusinessCalendar _businessCalendar;
    private readonly CurrentCapabilities _capabilities;
    private readonly IBusinessScope _businessScope;
    private readonly IDashboardSummarySalesFactsProvider _salesFacts;
    private readonly IProductCatalogStore _catalog;
    private readonly CalculateReorderNeeds _calculateReorderNeeds;

    public GetDashboardSummary(
        IClock clock,
        IBusinessCalendar businessCalendar,
        CurrentCapabilities capabilities,
        IBusinessScope businessScope,
        IDashboardSummarySalesFactsProvider salesFacts,
        IProductCatalogStore catalog,
        CalculateReorderNeeds calculateReorderNeeds)
    {
        _clock = clock;
        _businessCalendar = businessCalendar;
        _capabilities = capabilities;
        _businessScope = businessScope;
        _salesFacts = salesFacts;
        _catalog = catalog;
        _calculateReorderNeeds = calculateReorderNeeds;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has neither a usable membership nor a support session, so there is nothing to
    /// decide the response shape from. Nothing is read: an unresolved membership is a refusal, never
    /// a fall back to the least-privileged role. A request cannot reach this - the endpoint's
    /// <c>Dashboard.View</c> policy refuses such a caller first - and it fails closed if one ever
    /// could.
    /// </exception>
    public async Task<IDashboardSummaryDto> Handle(CancellationToken cancellationToken)
    {
        var mayViewFinancials = await _capabilities
            .AllowsAsync(Capability.DashboardViewFinancials, cancellationToken)
            .ConfigureAwait(false);

        // Resolved once, before anything is read, so every card describes the same instant and the
        // same business week rather than whichever moment its own read happened to start at.
        var window = MachineDashboardWindow.Resolve(_clock, _businessCalendar);

        // The sales facts are read only for a caller who may be shown them. For anyone else there
        // is no week-to-date query in the request at all, which is also why the shape below is
        // chosen by whether they were read: there is nothing to redact, only figures that exist and
        // figures that were never calculated.
        var salesFacts = mayViewFinancials
            ? await _salesFacts.GetSalesFactsAsync(
                window.CurrentWeek, window.PreviousComparableWeek, cancellationToken)
            : null;
        var products = await _catalog.ListUnorderedAsync(ProductCatalogFilter.None, cancellationToken);

        // Everything above is a read of the business's own database, so a support request makes it
        // exactly as a member's does. The fleet read below is the one Nayax-backed read behind this
        // summary, and in a support view it is not started at all (issue #552).
        if (_businessScope.State == BusinessScopeState.SupportReadOnly)
        {
            return SupportView(window, salesFacts, products);
        }

        var reorderNeeds = await _calculateReorderNeeds.Handle(cancellationToken);

        var needsRefill = NeedsRefill(reorderNeeds, products);
        var needsOrdering = NeedsOrdering(reorderNeeds, products);

        return salesFacts is { } facts
            ? new DashboardSummaryDto(
                window.NowUtc,
                window.BusinessToday,
                SalesThisWeek(window, facts),
                needsRefill,
                needsOrdering,
                Inventory(products))
            : new RedactedDashboardSummaryDto(
                window.NowUtc,
                window.BusinessToday,
                needsRefill,
                needsOrdering,
                RedactedInventory(products));
    }

    /// <summary>
    /// The summary a support session is answered (issue #552): the local sections, and the two the
    /// live fleet feeds reported as unavailable with the reason rather than counted from a fleet
    /// read that was never made.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The session's capability set did not grant <c>Dashboard.ViewFinancials</c>, so the week's
    /// figures were not read and there is no support-view shape to answer with. A request cannot
    /// reach this - <c>SupportSessionAccessPolicy</c>'s written-out set grants that capability, and
    /// its own tests pin the set literally - and it fails rather than publishing a shape nobody
    /// agreed if that ever changes without this use case being revisited.
    /// </exception>
    private static SupportViewDashboardSummaryDto SupportView(
        MachineDashboardWindow window,
        DashboardSummarySalesFacts? salesFacts,
        IReadOnlyList<ProductRecord> products)
    {
        if (salesFacts is not { } facts)
        {
            throw new InvalidOperationException(
                "A support session grants Dashboard.ViewFinancials, so the Dashboard summary's "
                    + "week-to-date figures must have been read for it.");
        }

        return new SupportViewDashboardSummaryDto(
            window.NowUtc,
            window.BusinessToday,
            SalesThisWeek(window, facts),
            UnavailableDashboardSectionDto.SupportView,
            UnavailableDashboardSectionDto.SupportView,
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

    /// <summary>
    /// The inventory card without its valuation (issue #527). Everything it does report is the same
    /// figure the card above reports over the same catalogue snapshot, including the completeness
    /// flag and the unknown-cost count: a caller who may not see the amount is still told the
    /// catalogue's costing is incomplete, because that is a data-quality fact about their stock and
    /// not a sum of money.
    /// </summary>
    private static RedactedDashboardInventorySummaryDto RedactedInventory(
        IReadOnlyList<ProductRecord> products)
    {
        var valuation = InventoryValuationPolicy.Summarize(
            products.Select(product => product.InventoryValue).ToList());

        return new RedactedDashboardInventorySummaryDto(
            valuation.IsComplete,
            valuation.ProductsWithUnknownCost,
            valuation.TotalProducts,
            products.Sum(product => product.QuantityInStock));
    }
}
