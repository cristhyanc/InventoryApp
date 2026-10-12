using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// Withdraws one of the current business's invites before it is used (issue #531).
///
/// <para><strong>The decision and the write are one statement.</strong> The invite is read once, to
/// tell "this business has no such invite" from "it has one, and here is what became of it", and
/// then <see cref="IMemberInviteStore.CancelAsync"/> cancels it <em>only while it is still live</em>
/// - the condition travels in the <c>WHERE</c> clause rather than being checked here and trusted a
/// moment later. An invite redeemed between the read and the write is therefore not cancelled on
/// the strength of a stale read: the statement matches nothing, and this use case re-reads and says
/// what the invite actually is. Without that, the one race that matters - a cancellation landing
/// just after a redemption - would leave a used invite recorded as cancelled and a membership
/// created by an invite the record says was withdrawn.</para>
///
/// <para><strong>A replayed cancellation is accepted and writes nothing.</strong> An invite that is
/// already cancelled is reported as it stands, with no second write and no second audit event: the
/// Owner asked for a state it already holds. A <em>used</em> invite and an <em>expired</em> one are
/// refused instead, because neither is a state a cancellation may overwrite - the first would make
/// the record disown a membership that exists, and the second would claim a deliberate decision
/// where an invite simply ran out.</para>
/// </summary>
public sealed class CancelMemberInvite
{
    /// <summary>
    /// The refusal for an invite that has been redeemed. The three messages here are published
    /// rather than internal because they are the exact sentences the <c>409</c> response body
    /// carries, which makes them part of the contract the tests pin; none of them names a business,
    /// a member or a token.
    /// </summary>
    public const string AlreadyUsedMessage =
        "This invite has already been used, so it cannot be cancelled.";

    /// <summary>The refusal for an invite that ran out on its own.</summary>
    public const string AlreadyExpiredMessage =
        "This invite has already expired, so there is nothing to cancel.";

    /// <summary>
    /// The fail-closed refusal for an invite whose stored state this deployment does not declare -
    /// a hand-written <c>UPDATE</c>, or a state a newer deployment wrote. Such an invite is already
    /// unusable, because nothing but a declared <c>Pending</c> state is live, and it is refused
    /// rather than rewritten into a state nobody can explain.
    /// </summary>
    public const string NotCancellableMessage =
        "This invite is not in a state that can be cancelled.";

    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IMemberInviteStore _store;
    private readonly IMemberInviteAudit _audit;
    private readonly IClock _clock;

    public CancelMemberInvite(
        ICurrentBusinessProvider currentBusiness,
        IAuthenticatedActorAccessor actorAccessor,
        IMemberInviteStore store,
        IMemberInviteAudit audit,
        IClock clock)
    {
        _currentBusiness = currentBusiness;
        _actorAccessor = actorAccessor;
        _store = store;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Cancels <paramref name="inviteId"/>, which is looked up only within the caller's own
    /// business - so another business's key, and a key that exists nowhere, are the same answer.
    /// </summary>
    /// <returns>
    /// The invite as the cancellation left it, or <see langword="null"/> when the caller's business
    /// holds no such invite.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">The caller has no usable membership.</exception>
    /// <exception cref="DomainConflictException">
    /// The invite has been used, has expired, or holds a state this deployment does not declare.
    /// Nothing is written in any of those cases.
    /// </exception>
    public async Task<MemberInvite?> Handle(int inviteId, CancellationToken cancellationToken)
    {
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = await _store.FindAsync(inviteId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        // One instant for the condition and the stamp alike, so the invite is never recorded as
        // cancelled at an instant it was already expired at.
        var cancelledAtUtc = _clock.UtcNow;

        var cancelled = await _store
            .CancelAsync(inviteId, cancelledAtUtc, cancellationToken)
            .ConfigureAwait(false);

        if (!cancelled)
        {
            return await ExplainAsync(inviteId, cancelledAtUtc, cancellationToken).ConfigureAwait(false);
        }

        var actor = _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        _audit.Record(new MemberInviteAuditEntry(
            actor.DirectoryTenantId,
            actor.ObjectId,
            businessId.Value,
            inviteId,
            MemberInviteChange.Cancelled,
            // The stored role as it is, so an undeclared value appears in the audit trail rather
            // than being reported as no role at all (MemberChangeAuditEntry does the same).
            existing.Role,
            cancelledAtUtc));

        // The invite as this cancellation left it. Nothing else about an invite is editable - there
        // is no path that changes its role, note, expiry or creation instant - so the read above is
        // still accurate for every other field, and no second query is needed to answer.
        return MemberInvite.From(
            existing with
            {
                StoredStatus = MemberInviteStatus.Cancelled,
                CancelledAtUtc = cancelledAtUtc,
            },
            cancelledAtUtc);
    }

    /// <summary>
    /// Nothing was written, so this re-reads the invite and answers from what it actually is now:
    /// accepted for one that is already cancelled, refused for every other state. The re-read is
    /// the authoritative one - the state at the moment the conditional write declined.
    /// </summary>
    private async Task<MemberInvite?> ExplainAsync(
        int inviteId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var current = await _store.FindAsync(inviteId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            // There is no path that deletes an invite, so this is unreachable in practice;
            // answering "not found" rather than inventing a result is the fail-closed direction.
            return null;
        }

        return MemberInvitePolicy.EffectiveStatus(current.StoredStatus, current.ExpiresAtUtc, nowUtc) switch
        {
            // A replay, or a cancellation another Owner committed a moment earlier. Either way the
            // invite is withdrawn, nothing changed here, and nothing is audited as a change.
            MemberInviteStatus.Cancelled => MemberInvite.From(current, nowUtc),
            MemberInviteStatus.Used => throw new DomainConflictException(AlreadyUsedMessage),
            MemberInviteStatus.Expired => throw new DomainConflictException(AlreadyExpiredMessage),
            _ => throw new DomainConflictException(NotCancellableMessage),
        };
    }
}
