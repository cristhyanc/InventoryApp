namespace Inventory.Application.Members;

/// <summary>
/// Narrow persistence port for the current business's member invites (issue #531).
///
/// <para><strong>No method takes a business id, and that is deliberate.</strong> An invite is
/// ordinary tenant-owned data: the <c>AppDbContext</c> query filters scope every read and
/// <c>BusinessOwnershipEnforcer</c> stamps every insert, so the owner is applied centrally and an
/// adapter here must not add a business predicate of its own (docs/architecture.md § Tenant
/// ownership). This is the opposite of <see cref="IBusinessMemberStore"/>, whose every method does
/// take one - <c>BusinessMembership</c> is a structural non-tenant-owned table with no filter to
/// defer to, and invites are not.</para>
///
/// <para><strong>The two cancellations are single conditional statements.</strong> Each one carries
/// its own "only while this invite is still live" condition in the <c>WHERE</c> clause, so the
/// check and the write are one operation: an invite redeemed or cancelled between a caller's read
/// and this call is simply not matched, rather than being overwritten on the strength of a stale
/// read. That is why they report what they changed instead of taking the state they expect.</para>
/// </summary>
public interface IMemberInviteStore
{
    /// <summary>
    /// Stores one new invite and returns it as persisted, including the key the database assigned.
    ///
    /// The owning business is not a parameter: it is stamped from the caller's resolved business on
    /// save, which is also what refuses the write when no business is resolved.
    /// </summary>
    Task<StoredMemberInvite> CreateAsync(NewMemberInvite invite, CancellationToken cancellationToken);

    /// <summary>
    /// Every invite of the current business, in a deterministic order the caller does not have to
    /// re-sort, and whatever state each one is in - a used, cancelled or expired invite is part of
    /// the record of who was invited and is never hidden.
    /// </summary>
    Task<IReadOnlyList<StoredMemberInvite>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One invite of the current business, or <see langword="null"/> when it holds no such invite -
    /// which is the same answer for a key that belongs to another business and for one that exists
    /// nowhere, so neither is confirmed.
    /// </summary>
    Task<StoredMemberInvite?> FindAsync(int inviteId, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels one invite, but only while it is still live at <paramref name="cancelledAtUtc"/>:
    /// pending, not already used or cancelled, and not past its expiry.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call cancelled it. <see langword="false"/> when it did not
    /// match - the invite is not the current business's, or it was already used, already cancelled,
    /// or expired - in which case nothing was written.
    /// </returns>
    Task<bool> CancelAsync(int inviteId, DateTime cancelledAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels every invite of the current business that is still live at
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
    Task<int> CancelAllLiveAsync(DateTime cancelledAtUtc, CancellationToken cancellationToken);
}
