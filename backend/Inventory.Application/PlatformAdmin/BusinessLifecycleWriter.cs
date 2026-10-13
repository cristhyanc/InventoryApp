using Inventory.Application.Members;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// The three lifecycle actions the Super Admin may take on a business that is not an application
/// decision (issue #545, one slice of #509 under #328): switch it off, switch it back on, and
/// release one of its members while it is off.
///
/// <para>They are one class for the reason <c>BusinessApplicationDecisionWriter</c> and
/// <c>MemberChangeWriter</c> exist: what must not vary between them is the frame - read the actor
/// first, open issue #522's shared write transaction, re-read the business <em>inside</em> it, refuse
/// unless the status is the one the action starts from, write, and audit once the change is
/// committed. An action that read the status outside the transaction, or audited before the commit,
/// or audited a write that changed nothing, would be a quiet hole in exactly one of the three.</para>
///
/// <para><strong>The authoritative read is inside the transaction.</strong> The mutable facts these
/// actions are decided from - the business's status, and which of its memberships are active - are
/// re-read after <c>BEGIN IMMEDIATE</c> has taken the write lock, so the comparison and the write are
/// one operation. That is what makes a deactivate and a reject arriving together resolve to one
/// winner and one <c>409</c>, and what stops a reactivation from committing against an Owner list
/// that was emptied while it waited for the lock. The read the action takes before the transaction
/// opens decides nothing: it exists to answer "no such business" with a <c>404</c> and to refuse a
/// stale view before a write lock is taken on the caller's behalf.</para>
///
/// <para><strong>Which membership rules apply, and which deliberately do not.</strong> None of the
/// three grants anybody an active membership, so none runs
/// <see cref="MembershipEligibility"/>'s check - the intent handed to the guard is the empty one, as
/// a rejection's is. The Owner rule is the interesting case. A reactivation checks it
/// <em>before</em> writing, with <see cref="BusinessOwnerRetention"/> over the memberships this
/// transaction read, because the thing being judged is the business it is about to start trading
/// again rather than a membership row it changed; and it answers with
/// <see cref="BusinessLifecyclePolicy.NoActiveOwnerMessage"/> rather than the guard's own sentence,
/// which tells the reader to give another member the Owner role first - advice only an Owner inside
/// the business could act on. A release does <em>not</em> apply the rule at all, which is this
/// slice's whole point: a deactivated business has no reachable Owner, so somebody attached to it
/// could otherwise never be freed, and the last Owner of a business nobody can reach is not access
/// worth protecting. What stops that from becoming a general power is the status gate - the release
/// is refused unless the business is still deactivated - and the reactivation refusal above, which
/// is what the business cannot come back without.</para>
/// </summary>
public sealed class BusinessLifecycleWriter
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPlatformBusinessDirectoryStore _store;
    private readonly IMemberInviteStore _invites;
    private readonly MembershipWriteGuard _guard;
    private readonly IBusinessDeactivationHook _deactivationHook;
    private readonly IPlatformBusinessLifecycleAudit _audit;
    private readonly IClock _clock;

    public BusinessLifecycleWriter(
        IAuthenticatedActorAccessor actorAccessor,
        IPlatformBusinessDirectoryStore store,
        IMemberInviteStore invites,
        MembershipWriteGuard guard,
        IBusinessDeactivationHook deactivationHook,
        IPlatformBusinessLifecycleAudit audit,
        IClock clock)
    {
        _actorAccessor = actorAccessor;
        _store = store;
        _invites = invites;
        _guard = guard;
        _deactivationHook = deactivationHook;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Switches one trading business off: its status becomes
    /// <see cref="BusinessStatus.Deactivated"/>, every invite of it that is still live is cancelled
    /// permanently, and the deactivation hook runs - all in one transaction.
    /// </summary>
    /// <param name="businessId">
    /// The business being switched off. It is the subject of the action, never a tenancy scope: the
    /// request keeps its denied <c>BusinessScope</c>, and nothing this writes is stamped with it.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    /// <returns>
    /// The deactivation as it was recorded, or <see langword="null"/> when no such business exists.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is nobody to record the action for.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The business is not <see cref="BusinessStatus.Active"/>. Nothing is committed.
    /// </exception>
    public async Task<PlatformBusinessDeactivation?> DeactivateAsync(
        int businessId,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        var applied = await ApplyAsync(
            businessId,
            BusinessLifecyclePolicy.CanDeactivate,
            BusinessLifecyclePolicy.NotActiveMessage,
            async (current, occurredAtUtc, token) =>
            {
                await _store.DeactivateAsync(current.BusinessId, token).ConfigureAwait(false);

                // The business id comes from the row this transaction just read, not from the route
                // value - so the one statement that reaches past this feature's own port names a
                // business the server itself established.
                var invitesCancelled = await _invites
                    .CancelAllLiveAsync(BusinessId.From(current.BusinessId), occurredAtUtc, token)
                    .ConfigureAwait(false);

                // Last, and still inside the transaction: whatever a later task hangs here commits
                // or rolls back with the deactivation it is about.
                await _deactivationHook
                    .OnBusinessDeactivatedAsync(
                        new BusinessDeactivated(current.BusinessId, occurredAtUtc),
                        token)
                    .ConfigureAwait(false);

                return new Applied<PlatformBusinessDeactivation>(
                    PlatformBusinessLifecycleChange.Deactivated,
                    current.Status,
                    BusinessStatus.Deactivated,
                    MembershipId: null,
                    invitesCancelled,
                    occurredAtUtc,
                    Changed: true,
                    new PlatformBusinessDeactivation(
                        current.BusinessId,
                        current.Name,
                        BusinessStatus.Deactivated,
                        invitesCancelled));
            },
            cancellationToken).ConfigureAwait(false);

        return Audited(actor, businessId, applied);
    }

    /// <summary>
    /// Switches one deactivated business back on: its status becomes
    /// <see cref="BusinessStatus.Active"/> and nothing else changes.
    /// </summary>
    /// <returns>
    /// The reactivation as it was recorded, or <see langword="null"/> when no such business exists.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">The request carries no identifiable actor.</exception>
    /// <exception cref="DomainConflictException">
    /// The business is not <see cref="BusinessStatus.Deactivated"/>, or it no longer has an active
    /// Owner membership. Nothing is committed in either case.
    /// </exception>
    public async Task<PlatformBusinessReactivation?> ReactivateAsync(
        int businessId,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        var applied = await ApplyAsync(
            businessId,
            BusinessLifecyclePolicy.CanReactivate,
            BusinessLifecyclePolicy.NotDeactivatedMessage,
            async (current, occurredAtUtc, token) =>
            {
                // Judged on the memberships this transaction read, by the one Domain rule that
                // decides what "has an active Owner" means - so a revoked Owner, a Manager and a
                // stored role this code cannot read are all "not an Owner" here exactly as they are
                // everywhere else.
                if (!BusinessOwnerRetention.RetainsActiveOwner(OwnerView(current)))
                {
                    throw new DomainConflictException(BusinessLifecyclePolicy.NoActiveOwnerMessage);
                }

                await _store.ReactivateAsync(current.BusinessId, token).ConfigureAwait(false);

                return new Applied<PlatformBusinessReactivation>(
                    PlatformBusinessLifecycleChange.Reactivated,
                    current.Status,
                    BusinessStatus.Active,
                    MembershipId: null,
                    InvitesCancelled: null,
                    occurredAtUtc,
                    Changed: true,
                    new PlatformBusinessReactivation(
                        current.BusinessId,
                        current.Name,
                        BusinessStatus.Active));
            },
            cancellationToken).ConfigureAwait(false);

        return Audited(actor, businessId, applied);
    }

    /// <summary>
    /// Releases one membership of one deactivated business, so the person it belongs to is free to
    /// redeem an invite or apply for a business of their own.
    /// </summary>
    /// <param name="businessId">The deactivated business the membership must belong to.</param>
    /// <param name="membershipId">
    /// The membership to release, looked up only within that business - so another business's key,
    /// and a key that exists nowhere, are the same answer.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    /// <returns>
    /// The member as the release left them, or <see langword="null"/> when that business holds no
    /// such membership - including when the business itself does not exist.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">The request carries no identifiable actor.</exception>
    /// <exception cref="DomainConflictException">
    /// The business is not <see cref="BusinessStatus.Deactivated"/>. Nothing is committed.
    /// </exception>
    public async Task<PlatformBusinessMember?> ReleaseMemberAsync(
        int businessId,
        int membershipId,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        var applied = await ApplyAsync(
            businessId,
            BusinessLifecyclePolicy.AllowsMemberAccess,
            BusinessLifecyclePolicy.MembersUnavailableMessage,
            async (current, occurredAtUtc, token) =>
            {
                var member = current.Members.SingleOrDefault(candidate => candidate.MembershipId == membershipId);
                if (member is null)
                {
                    return null;
                }

                if (!member.IsActive)
                {
                    // Already released, by this administrator a moment ago or by the business's own
                    // Owner before it was switched off. The row is reported as it stands, with no
                    // second write and no audit event: rewriting its state-change instant would
                    // claim a transition that never happened (issue #522).
                    return new Applied<PlatformBusinessMember>(
                        PlatformBusinessLifecycleChange.MemberReleased,
                        current.Status,
                        current.Status,
                        membershipId,
                        InvitesCancelled: null,
                        occurredAtUtc,
                        Changed: false,
                        member);
                }

                await _store
                    .ReleaseMemberAsync(current.BusinessId, membershipId, occurredAtUtc, token)
                    .ConfigureAwait(false);

                return new Applied<PlatformBusinessMember>(
                    PlatformBusinessLifecycleChange.MemberReleased,
                    current.Status,
                    current.Status,
                    membershipId,
                    InvitesCancelled: null,
                    occurredAtUtc,
                    Changed: true,
                    member with { IsActive = false });
            },
            cancellationToken).ConfigureAwait(false);

        return Audited(actor, businessId, applied);
    }

    /// <summary>
    /// Every membership of one deactivated business, so the Super Admin can see who is still
    /// attached to it and release them.
    ///
    /// <para>It takes no transaction and emits no audit event, for the reason the business directory
    /// read beside it does neither: it is a read, and the status gate is the whole of what decides
    /// whether it may answer.</para>
    /// </summary>
    /// <returns>
    /// The business's memberships, active and inactive alike, or <see langword="null"/> when no such
    /// business exists.
    /// </returns>
    /// <exception cref="DomainConflictException">
    /// The business is not <see cref="BusinessStatus.Deactivated"/>, so its members are its own
    /// Owner's business and are not listed here.
    /// </exception>
    public async Task<IReadOnlyList<PlatformBusinessMember>?> ListMembersAsync(
        int businessId,
        CancellationToken cancellationToken)
    {
        var business = await _store.FindLifecycleTargetAsync(businessId, cancellationToken)
            .ConfigureAwait(false);
        if (business is null)
        {
            return null;
        }

        return BusinessLifecyclePolicy.AllowsMemberAccess(business.Status)
            ? business.Members
            : throw new DomainConflictException(BusinessLifecyclePolicy.MembersUnavailableMessage);
    }

    /// <summary>
    /// The frame all three actions share: the pre-transaction read that answers "no such business",
    /// the status gate, issue #522's write transaction, the authoritative re-read inside it, and the
    /// same gate again on what the re-read found.
    /// </summary>
    /// <param name="businessId">The business the action is about.</param>
    /// <param name="permitted">The status this action may start from.</param>
    /// <param name="refusal">The sentence a business in any other status is refused with.</param>
    /// <param name="write">
    /// The action itself, given the business as the in-transaction read found it and the instant to
    /// record. It is sampled inside the lock rather than when the request arrived, so the rows record
    /// when the change really happened rather than when the caller started waiting for it. Returning
    /// <see langword="null"/> means "not found" and commits nothing of its own.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    private async Task<Applied<T>?> ApplyAsync<T>(
        int businessId,
        Func<BusinessStatus, bool> permitted,
        string refusal,
        Func<PlatformBusinessLifecycleTarget, DateTime, CancellationToken, Task<Applied<T>?>> write,
        CancellationToken cancellationToken)
        where T : class
    {
        var existing = await _store.FindLifecycleTargetAsync(businessId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        Require(permitted, existing.Status, refusal);

        // No membership is activated by any of these three, so there is no identity for
        // MembershipEligibility to be re-checked for, and the Owner rule a reactivation needs is
        // not the guard's "keep an Owner" one - see the remarks on this class. What the guard is
        // here for is its BEGIN IMMEDIATE transaction, which is the one write path a membership
        // change may happen on (issue #522).
        var intent = default(MembershipWriteIntent);

        return await _guard
            .ExecuteAsync(
                intent,
                async token =>
                {
                    var current = await _store.FindLifecycleTargetAsync(businessId, token)
                        .ConfigureAwait(false);
                    if (current is null)
                    {
                        // The business row stopped existing between the two reads. There is no path
                        // that deletes one, so this is unreachable in practice; answering "not
                        // found" rather than inventing a result is the fail-closed direction.
                        return null;
                    }

                    Require(permitted, current.Status, refusal);

                    return await write(current, _clock.UtcNow, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Records the one audit event for a committed change and answers with what the action
    /// produced. A refused action never reaches here - the exception rolled its transaction back -
    /// and an action that changed nothing is answered without an event.
    /// </summary>
    private T? Audited<T>(ActorIdentity actor, int businessId, Applied<T>? applied)
        where T : class
    {
        if (applied is null)
        {
            return null;
        }

        if (applied.Changed)
        {
            _audit.Record(new PlatformBusinessLifecycleAuditEntry(
                actor.DirectoryTenantId,
                actor.ObjectId,
                businessId,
                applied.Change,
                applied.FromStatus,
                applied.ToStatus,
                applied.MembershipId,
                applied.InvitesCancelled,
                applied.OccurredAtUtc));
        }

        return applied.Result;
    }

    /// <summary>
    /// Read before anything else, so an action can never be committed for a request whose actor the
    /// audit trail could not name. The policy at the boundary has already established that this is
    /// the configured platform administrator; this is who they are.
    /// </summary>
    private ActorIdentity RequireActor() =>
        _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

    private static void Require(Func<BusinessStatus, bool> permitted, BusinessStatus status, string refusal)
    {
        if (!permitted(status))
        {
            throw new DomainConflictException(refusal);
        }
    }

    /// <summary>
    /// The business's memberships as <see cref="BusinessOwnerRetention"/> reads them: the stored
    /// role and whether the membership is active, and nothing else.
    /// </summary>
    private static IReadOnlyCollection<BusinessMembershipRole> OwnerView(PlatformBusinessLifecycleTarget target) =>
        [.. target.Members.Select(member => new BusinessMembershipRole(member.StoredRole, member.IsActive))];

    /// <summary>What the in-transaction write actually did, so the audit can describe it exactly.</summary>
    private sealed record Applied<T>(
        PlatformBusinessLifecycleChange Change,
        BusinessStatus FromStatus,
        BusinessStatus ToStatus,
        int? MembershipId,
        int? InvitesCancelled,
        DateTime OccurredAtUtc,
        bool Changed,
        T Result);
}
