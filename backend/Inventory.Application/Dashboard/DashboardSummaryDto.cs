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
/// A summary section the server deliberately did not compute for this request, published as
/// <c>{ "available": false, "reason": "..." }</c> (issue #552).
///
/// <para>It replaces a whole section rather than blanking figures inside one, because a count is
/// not something a caller can be shown as partially missing: a <c>0</c> or a <c>null</c>
/// <c>machinesNeedingRefill</c> reads as a fact about the business's machines. <c>Available</c> is
/// a constant <c>false</c> - this type exists only for the unavailable case, so there is no
/// combination of properties that could describe an available section unavailably.</para>
///
/// <para>The <see cref="Reason"/> is a stable code a client maps to its own wording, never a
/// sentence to display. Issue #552 publishes exactly one, <see cref="SupportView"/>; issue #329
/// adds <c>NayaxNotConnected</c> for a business whose connection is unusable, which is the same
/// shape and deliberately not a second one.</para>
/// </summary>
public sealed record UnavailableDashboardSectionDto(string Reason)
{
    /// <summary>
    /// The reason code for a section withheld because the request is a support session's
    /// read-only view of the business (issue #551): the business's Nayax credentials are not
    /// resolved in that state, so the live fleet this section needs is not read at all.
    /// </summary>
    public const string SupportViewReason = "SupportView";

    /// <summary>
    /// Always <see langword="false"/>. The property is published so a client branches on a stated
    /// flag rather than on the absence of the figures it expected.
    /// </summary>
    public bool Available => false;

    /// <summary>The section as a support view publishes it.</summary>
    public static UnavailableDashboardSectionDto SupportView { get; } = new(SupportViewReason);
}

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
/// The "Inventory" card as a caller without <c>Dashboard.ViewFinancials</c> receives it (issue
/// #527): the same card with no valuation property at all.
///
/// <see cref="IsInventoryValueComplete"/> and <see cref="ProductsWithUnknownCost"/> stay, because
/// they describe the catalogue's costing data quality rather than an amount, and
/// <see cref="ProductCount"/>/<see cref="UnitsInStorage"/> are unit counts a role that stocks the
/// machines needs. What is withheld is the money.
/// </summary>
public sealed record RedactedDashboardInventorySummaryDto(
    bool IsInventoryValueComplete,
    int ProductsWithUnknownCost,
    int ProductCount,
    int UnitsInStorage);

/// <summary>
/// What every caller of <c>GET /api/dashboard/summary</c> is told, whatever their role and however
/// their request resolved its business (issues #527 and #552): the instant the figures were
/// resolved at and the business date it falls on.
///
/// The summary publishes exactly three shapes - <see cref="DashboardSummaryDto"/> for a caller
/// whose role grants <c>Dashboard.ViewFinancials</c>, <see cref="RedactedDashboardSummaryDto"/> for
/// one whose role does not, and <see cref="SupportViewDashboardSummaryDto"/> for a request inside a
/// support session - and this is the part all three have in common. They are separate types rather
/// than one type with nullable properties because a withheld figure must be <em>absent</em> from
/// the JSON: <c>null</c> is already a meaningful answer for <c>inventoryValueAtCost</c> ("the
/// valuation is incomplete"), <c>0</c> would read as a known zero week, and a property a type does
/// not have cannot be reintroduced by a later change to a serializer option, a converter or an
/// attribute. Which shape was built is what the response is serialized from, so the same reasoning
/// keeps each card on its own type: the inventory card is deliberately not on this interface
/// because its two shapes differ, and since issue #552 the refill and ordering cards are not
/// either.
/// </summary>
public interface IDashboardSummaryDto
{
    /// <summary>The single instant every metric in the summary was resolved against.</summary>
    DateTime AsOfUtc { get; }

    /// <summary>The business date that instant falls on, in the business's own time zone.</summary>
    DateTime BusinessDate { get; }
}

/// <summary>
/// The authoritative home Dashboard summary contract (issue #459): the backend owns every sales,
/// refill, ordering and valuation decision behind these cards, and a caller displays the figures and
/// the completeness flags as returned. It is read-only.
///
/// Since issue #527 this is the shape a caller whose role grants <c>Dashboard.ViewFinancials</c> -
/// a Manager or an Owner - receives, unchanged. A caller whose role does not is answered
/// <see cref="RedactedDashboardSummaryDto"/> instead.
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

/// <summary>
/// The summary as a caller without <c>Dashboard.ViewFinancials</c> receives it (issue #527): the
/// contract above with no <c>salesThisWeek</c> object and no <c>inventory.inventoryValueAtCost</c>.
///
/// Both are absent from the serialized response rather than <c>null</c> or <c>0</c>, and the sales
/// figures are never calculated for such a caller at all - <see cref="GetDashboardSummary"/> does
/// not read them. Everything else is the same figure the same request would have produced for a
/// Manager or an Owner: redaction removes two amounts and changes nothing else.
/// </summary>
public sealed record RedactedDashboardSummaryDto(
    DateTime AsOfUtc,
    DateTime BusinessDate,
    DashboardRefillSummaryDto NeedsRefill,
    DashboardOrderingSummaryDto NeedsOrdering,
    RedactedDashboardInventorySummaryDto Inventory) : IDashboardSummaryDto;

/// <summary>
/// The summary as a request inside a support session receives it (issue #552): every local section
/// exactly as a member of the business is given it, and the two sections the live Nayax fleet feeds
/// replaced by <see cref="UnavailableDashboardSectionDto"/> with
/// <see cref="UnavailableDashboardSectionDto.SupportViewReason"/>.
///
/// <para>Nothing is degraded that did not have to be. A support session resolves no Nayax
/// credentials at all (issue #551), so the refill and ordering cards cannot be counted - but the
/// week's sales, the catalogue and the valuation are read from the business's own database exactly
/// as they always are, and withholding them too would hide the information the session was opened
/// to look at.</para>
///
/// <para>The week's figures are present because a support session's fixed capability set grants
/// <c>Dashboard.ViewFinancials</c> (<see cref="Inventory.Application.Access.SupportSessionAccessPolicy"/>),
/// which is why this is one shape rather than a redacted pair: the capability is still read through
/// <c>CurrentCapabilities</c> on the request, not assumed here.</para>
/// </summary>
public sealed record SupportViewDashboardSummaryDto(
    DateTime AsOfUtc,
    DateTime BusinessDate,
    DashboardSalesThisWeekDto SalesThisWeek,
    UnavailableDashboardSectionDto NeedsRefill,
    UnavailableDashboardSectionDto NeedsOrdering,
    DashboardInventorySummaryDto Inventory) : IDashboardSummaryDto;
