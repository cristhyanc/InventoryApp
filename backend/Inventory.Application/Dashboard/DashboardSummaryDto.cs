namespace Inventory.Application.Dashboard;

/// <summary>
/// One summary period, in both time bases the Dashboard measures in: the UTC instants its completed
/// sales are selected between (inclusive at both ends) and the first/last <c>Australia/Sydney</c>
/// business dates those instants cover. It is the published projection of
/// <see cref="Inventory.Application.Machines.MachineDashboardPeriodUtc"/>, the period type the site
/// and machine dashboards already resolve, so a caller can state exactly which week-to-date it is
/// being shown.
/// </summary>
public sealed record DashboardSummaryPeriodDto(
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime FirstBusinessDate,
    DateTime LastBusinessDate);

/// <summary>
/// The "Sales this week" card: week-to-date gross vending revenue from completed sales, and its
/// comparison against the same elapsed trading time into the previous Sydney business week.
///
/// <see cref="Sales"/> and <see cref="TransactionCount"/> are known figures over
/// <see cref="Period"/>: zero means no completed sale was recorded in the period, not missing data.
/// Everything about the comparison is <c>null</c> when <see cref="IsComparisonAvailable"/> is
/// <c>false</c>, and <see cref="ChangePercent"/> is <c>null</c> for a zero prior period as well -
/// see <see cref="Inventory.Domain.Reporting.Dashboard.PeriodRevenueComparisonPolicy"/>, which owns
/// both rules.
/// </summary>
public sealed record DashboardSalesThisWeekDto(
    decimal Sales,
    int TransactionCount,
    DashboardSummaryPeriodDto Period,
    DashboardSummaryPeriodDto ComparisonPeriod,
    bool IsComparisonAvailable,
    decimal? ComparisonSales,
    int? ComparisonTransactionCount,
    decimal? ChangeAmount,
    decimal? ChangePercent,
    string? ComparisonNote);

/// <summary>
/// The "Needs refill" card, counted over the live Nayax machine fleet. Read
/// <see cref="Inventory.Domain.Machines.MachineRefillAlertSummary"/> for the overlap semantics: the
/// selection counts are disjoint, the two machine counts are not, and
/// <see cref="MachinesNeedingRefill"/> counts a machine once however many of its selections alert.
///
/// <see cref="MachinesEvaluated"/> and <see cref="SelectionsEvaluated"/> are what make a zero
/// honest: zero of zero means the fleet reported nothing to evaluate, while zero of many means every
/// evaluated selection is adequately stocked.
/// </summary>
public sealed record DashboardRefillSummaryDto(
    int MachinesNeedingRefill,
    int MachinesWithEmptySelections,
    int MachinesWithLowSelections,
    int EmptySelectionCount,
    int LowSelectionCount,
    int MachinesEvaluated,
    int SelectionsEvaluated);

/// <summary>
/// The "Needs ordering" card: the distinct catalogue products the authoritative reorder policy says
/// must be purchased, which is exactly the set <c>GET api/products/alerts/low-stock</c> lists for the
/// unnarrowed catalogue - same storage stock, same outstanding supplier-order quantity, same live
/// machine replenishment need, same thresholds. <see cref="ProductsEvaluated"/> is the whole
/// business-owned catalogue this count was taken over.
/// </summary>
public sealed record DashboardOrderingSummaryDto(
    int ProductsNeedingOrdering,
    int ProductsEvaluated);

/// <summary>
/// The "Inventory" card. The three figures have deliberately different scopes and are not
/// interchangeable:
/// <list type="bullet">
/// <item><see cref="InventoryValueAtCost"/> is the business-owned perpetual AVCO valuation
/// (<c>Product.InventoryValue</c>), never a selling-price valuation, and is <c>null</c> whenever
/// <see cref="IsInventoryValueComplete"/> is <c>false</c> - a product that has never had a cost
/// rebuild run for it has an unknown cost, not a zero one. It values business-owned costing
/// inventory wherever it sits, including units already loaded into a machine.</item>
/// <item><see cref="UnitsInStorage"/> is the sum of <c>Product.QuantityInStock</c>: physical
/// storage/home stock available to pick from, which excludes units already inside a machine. It is
/// not a valuation input and must never be multiplied by a price.</item>
/// <item><see cref="ProductCount"/> is every product in the caller's catalogue, active and inactive,
/// which is the same population the valuation and the units total are taken over.</item>
/// </list>
/// </summary>
public sealed record DashboardInventorySummaryDto(
    decimal? InventoryValueAtCost,
    bool IsInventoryValueComplete,
    int ProductsWithUnknownCost,
    int ProductCount,
    int UnitsInStorage);

/// <summary>
/// The authoritative home Dashboard summary contract (issue #459): the backend owns every sales,
/// refill, ordering and valuation decision behind these cards, and a caller displays the figures and
/// the completeness flags as returned. It is additive - no existing endpoint, response or figure
/// changes - and read-only.
///
/// <see cref="AsOfUtc"/> is the single instant every metric was resolved against, and
/// <see cref="BusinessDate"/> the <c>Australia/Sydney</c> business date it falls on, so a caller can
/// label the figures rather than infer freshness from its own clock. The business timezone itself is
/// not carried here: it is a fixed business constant owned by
/// <c>Inventory.Infrastructure.Time.SydneyBusinessCalendar</c> (see docs/architecture.md § Time),
/// and repeating its identifier in a response body would make it look negotiable per request.
/// </summary>
public sealed record DashboardSummaryDto(
    DateTime AsOfUtc,
    DateTime BusinessDate,
    DashboardSalesThisWeekDto SalesThisWeek,
    DashboardRefillSummaryDto NeedsRefill,
    DashboardOrderingSummaryDto NeedsOrdering,
    DashboardInventorySummaryDto Inventory);
