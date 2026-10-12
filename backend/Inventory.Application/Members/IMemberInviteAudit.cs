using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>Which invite change an audit event describes (issue #531).</summary>
public enum MemberInviteChange
{
    /// <summary>An invite link was created, and its token handed to the Owner once.</summary>
    Created = 1,

    /// <summary>A live invite was cancelled before it was used.</summary>
    Cancelled = 2,

    /// <summary>
    /// An invite was redeemed (issue #532): a person presented the link and the business gained a
    /// member. It is the one change here made by somebody who was not a member of the business a
    /// moment earlier, which is exactly why it is audited - "how did this person get in?" has to be
    /// answerable from the actor, the business and the invite this event names.
    /// </summary>
    Redeemed = 3,
}

/// <summary>
/// One audit event about one committed invite change (issue #531).
///
/// It records who did what, in which business, to which invite, and for which role, and it is
/// emitted only after the change is committed: a refused change did not happen, and an audit trail
/// that claimed otherwise would be worse than none. It is the same shape, and exists for the same
/// reasons, as <see cref="MemberChangeAuditEntry"/>.
///
/// <para>What it deliberately does <em>not</em> carry is the invite <strong>token</strong>, in
/// plaintext or hashed, and the Owner's note. The token is the secret the whole feature is built
/// around, and an audit trail is exactly the kind of long-lived, widely-readable store it must
/// never reach; the note is free text an Owner typed, which may well name the person being invited.
/// No display name and no email address appear either, for the standing reason of issue #524 - and
/// none of them identifies anything here, because the business id and the invite id are what an
/// investigation needs.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting Owner's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">The acting Owner's Entra <c>oid</c>.</param>
/// <param name="BusinessId">
/// The business the invite belongs to: resolved from the actor's own membership for a creation or a
/// cancellation, and taken from the invite row itself for a redemption (issue #532), whose actor is
/// not a member of it until the change this event records.
/// </param>
/// <param name="InviteId">The invite that was created or cancelled.</param>
/// <param name="Change">Which change it was.</param>
/// <param name="Role">The role the invite grants, which is what makes a creation worth auditing.</param>
/// <param name="OccurredAtUtc">When the change was committed, from the application clock.</param>
public sealed record MemberInviteAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    int InviteId,
    MemberInviteChange Change,
    BusinessRole Role,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for invite changes, implemented at the InventoryApi boundary over <c>ILogger</c>
/// (issue #531), for the same reasons as <see cref="IMemberManagementAudit"/>: an audit row in the
/// same database the change was made in would need its own non-tenant-owned table and would be
/// evidence kept inside the thing it is evidence about.
/// </summary>
public interface IMemberInviteAudit
{
    void Record(MemberInviteAuditEntry entry);
}
