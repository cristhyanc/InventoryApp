namespace Inventory.Infrastructure.Models;

/// <summary>
/// Whether one switchable Admin menu destination is offered to anybody but a Super Admin
/// (issue #1148): one row per destination a Super Admin has switched, keyed by the destination's
/// own catalogue key.
///
/// <para><strong>One row per key, and only for a key somebody switched.</strong> The row's key is
/// the catalogue key itself (<c>Inventory.Application.PlatformOptions.MenuVisibilityKeys</c>), so
/// the database assigns nothing and two concurrent writes to the same destination collide on the
/// primary key instead of leaving two rows with two different answers - while two writes to
/// <em>different</em> destinations are two different rows and cannot overwrite each other.
/// <strong>No row for a key means that destination is offered</strong>, which is why the migration
/// that adds this table inserts nothing but the one carried-over value below, and why every existing
/// installation keeps exactly the visibility it has today. The default lives in
/// <c>Inventory.Application.PlatformOptions.GetMenuVisibility</c>, in one place, and nothing infers
/// it from a column.</para>
///
/// <para><strong>What the keys are, and what they are not.</strong> A key names a destination the
/// platform may stop offering, from a closed, compiled catalogue; a caller cannot invent one, and a
/// key the catalogue no longer declares is ignored on the way out rather than deleted. The table is
/// keyed by a string so a later group - Reports, tracked in #1153 - needs no storage change at all,
/// and that is the only generality it has: this is not a feature-flag framework, and nothing here
/// grants, revokes or gates anything.</para>
///
/// <para><strong>A platform table, the twelfth (issue #848 added the first).</strong> Like
/// <see cref="PlatformOptionVisibility"/> before it, it is about <em>no business at all</em>: the
/// setting is shared by the whole application, so there is no <c>BusinessId</c> to carry, no
/// <see cref="IBusinessOwned"/> to implement, no query filter to apply and no ownership stamp to
/// make. <c>BusinessOwnershipCoverageTests</c> declares it in <c>PlatformEntities</c> and holds that
/// list apart from the structurally global tables that <em>are</em> about one business.</para>
///
/// <para><strong>Who may write it, and who may read it.</strong> Only the Super Admin surfaces of
/// issue #1148 write it - <c>PUT /api/super-admin/menu-visibility/{key}</c> and, for the duration of
/// the rollout, the legacy <c>PUT /api/super-admin/option-visibility</c> - both behind
/// <c>PlatformAdminPolicy</c> and the <c>PlatformDiagnosticsEndpointAttribute</c> marker, which is
/// class 2 of the endpoint authorization classes and carries a <em>denied</em> <c>BusinessScope</c>,
/// so nothing tenant-owned is reachable from them. The <em>values</em> are read by every signed-in
/// member through <c>GET /api/me/access</c>, because a destination the application has switched off
/// has to stop being offered to the people it was switched off for; that read is the one documented
/// exception to "only the platform-admin surface reaches a platform table", it publishes the keys
/// that are switched off and nothing else, and it is stated as an invariant in AGENTS.md § Tenant
/// ownership and data isolation.</para>
///
/// <para><strong>It is visibility only.</strong> Switching a destination off hides it; it grants and
/// revokes nothing. Every one of those destinations' endpoints keeps authorizing each request by its
/// own capability, plan and provider rules exactly as before, whatever this table says, and a Super
/// Admin's exemption from the switch is not an exemption from any of them.</para>
/// </summary>
public class PlatformMenuVisibility
{
    /// <summary>
    /// The destination's catalogue key - the primary key, written by the adapter rather than
    /// assigned by the database, so a second insert for the same destination is refused.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Whether that destination is offered to members who are not the platform administrator.
    /// <see langword="true"/> is the state every destination starts in - as is the absence of the
    /// row itself - and a Super Admin keeps the destination in both states.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// When this destination's setting was last written, from the application clock. The row exists
    /// only because somebody switched it, so there is no "never changed" state to represent - except
    /// for the one row the migration carries over from <see cref="PlatformOptionVisibility"/>, which
    /// keeps that row's own instant rather than inventing one.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }
}
