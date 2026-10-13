using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// Which lifecycle action one audit event is about (issue #545). It is stated rather than inferred
/// from the status transition, because a release does not change the business's status at all and
/// would otherwise be indistinguishable from nothing having happened.
/// </summary>
public enum PlatformBusinessLifecycleChange
{
    /// <summary>A business was switched off: <c>Active</c> to <c>Deactivated</c>.</summary>
    Deactivated = 1,

    /// <summary>A business was switched back on: <c>Deactivated</c> to <c>Active</c>.</summary>
    Reactivated = 2,

    /// <summary>One membership of a deactivated business was released.</summary>
    MemberReleased = 3,
}

/// <summary>
/// One audit event about one committed lifecycle action on one business (issue #545): who did it,
/// which business, what the action was, which status the business moved from and to, which
/// membership it was about where that applies, and how many invites it cancelled.
///
/// <para>It is emitted only after the action is committed, for the reason every audit entry here is:
/// a refused action did not happen, and an action that changed nothing - releasing a membership that
/// was already inactive - is not a change to record either. An audit trail that claimed otherwise
/// would be worse than none.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> The business <em>name</em>, its
/// contact address and ABN, and the released member's display name, email address and Entra
/// identity - the issue's "no names logged". The actor's Entra <c>(tid, oid)</c> pair, the business
/// id and the membership id are identifiers rather than personal detail, and they are what an
/// investigation needs: "who switched this business off, and whose membership did they release".
/// <see cref="PlatformBusinessLifecycleAuditEntry"/> has no field for a name of any kind, so even a
/// sink that printed the whole entry could not emit one.</para>
///
/// <para>It is a separate port from <see cref="IPlatformBusinessDecisionAudit"/> because a lifecycle
/// action is a different event with different fields: a decision records whether a rejection reason
/// was written, which a deactivation has no equivalent of, and these record a membership id and an
/// invite count, which a decision has no equivalent of.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting platform administrator's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>.</param>
/// <param name="BusinessId">The business the action was about.</param>
/// <param name="Change">Which action it was.</param>
/// <param name="FromStatus">
/// The status the business held before the action, exactly as it was stored. For a member release it
/// is the status the business still holds, because a release changes no status.
/// </param>
/// <param name="ToStatus">The status it holds now.</param>
/// <param name="MembershipId">
/// The membership that was released, or <see langword="null"/> for an action that is about no single
/// membership.
/// </param>
/// <param name="InvitesCancelled">
/// How many live invites the action cancelled, or <see langword="null"/> for an action that cancels
/// none. It is nullable rather than <c>0</c> so a reactivation or a release does not record the
/// claim "no invites were cancelled" about an operation that never looked at one.
/// </param>
/// <param name="OccurredAtUtc">When the action was committed, from the application clock.</param>
public sealed record PlatformBusinessLifecycleAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    PlatformBusinessLifecycleChange Change,
    BusinessStatus FromStatus,
    BusinessStatus ToStatus,
    int? MembershipId,
    int? InvitesCancelled,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for platform lifecycle actions on businesses, implemented at the InventoryApi
/// boundary over <c>ILogger</c> (issue #545), for the same reasons as
/// <see cref="IPlatformBusinessDecisionAudit"/>: the event carries the request's correlation id,
/// which is a transport fact, and an audit row in the same database the change was made in would be
/// evidence kept inside the thing it is evidence about.
/// </summary>
public interface IPlatformBusinessLifecycleAudit
{
    void Record(PlatformBusinessLifecycleAuditEntry entry);
}
