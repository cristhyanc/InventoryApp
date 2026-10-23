using System.Text.Json.Serialization;

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

    /// <summary>When the preset was created, from the application clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When it was last changed. Equal to <see cref="CreatedAtUtc"/> until the first edit, so "never
    /// edited" is readable without a nullable column that every reader would have to interpret.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }
}
