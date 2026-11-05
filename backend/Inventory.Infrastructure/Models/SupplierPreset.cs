using System.Text.Json.Serialization;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One platform-wide suggested supplier (issue #848, child B of #835): the list a Super Admin keeps
/// and that issue #835 child C copies into each newly approved business.
///
/// <para><strong>A platform table, which is a different thing from the five structurally
/// non-tenant-owned tables.</strong> <see cref="Business"/>, <see cref="BusinessMembership"/>,
/// <see cref="BusinessBackfillAudit"/>, <see cref="MemberInvite"/> and <see cref="SupportSession"/>
/// are each <em>about</em> one business and carry its key in every statement; this table is about no
/// business at all. It has no <c>BusinessId</c> to carry, implements no <see cref="IBusinessOwned"/>,
/// and nothing here is ever copied back out of a business - the copying goes one way, into a
/// business, and what lands there is an ordinary tenant-owned <see cref="Supplier"/> row that the
/// business then owns outright.</para>
///
/// <para>The classification is asserted rather than implied: <c>BusinessOwnershipCoverageTests</c>
/// holds it in its own list, so a table arriving later with no owner is still the failure that test
/// exists to produce, and this one is not mistaken for an unassigned tenant table.</para>
///
/// <para><strong>Who may write it.</strong> Only the Super Admin surface of this issue, behind
/// <c>PlatformAdminPolicy</c> and the <c>PlatformDiagnosticsEndpointAttribute</c> marker - class 2
/// of the endpoint authorization classes, the same as the businesses and support-session surfaces.
/// A request there carries a <em>denied</em> <c>BusinessScope</c>, which is exactly why this table
/// cannot be tenant-owned: the central query filter would match none of its rows and
/// <c>BusinessOwnershipEnforcer</c> would have no business to stamp. No business endpoint reads or
/// writes it, and no supplier endpoint knows it exists.</para>
///
/// <para><strong>Nothing is ever deleted.</strong> A preset that should stop being suggested is
/// disabled (<see cref="IsEnabled"/>), which is a state a Super Admin can undo, rather than removed
/// - and the seeded rows in particular are edited and disabled like any other, never reset. See
/// <c>AddSupplierPresets</c> for why the four shipped rows are inserted by that one migration and by
/// nothing else.</para>
///
/// <para><strong>Being suggested and being searched are two columns</strong> (issue #1077):
/// <see cref="IsEnabled"/> decides whether businesses are offered the supplier, and
/// <see cref="SearchPrices"/> decides whether the price collector searches it. They were one flag
/// until 3 November 2026, which meant the only way to stop paying for one supplier's searches was to
/// stop offering it to businesses too.</para>
/// </summary>
public class SupplierPreset
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <summary>
    /// The supplier's name as a Super Admin typed it, trimmed and at most
    /// <c>SupplierPresetPolicy.MaxNameLength</c> characters. This is the value that is displayed and
    /// the value copied into a business; <see cref="NormalisedName"/> beside it is what decides
    /// whether two of them are the same.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <c>SupplierPresetPolicy.NormalisedName</c> of <see cref="Name"/> - the trimmed name
    /// upper-cased invariantly - stored so the uniqueness rule is a unique index rather than a
    /// <c>WHERE</c> clause two concurrent inserts can both pass.
    ///
    /// <para>It is deliberately off the wire: it is an index key, not a field of the resource, and a
    /// response that published both would invite a client to compare names itself.</para>
    /// </summary>
    [JsonIgnore]
    public string NormalisedName { get; set; } = string.Empty;

    /// <summary>
    /// The supplier's website, or <see langword="null"/> when none is recorded. Validated by
    /// <c>Inventory.Application.Suppliers.SupplierUrlPolicy</c> - issue #847's rule, the only
    /// supplier-website rule there is, and the one a business's own <c>Supplier.Url</c> uses:
    /// absolute http(s), at most 2048 characters, blank stored as null rather than as an empty
    /// string. Nothing fetches or resolves it.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Whether this preset is still suggested <em>to businesses</em>. Disabling is how a preset stops
    /// being offered - it is what the provisioning copy of issue #849 and the "Available to add" list
    /// of issue #935 read - and it is reversible; there is no delete. A disabled preset stays visible
    /// to the Super Admin, because a list that hid them would make re-enabling one impossible.
    ///
    /// <para>Since issue #1077 it says <strong>only</strong> that. Whether this supplier's prices are
    /// searched for is <see cref="SearchPrices"/>, and the collector reads that column and not this
    /// one.</para>
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Whether the price collector searches this supplier (issue #1077, the owner's decision of
    /// 3 November 2026).
    ///
    /// <para><strong>It is a separate column from <see cref="IsEnabled"/> because the two decisions
    /// are separate.</strong> The Kmart and BIG W routes are charged per searched result, so pausing
    /// their searches is an ordinary thing to want - and until this column existed the only way to do
    /// it was to press Disable, which also stopped the supplier being offered to businesses. Nothing
    /// here changes what a business sees.</para>
    ///
    /// <para>It is one half of the decision, never the whole of it:
    /// <c>PriceCollectorWorkListPolicy.IsSupplierDue</c> searches a supplier only when this is
    /// <see langword="true"/> <strong>and</strong> <see cref="PriceConnector"/> names a route, so
    /// switching it on for a supplier nothing collects issues no work.</para>
    ///
    /// <para>Switching it off stops <em>new</em> readings and nothing else: the listings,
    /// observations and held readings this supplier already has are untouched, and a stored price
    /// still ages out by <see cref="PriceCheckFreshnessHours"/> exactly as before. New presets are
    /// created searchable, and <c>AddSupplierPresetSearchPrices</c> gave every row that already
    /// existed its <see cref="IsEnabled"/> value, so the collector's behaviour did not change on
    /// deploy.</para>
    /// </summary>
    public bool SearchPrices { get; set; } = true;

    /// <summary>
    /// Whether this supplier serves the whole country. Mutually exclusive with
    /// <see cref="ServedStates"/>, and one of the two is required of anything this application
    /// stores - <c>SupplierPresetMasterPolicy</c> is that rule (issue #931).
    ///
    /// <para>"Nationwide" is kept as its own flag rather than as all eight state codes in
    /// <see cref="ServedStates"/>, because "serves everywhere" is a different claim from "serves these
    /// eight" and the next territory to be declared would turn the second into a lie.</para>
    /// </summary>
    public bool ServesNationwide { get; set; }

    /// <summary>
    /// The states and territories this supplier serves, as <c>SupplierPresetMasterPolicy</c> stores
    /// them: the declared codes in that policy's own order, comma-separated, and the empty string for
    /// a nationwide preset (issue #931).
    ///
    /// <para>It is one column rather than a child table because the vocabulary is closed at eight
    /// values, a preset's market is read and written whole, and nothing joins to a state. The
    /// canonical order is what makes it comparable: one market has exactly one stored spelling.</para>
    ///
    /// <para>An existing row the <c>AddSupplierPresetMasterFields</c> migration could not recognise
    /// keeps the column default - no states, and not nationwide - which is an unconfigured market a
    /// Super Admin must set rather than an invented claim about where the supplier sells.</para>
    /// </summary>
    public string ServedStates { get; set; } = string.Empty;

    /// <summary>
    /// Whether the prices this supplier publishes include GST (issue #931). A stored fact about the
    /// published price list, never inferred from a name, a connector or an amount: the supermarkets,
    /// Kmart and BIG W publish GST-inclusive prices, and JB Metro publishes ex-GST ones.
    ///
    /// <para>No GST is calculated anywhere near this column. It says what a collected price
    /// <em>means</em>, which is what a later price comparison needs in order not to compare an
    /// inclusive price with an exclusive one.</para>
    /// </summary>
    public bool PricesIncludeGst { get; set; }

    /// <summary>
    /// How this supplier's prices are collected, or <see langword="null"/> when nothing collects them
    /// - the state Costco is in, and a real one (issue #931).
    ///
    /// <para>A nullable enum, so the column is a nullable <c>INTEGER</c> with no default:
    /// <c>SupplierPriceConnector</c> is numbered from 1, so no stored number this application writes
    /// can be read as the first declared route by accident.</para>
    /// </summary>
    public SupplierPriceConnector? PriceConnector { get; set; }

    /// <summary>
    /// What the connector needs in order to know which retailer it is for, or
    /// <see langword="null"/> where the route takes none (issue #931).
    ///
    /// <para>Required for <see cref="SupplierPriceConnector.Varnmill"/>, which serves several
    /// retailers through one service, and empty for every other route and for no route at all -
    /// <c>SupplierPresetMasterPolicy</c> is that rule. Stored trimmed with its capitalisation kept,
    /// because it is an identifier a collector hands to a remote service rather than a name anything
    /// compares.</para>
    /// </summary>
    public string? PriceConnectorKey { get; set; }

    /// <summary>
    /// How often this supplier's prices are searched (issue #1108, the owner's decision of
    /// 5 November 2026): every day, on chosen weekdays, or on chosen weekdays every second week.
    ///
    /// <para>The schedule lives on the supplier master record for the reason
    /// <see cref="PriceCheckFreshnessHours"/> does - so no code names a supplier - and it is now what
    /// decides which work a scheduled run is issued, rather than how often somebody starts one. See
    /// <c>SupplierSearchSchedulePolicy</c> for the rules and
    /// <c>SupplierSearchDuePolicy</c> for what they mean for one product and supplier pair.</para>
    ///
    /// <para>It defaults to <see cref="SupplierSearchFrequency.Daily"/>, because daily is what the
    /// collector did before this column existed, and <c>AddSupplierPresetSearchSchedule</c> mapped
    /// every row that already existed from its stored freshness limit.</para>
    /// </summary>
    public SupplierSearchFrequency SearchFrequency { get; set; } = SupplierSearchFrequency.Daily;

    /// <summary>
    /// The weekdays this supplier is searched on, as <c>SupplierSearchSchedulePolicy</c> stores
    /// them: the declared codes in that policy's own Monday-to-Sunday order, comma-separated, and the
    /// empty string for a daily schedule (issue #1108).
    ///
    /// <para>One column rather than a child table, for the reason <see cref="ServedStates"/> is one:
    /// the vocabulary is closed at seven values, a schedule is read and written whole, and nothing
    /// joins to a weekday. The canonical order is what makes it comparable - one set of days has
    /// exactly one stored spelling - which is what lets a save decide whether the schedule actually
    /// changed and therefore whether <see cref="ScheduleEffectiveFromUtc"/> moves.</para>
    /// </summary>
    public string SearchDaysOfWeek { get; set; } = string.Empty;

    /// <summary>
    /// The date whose week is an <em>on</em> week for a fortnightly schedule, and
    /// <see langword="null"/> for every other frequency (issue #1108).
    ///
    /// <para>A date only, with no instant and no zone, because it names a day of the platform's
    /// collection calendar (<c>SupplierSearchSchedulePolicy.TimeZoneId</c>) rather than a moment.
    /// <strong>It only says which weeks are on</strong>: it is normalised to the Monday of its own
    /// week and the weeks alternate from there in both directions, so a date before it follows the
    /// same alternation rather than holding collection back until it arrives.</para>
    /// </summary>
    public DateOnly? FortnightStartDate { get; set; }

    /// <summary>
    /// When this supplier's schedule last became effective (issue #1108) - the instant an answer has
    /// to be at or after, together with the current occurrence's start, in order to cover a pair
    /// (<c>SupplierSearchDuePolicy.CoverageBoundary</c>).
    ///
    /// <para><strong>It moves on exactly two things</strong>, whichever request makes them: the
    /// frequency, the days or the fortnight start date changing, and <see cref="SearchPrices"/> going
    /// from off to on. An unrelated edit - a renamed preset, a new website, a changed market - leaves
    /// it alone, because none of those makes an answer already given wrong. A legacy <c>PUT</c> that
    /// carries no schedule fields at all is held to the same rule, which is what keeps the Super
    /// Admin page deployed before #1109 working: it keeps the stored schedule, and advances this
    /// instant only if that same request switched searching back on.</para>
    ///
    /// <para>Without it an answer given under the old schedule would satisfy the new one: a pair
    /// answered on Monday morning would stay covered by a schedule somebody changed on Monday
    /// afternoon, and switching searching off and straight back on would buy nothing.
    /// <c>AddSupplierPresetSearchSchedule</c> set it to the instant the schedule columns arrived, so
    /// no answer recorded before that counts.</para>
    /// </summary>
    public DateTime ScheduleEffectiveFromUtc { get; set; }

    /// <summary>
    /// How old an observed price of this supplier may be and still be compared, in hours (issue
    /// #925).
    ///
    /// <para><strong>The limit lives on the supplier master record precisely so no code names a
    /// supplier.</strong> The owner's decision of 27 October 2026 is 30 hours for a supplier checked
    /// daily and 102 for BIG W, which is checked on Thursday and Sunday; both are the longest
    /// expected gap between two checks plus a six-hour margin, so a run that starts a little later
    /// than yesterday's does not mark every price stale.</para>
    ///
    /// <para><strong>Since issue #1108 it is derived, not kept.</strong>
    /// <c>SupplierSearchSchedulePolicy.FreshnessHoursFor</c> of the schedule above is the only way
    /// this column is written - by both saves and by <c>AddSupplierPresetSearchSchedule</c> - so a
    /// supplier's schedule and its freshness limit can no longer disagree, which is what they did
    /// while the two were separate stored numbers. Nothing accepts a limit of its own any more.
    /// <c>SupplierPriceFreshnessPolicy</c> still owns the arithmetic and the accepted range; this
    /// column stores one supplier's answer.</para>
    /// </summary>
    public int PriceCheckFreshnessHours { get; set; } =
        SupplierPriceFreshnessPolicy.DailyCheckLimitHours;

    /// <summary>When the preset was created, from the application clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When it was last changed. Equal to <see cref="CreatedAtUtc"/> until the first edit, so "never
    /// edited" is readable without a nullable column that every reader would have to interpret.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }
}
