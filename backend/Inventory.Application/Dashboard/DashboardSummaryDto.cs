using System.Text.Json.Serialization;
using Inventory.Application.Reorder;

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
/// The "Needs refill" section of the summary, which is either the card's real figures
/// (<see cref="DashboardRefillSummaryDto"/>) or the withheld shape
/// (<see cref="UnavailableDashboardSectionDto"/>) with the reason the live fleet behind it was not
/// read (issues #552 and #726).
///
/// <para>A union rather than a card with nullable counts, for the reason
/// <see cref="UnavailableDashboardSectionDto"/> gives: a <c>0</c> or a <c>null</c>
/// <c>machinesNeedingRefill</c> reads as a fact about the business's machines. The two
/// <see cref="JsonDerivedTypeAttribute"/> declarations carry no type discriminator, so the JSON is
/// the member's own object and nothing else - a client branches on the stated <c>available</c> flag,
/// which is what <c>isDashboardSectionUnavailable</c> in the frontend's models already does.</para>
/// </summary>
[JsonDerivedType(typeof(DashboardRefillSummaryDto))]
[JsonDerivedType(typeof(UnavailableDashboardSectionDto))]
public interface IDashboardRefillSectionDto
{
}

/// <summary>
/// The "Needs refill" card, counted over the live Nayax machine fleet. Read
/// <see cref="Inventory.Domain.Machines.MachineRefillAlertSummary"/> for the overlap semantics: the
/// selection counts are disjoint, the two machine counts are not, and
/// <see cref="MachinesNeedingRefill"/> counts a machine once however many of its selections alert.
///
/// <see cref="MachinesEvaluated"/> and <see cref="SelectionsEvaluated"/> are what make a zero
/// honest: zero of zero means the fleet reported nothing to evaluate, while zero of many means every
/// evaluated selection is adequately stocked.
///
/// <para>It is published only when the fleet really was read. A business whose Nayax connection is
/// unusable, or whose Nayax account has not granted the fleet reads, is given
/// <see cref="UnavailableDashboardSectionDto"/> instead (issue #726): this card has no honest
/// storage-only form, because every figure on it is a figure about machines.</para>
/// </summary>
public sealed record DashboardRefillSummaryDto(
    int MachinesNeedingRefill,
    int MachinesWithEmptySelections,
    int MachinesWithLowSelections,
    int EmptySelectionCount,
    int LowSelectionCount,
    int MachinesEvaluated,
    int SelectionsEvaluated) : IDashboardRefillSectionDto;

/// <summary>
/// The "Needs ordering" card: the distinct catalogue products the authoritative reorder policy says
/// must be purchased, which is exactly the set <c>GET api/products/alerts/low-stock</c> lists for the
/// unnarrowed catalogue - same storage stock, same outstanding supplier-order quantity, same live
/// machine replenishment need, same thresholds. <see cref="ProductsEvaluated"/> is the whole
/// business-owned catalogue this count was taken over.
///
/// <para><see cref="Mode"/> is which facts the count accounted for (issue #726):
/// <see cref="ReorderModes.MachineAware"/> when the live fleet was read, and
/// <see cref="ReorderModes.StorageOnly"/> for a business with no usable Nayax connection or no
/// permission for the fleet reads - storage stock, thresholds, restock targets and outstanding
/// supplier orders, with the machines' need taken as 0. This card always carries a real count,
/// because reordering is about storage: the mode says what went into it, and in storage-only mode
/// <c>needsRefill</c> beside it carries the reason the fleet is missing. A stable code a client
/// branches on, never a sentence to display.</para>
/// </summary>
public sealed record DashboardOrderingSummaryDto(
    int ProductsNeedingOrdering,
    int ProductsEvaluated,
    string Mode);

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
/// sentence to display. Issue #552 published exactly one, <see cref="SupportView"/>; issue #726
/// adds the two availability answers of a business whose Nayax fleet reads cannot be made -
/// <see cref="NayaxNotConnected"/> and <see cref="PermissionNotGranted"/> - in the same shape and
/// deliberately not in a second one, and issue #967 adds <see cref="PlanRequired"/> beside them for a
/// business whose plan does not include the provider at all.</para>
/// </summary>
public sealed record UnavailableDashboardSectionDto(string Reason) : IDashboardRefillSectionDto
{
    /// <summary>
    /// The reason code for a section withheld because the request is a support session's
    /// read-only view of the business (issue #551): the business's Nayax credentials are not
    /// resolved in that state, so the live fleet this section needs is not read at all.
    /// </summary>
    public const string SupportViewReason = "SupportView";

    /// <summary>
    /// The reason code for a business that has no Nayax credentials an ordinary operation may use -
    /// nothing stored, or a connection the status gate refuses (issue #726). An operator connects or
    /// reconnects Nayax; nothing about the business's own storage data is affected.
    ///
    /// <para>Taken from <see cref="MachineNeedUnavailableReasons"/>, the one published vocabulary for
    /// these two answers, so this section and the reorder-alert listing's response metadata cannot
    /// drift into two spellings of one reason.</para>
    /// </summary>
    public const string NayaxNotConnectedReason = MachineNeedUnavailableReasons.NayaxNotConnected;

    /// <summary>
    /// The reason code for a business whose Nayax account has not granted this feature's reads
    /// (issue #726): the credentials are usable and Nayax refused the fleet read with <c>403</c>.
    /// Availability is per feature (issue #329, decision P9), so it is this section that is
    /// unavailable and not the connection that is broken.
    ///
    /// <para>Taken from <see cref="MachineNeedUnavailableReasons"/>, as
    /// <see cref="NayaxNotConnectedReason"/> is.</para>
    /// </summary>
    public const string PermissionNotGrantedReason = MachineNeedUnavailableReasons.PermissionNotGranted;

    /// <summary>
    /// The reason code for a business whose plan does not include the sales provider (issue #967).
    /// The fleet read is not made at all, so the section is withheld exactly as it is for an unusable
    /// connection - and distinctly, because the business may have perfectly valid credentials saved
    /// and the thing that would restore the card is the plan.
    ///
    /// <para>Taken from <see cref="MachineNeedUnavailableReasons"/>, as the two above are.</para>
    /// </summary>
    public const string PlanRequiredReason = MachineNeedUnavailableReasons.PlanRequired;

    /// <summary>
    /// Always <see langword="false"/>. The property is published so a client branches on a stated
    /// flag rather than on the absence of the figures it expected.
    /// </summary>
    public bool Available => false;

    /// <summary>The section as a support view publishes it.</summary>
    public static UnavailableDashboardSectionDto SupportView { get; } = new(SupportViewReason);

    /// <summary>The section for a business with no usable Nayax connection.</summary>
    public static UnavailableDashboardSectionDto NayaxNotConnected { get; } = new(NayaxNotConnectedReason);

    /// <summary>The section for a feature this business's Nayax account has not granted.</summary>
    public static UnavailableDashboardSectionDto PermissionNotGranted { get; } = new(PermissionNotGrantedReason);

    /// <summary>The section for a business whose plan does not include the sales provider.</summary>
    public static UnavailableDashboardSectionDto PlanRequired { get; } = new(PlanRequiredReason);

    /// <summary>
    /// The section for the reason a reorder calculation reported (issue #726). One mapping from the
    /// Application's reason to the published code, so the dashboard cannot publish a code the
    /// reorder use case does not produce, or invent one for a reason added later.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A reason this vocabulary has never heard of - a value cast from an integer, which an enum
    /// cannot prevent. Refused rather than published as available or under a guessed code.
    /// </exception>
    public static UnavailableDashboardSectionDto For(MachineNeedUnavailableReason reason) => reason switch
    {
        MachineNeedUnavailableReason.NayaxNotConnected => NayaxNotConnected,
        MachineNeedUnavailableReason.PermissionNotGranted => PermissionNotGranted,
        MachineNeedUnavailableReason.PlanRequired => PlanRequired,
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Unknown machine-need unavailability reason."),
    };
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
/// <see cref="NeedsRefill"/> is the union of issue #726: the card's figures when the live fleet was
/// read, and the withheld shape with its reason for a business whose Nayax connection is unusable or
/// whose Nayax account has not granted the fleet reads. <see cref="NeedsOrdering"/> is always a real
/// count and states the mode it was counted in, because reordering is about storage stock.
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
    IDashboardRefillSectionDto NeedsRefill,
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
    IDashboardRefillSectionDto NeedsRefill,
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
