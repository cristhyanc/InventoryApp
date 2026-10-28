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
    /// Whether this preset is still suggested. Disabling is how a preset stops being offered, and it
    /// is reversible; there is no delete. A disabled preset stays visible to the Super Admin, because
    /// a list that hid them would make re-enabling one impossible.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

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
    /// How old an observed price of this supplier may be and still be compared, in hours (issue
    /// #925).
    ///
    /// <para><strong>The limit lives on the supplier master record precisely so no code names a
    /// supplier.</strong> The owner's decision of 27 October 2026 is 30 hours for a supplier checked
    /// daily and 102 for BIG W, which is checked on Thursday and Sunday; both are the longest
    /// expected gap between two checks plus a six-hour margin, so a run that starts a little later
    /// than yesterday's does not mark every price stale. A supplier whose schedule changes is an edit
    /// to this column, and a supplier added later needs none.</para>
    ///
    /// <para>It defaults to <c>SupplierPriceFreshnessPolicy.DailyCheckLimitHours</c>, because daily
    /// is what the collector does unless somebody records otherwise, and
    /// <c>AddSupplierPriceObservations</c> set the twice-weekly limit on the one shipped record whose
    /// cadence the owner stated. <c>SupplierPriceFreshnessPolicy</c> owns the arithmetic and the
    /// accepted range; this column stores one supplier's answer.</para>
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
