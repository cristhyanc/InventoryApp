using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>Which member change an audit event describes (issue #533).</summary>
public enum MemberChange
{
    /// <summary>A member's role was changed.</summary>
    RoleChanged = 1,

    /// <summary>A member's approval was revoked, so their access stops on their next request.</summary>
    Revoked = 2,

    /// <summary>A revoked membership was reinstated.</summary>
    Reinstated = 3,
}

/// <summary>
/// One audit event about one committed member change (issue #533).
///
/// It records who changed what, in which business, and the old → new role and status, and it is
/// emitted only after the change is committed: a refused or rolled-back change did not happen, and
/// an audit trail that claimed otherwise would be worse than none.
///
/// What it deliberately does <em>not</em> carry is the member's display name or email address.
/// Those are display-only personal data (issue #524) and never logged, and they identify nobody
/// here: the membership id and the business id are what an investigation needs, and the actor's
/// Entra <c>(tid, oid)</c> pair - an identifier rather than a credential - is what says who did it.
/// No role matrix, capability or business name appears either.
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting Owner's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">The acting Owner's Entra <c>oid</c>.</param>
/// <param name="BusinessId">The business the change was made in, resolved from that actor's membership.</param>
/// <param name="MembershipId">The membership that changed.</param>
/// <param name="Change">Which change it was.</param>
/// <param name="PreviousRole">The role the membership held before, as stored.</param>
/// <param name="NewRole">The role it holds now; the same value as <paramref name="PreviousRole"/> for a revocation or a reinstatement.</param>
/// <param name="PreviouslyActive">Whether the membership was active before.</param>
/// <param name="NowActive">Whether it is active now; unchanged by a role change.</param>
/// <param name="OccurredAtUtc">When the change was committed, from the application clock.</param>
public sealed record MemberChangeAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    int MembershipId,
    MemberChange Change,
    BusinessRole PreviousRole,
    BusinessRole NewRole,
    bool PreviouslyActive,
    bool NowActive,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for member changes, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #533).
///
/// It is a log port rather than a database table for the same reason the platform diagnostics
/// audit is (see <see cref="PlatformDiagnostics.IPlatformDiagnosticsAudit"/>): an audit row in the
/// same database the change was made in would need its own non-tenant-owned table and an owner for
/// it, and would be evidence kept inside the thing it is evidence about. The structured event goes
/// to the platform's log pipeline, whose destination and retention are platform configuration a
/// human must verify rather than something this application can assert.
/// </summary>
public interface IMemberManagementAudit
{
    void Record(MemberChangeAuditEntry entry);
}
