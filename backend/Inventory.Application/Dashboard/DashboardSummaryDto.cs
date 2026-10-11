namespace Inventory.Application.Dashboard;

/// <summary>
/// One summary period, in both time bases the Dashboard measures in: the UTC instants its completed
/// sales are selected between (inclusive at both ends) and the first/last business dates
/// (in the current business's time zone) those instants cover. It is the published projection of
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
/// comparison against the same elapsed trading time into the previous business week.
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
/// The "Inventory" card without its valuation, for a caller who does not hold
/// <see cref="Inventory.Application.Access.Capability.DashboardViewFinancials"/> (issue #527). It
/// is a distinct type rather than <see cref="DashboardInventorySummaryDto"/> with a <c>null</c>
/// total, because <c>null</c> already means something here - the valuation is incomplete - and a
/// redacted figure must be <em>absent</em>, not reported as unavailable or as zero.
///
/// The three remaining figures are the same ones every caller is given.
/// <see cref="IsInventoryValueComplete"/> and <see cref="ProductsWithUnknownCost"/> stay because
/// they are data-quality facts about the catalogue rather than financial figures: an operator may
/// know that some product costs have never been rebuilt without being shown what the stock is
/// worth.
/// </summary>
public sealed record RedactedDashboardInventorySummaryDto(
    bool IsInventoryValueComplete,
    int ProductsWithUnknownCost,
    int ProductCount,
    int UnitsInStorage);

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
/// The home Dashboard summary in one of its exactly two published shapes (issue #527):
/// <see cref="DashboardSummaryDto"/> for a caller who holds
/// <see cref="Inventory.Application.Access.Capability.DashboardViewFinancials"/>, and
/// <see cref="RedactedDashboardSummaryDto"/> for one who does not.
///
/// <para>This interface is what the two shapes have in common, and it is the whole of it: the
/// instant and business date the figures were resolved at, and the refill and ordering cards, which
/// carry no financial figure and are therefore identical for every caller. A figure that is not on
/// this interface is either financial or carries one, and whether it is published is the
/// capability's decision.</para>
///
/// <para>Redaction is expressed as two types rather than as nullable properties on one, because a
/// property that exists can be serialized: <c>null</c> and <c>0</c> both read as known figures to a
/// client, and <see cref="DashboardInventorySummaryDto.InventoryValueAtCost"/> already uses
/// <c>null</c> for "the valuation is incomplete". A field a type does not have cannot be written by
/// accident.</para>
/// </summary>
public interface IDashboardSummaryDto
{
    /// <summary>The single instant every metric in the summary was resolved against.</summary>
    DateTime AsOfUtc { get; }

    /// <summary>The business date <see cref="AsOfUtc"/> falls on, in the business's own zone.</summary>
    DateTime BusinessDate { get; }

    /// <summary>The "Needs refill" card, which every caller is given in full.</summary>
    DashboardRefillSummaryDto NeedsRefill { get; }

    /// <summary>The "Needs ordering" card, which every caller is given in full.</summary>
    DashboardOrderingSummaryDto NeedsOrdering { get; }
}

/// <summary>
/// The home Dashboard summary as a caller who may <em>not</em> see its financial figures receives it
/// (issue #527): a member whose role does not grant
/// <see cref="Inventory.Application.Access.Capability.DashboardViewFinancials"/>, which today is an
/// Operator.
///
/// It is the full contract minus exactly two things - the whole <c>salesThisWeek</c> card and the
/// inventory valuation - and those two are <em>absent</em> from the serialized response rather than
/// null or zero. Everything else is the same figure, from the same read, that every other caller is
/// given: this type exists to remove financial figures and to change nothing else. The redacted
/// figures are also never calculated for such a caller - see
/// <see cref="GetDashboardSummary"/>, which does not read the sales facts at all.
/// </summary>
public sealed record RedactedDashboardSummaryDto(
    DateTime AsOfUtc,
    DateTime BusinessDate,
    DashboardRefillSummaryDto NeedsRefill,
    DashboardOrderingSummaryDto NeedsOrdering,
    RedactedDashboardInventorySummaryDto Inventory) : IDashboardSummaryDto;

/// <summary>
/// The authoritative home Dashboard summary contract (issue #459): the backend owns every sales,
/// refill, ordering and valuation decision behind these cards, and a caller displays the figures and
/// the completeness flags as returned. It is additive - no existing endpoint, response or figure
/// changes - and read-only.
///
/// <para>This is the shape published to a caller who holds
/// <see cref="Inventory.Application.Access.Capability.DashboardViewFinancials"/> (issue #527). A
/// caller who does not is answered <see cref="RedactedDashboardSummaryDto"/>, which omits
/// <see cref="SalesThisWeek"/> and the inventory valuation; no property here changed with that
/// slice.</para>
///
/// <see cref="AsOfUtc"/> is the single instant every metric was resolved against, and
/// <see cref="BusinessDate"/> the business date it falls on, so a caller can
/// label the figures rather than infer freshness from its own clock. The business timezone itself is
/// not carried here: it is the current business's configured IANA time zone (issue #499), resolved
/// per request from the trusted current business through <c>IBusinessCalendar</c> (see
/// docs/architecture.md § Time), and a caller reads it from <c>GET /api/business/current</c> rather
/// than from each response body.
/// </summary>
public sealed record DashboardSummaryDto(
    DateTime AsOfUtc,
    DateTime BusinessDate,
    DashboardSalesThisWeekDto SalesThisWeek,
    DashboardRefillSummaryDto NeedsRefill,
    DashboardOrderingSummaryDto NeedsOrdering,
    DashboardInventorySummaryDto Inventory) : IDashboardSummaryDto;
