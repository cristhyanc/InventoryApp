namespace Inventory.Infrastructure.Models;

/// <summary>
/// Whether the one Admin option a Super Admin may switch off is offered to anybody else
/// (issue #873): Historical GST Classification, the occasional maintenance tool of issue #433.
///
/// <para><strong>One row, and at most one.</strong> This is a single shared application-wide
/// setting rather than a list, so the row's key is the fixed <see cref="SingletonId"/> and the
/// database assigns nothing: two concurrent writes then collide on the primary key instead of
/// leaving two rows with two different answers. <strong>No row at all is the shipped state and
/// means every option is offered</strong> - which is why the migration that adds this table inserts
/// nothing, and why every existing installation keeps the visibility it has today until a Super
/// Admin switches something off. The default lives in
/// <c>Inventory.Application.PlatformAdmin.OptionVisibilitySettings.Default</c>, in one place, and
/// nothing infers it from a column.</para>
///
/// <para><strong>A platform table, the second one (issue #848 added the first).</strong> Like
/// <see cref="SupplierPreset"/> it is about <em>no business at all</em>: the setting is shared by the
/// whole application, so there is no <c>BusinessId</c> to carry, no <see cref="IBusinessOwned"/> to
/// implement, no query filter to apply and no ownership stamp to make. That is a different
/// classification from <see cref="Business"/>, <see cref="BusinessMembership"/>,
/// <see cref="BusinessBackfillAudit"/>, <see cref="MemberInvite"/> and <see cref="SupportSession"/>,
/// each of which <em>is</em> about one business and names its key in every statement;
/// <c>BusinessOwnershipCoverageTests</c> holds the two lists apart and asserts both.</para>
///
/// <para><strong>Who may write it, and who may read it.</strong> Only the Super Admin surface of
/// issue #873 writes it - <c>PUT /api/super-admin/option-visibility</c>, behind
/// <c>PlatformAdminPolicy</c> and the <c>PlatformDiagnosticsEndpointAttribute</c> marker, which is
/// class 2 of the endpoint authorization classes and carries a <em>denied</em> <c>BusinessScope</c>,
/// so nothing tenant-owned is reachable from it. The <em>value</em> is read by every signed-in
/// member, through <c>GET /api/me/access</c>, because an option the application has switched off has
/// to stop being offered to the people it was switched off for; that read is the one documented
/// exception to "only the platform-admin surface reaches a platform table", it publishes this one
/// boolean and nothing else, and it is stated as an invariant in AGENTS.md § Tenant ownership and
/// data isolation.</para>
///
/// <para><strong>It is visibility only.</strong> Switching the option off hides it; it grants and
/// revokes nothing. <c>POST /api/admin/historical-gst-classification/preview</c> and
/// <c>.../apply</c> keep authorizing every request by the <c>Data.Repair</c> capability exactly as
/// before (issue #433), whatever this row says, and a Super Admin's exemption from the switch is not
/// an exemption from that capability.</para>
/// </summary>
public class PlatformOptionVisibility
{
    /// <summary>
    /// The only key this table's single row may have. It is written by the adapter rather than
    /// assigned by the database, so a second insert is refused by the primary key.
    /// </summary>
    public const int SingletonId = 1;

    /// <summary>The fixed key; see <see cref="SingletonId"/>.</summary>
    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// Whether Historical GST Classification is offered to members who are not the platform
    /// administrator. <see langword="true"/> is the state every installation starts in - as is the
    /// absence of the row itself - and a Super Admin keeps the option in both states.
    /// </summary>
    public bool HistoricalGstClassificationVisible { get; set; } = true;

    /// <summary>
    /// When the setting was last written, from the application clock. The row exists only because
    /// somebody switched something, so there is no "never changed" state to represent.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }
}
