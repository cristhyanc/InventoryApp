using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// Narrow persistence port for one business's member invites, as the Owner who manages them sees
/// them (issue #531).
///
/// <para><strong>Every method takes the owning <see cref="BusinessId"/>, and it is only ever one
/// tenancy resolution produced from the authenticated actor's membership</strong> - never a value a
/// request supplied. It is passed explicitly rather than left to the <c>AppDbContext</c> tenant
/// query filters because <c>MemberInvite</c> is one of the five structural non-tenant-owned tables
/// (docs/architecture.md § Tenant ownership): the redemption of issue #532 has to find an invite by
/// the token a caller with no membership presents, which a filter keyed to a resolved business can
/// never match. The boundary for this table is therefore the business id in the statement, exactly
/// as it is for <see cref="IBusinessMemberStore"/>, and pairing it with the invite key in every
/// lookup is what makes another business's invite "not found" rather than reachable.</para>
///
/// <para><strong>The two cancellations are single conditional statements.</strong> Each one carries
/// its own "only while this invite is still live" condition in the <c>WHERE</c> clause, so the
/// check and the write are one operation: an invite redeemed or cancelled between a caller's read
/// and this call is simply not matched, rather than being overwritten on the strength of a stale
/// read. That is why they report what they changed instead of taking the state they expect.</para>
///
/// <para>Redeeming an invite is deliberately not on this port at all. It is
/// <see cref="IMemberInviteRedemptionStore"/>'s, because it is reached by a caller who has no
/// business and therefore cannot supply one.</para>
/// </summary>
public interface IMemberInviteStore
{
    /// <summary>
    /// Stores one new invite in <paramref name="businessId"/> and returns it as persisted,
    /// including the key the database assigned.
    /// </summary>
    Task<StoredMemberInvite> CreateAsync(
        BusinessId businessId,
        NewMemberInvite invite,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every invite of one business, in a deterministic order the caller does not have to re-sort,
    /// and whatever state each one is in - a used, cancelled or expired invite is part of the
    /// record of who was invited and is never hidden.
    /// </summary>
    Task<IReadOnlyList<StoredMemberInvite>> ListAsync(
        BusinessId businessId,
        CancellationToken cancellationToken);

    /// <summary>
    /// One invite of one business, or <see langword="null"/> when that business holds no such
    /// invite - which is the same answer for a key that belongs to another business and for one
    /// that exists nowhere, so neither is confirmed.
    /// </summary>
    Task<StoredMemberInvite?> FindAsync(
        BusinessId businessId,
        int inviteId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cancels one invite, but only while it is still live at <paramref name="cancelledAtUtc"/>:
    /// pending, not already used or cancelled, and not past its expiry.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call cancelled it. <see langword="false"/> when it did not
    /// match - the invite is not that business's, or it was already used, already cancelled, or
    /// expired - in which case nothing was written.
    /// </returns>
    Task<bool> CancelAsync(
        BusinessId businessId,
        int inviteId,
        DateTime cancelledAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cancels every invite of one business that is still live at
    /// <paramref name="cancelledAtUtc"/>, and returns how many that was.
    ///
    /// <para>It exists for the business deactivation of #509, which must leave no usable invite
    /// behind: deactivating a business whose outstanding links still worked would be a way back in
    /// after the door was closed. It is one statement, so #509 can call it inside its own
    /// transaction and have the cancellations commit or roll back with the deactivation itself.</para>
    ///
    /// <para>Already-used, already-cancelled and expired invites are deliberately untouched. A
    /// cancellation records a deliberate decision at an instant; rewriting an invite that was
    /// already closed, or that expired on its own, would make the record claim something that did
    /// not happen - and it is what makes a second call idempotent.</para>
    /// </summary>
    Task<int> CancelAllLiveAsync(
        BusinessId businessId,
        DateTime cancelledAtUtc,
        CancellationToken cancellationToken);
}
