using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// What every member change of issue #533 has in common: resolve the business from the caller's own
/// membership, open issue #522's shared write transaction, re-read the target inside it, write only
/// what actually differs, and audit the change once it is committed.
///
/// It exists so the three use cases beside it hold their own rule and nothing else. The parts
/// collected here are the parts that must not vary between them - a change that read the membership
/// outside the transaction, or audited before the commit, or forgot to say which rules apply, would
/// be a quiet hole in exactly one of the three.
///
/// <para>Since issue #680 every change also declares the business that must still be
/// <see cref="BusinessStatus.Active"/>, which the guard re-reads inside the same transaction. The
/// business id comes from the middleware's resolution when the request arrived, so without that
/// re-check a reinstatement already in flight could re-activate a membership the Super Admin had
/// just released from the business they were deactivating.</para>
///
/// <para><strong>The authoritative read is inside the transaction.</strong> The mutable facts a
/// change is decided from - the member's current role and whether their approval is current - are
/// re-read after <c>BEGIN IMMEDIATE</c> has taken the write lock, so the comparison and the write
/// are one operation and a concurrent change cannot land between them. The read the use case is
/// handed before the transaction opens is used for two immutable facts only: the Entra identity the
/// eligibility rule is re-checked for, and whether that business holds this membership at all.</para>
///
/// <para><strong>So is the clock</strong> (issue #681, the pattern issue #608 established for the
/// invite redemption). The instant a change is stamped with, and audited at, is sampled after
/// <c>BEGIN IMMEDIATE</c> has taken the write lock, because the only interesting thing that can
/// happen before that is a wait: an instant read before it would describe when the request arrived
/// rather than when the business actually changed.</para>
///
/// <para><strong>The audit event is emitted after the commit, and only for a real change.</strong>
/// A refused change was rolled back and did not happen; a request that asked for the state the
/// membership already held changed nothing. Recording either as a change would make the audit trail
/// describe a history the database does not have.</para>
/// </summary>
public sealed class MemberChangeWriter
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IBusinessMemberStore _store;
    private readonly MembershipWriteGuard _guard;
    private readonly IMemberManagementAudit _audit;
    private readonly IClock _clock;

    public MemberChangeWriter(
        ICurrentBusinessProvider currentBusiness,
        IAuthenticatedActorAccessor actorAccessor,
        IBusinessMemberStore store,
        MembershipWriteGuard guard,
        IMemberManagementAudit audit,
        IClock clock)
    {
        _currentBusiness = currentBusiness;
        _actorAccessor = actorAccessor;
        _store = store;
        _guard = guard;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Applies one member change.
    /// </summary>
    /// <param name="membershipId">
    /// The membership to change, looked up only within the caller's own business - so another
    /// business's key, and a key that exists nowhere, are the same answer.
    /// </param>
    /// <param name="change">
    /// Which change this is. It decides which rules <see cref="MembershipWriteGuard"/> re-checks,
    /// and it is stated by the operation rather than inferred from the rows that changed, for the
    /// same reason <see cref="MembershipWriteIntent"/> is.
    /// </param>
    /// <param name="target">
    /// The state the operation wants the membership left in, decided from the authoritative
    /// in-transaction read. It may refuse the change by throwing
    /// <see cref="DomainConflictException"/>, which rolls the transaction back.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    /// <returns>
    /// The member as the change left them, or <see langword="null"/> when the caller's business
    /// holds no such membership.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">The caller has no usable membership.</exception>
    /// <exception cref="DomainConflictException">
    /// A membership rule refused the change: the identity already holds an active membership
    /// elsewhere, the business would be left without an active Owner, or the operation's own rule
    /// refused. Nothing is committed in any of those cases.
    /// </exception>
    public async Task<BusinessMember?> ApplyAsync(
        int membershipId,
        MemberChange change,
        Func<MemberMembership, MemberTargetState> target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = await _store.FindAsync(businessId, membershipId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var intent = IntentFor(change, businessId, existing);

        // Deliberately no instant read here (issue #608's pattern, applied to a member change by
        // issue #681). Everything this change is stamped and audited by is sampled inside the
        // transaction below, because the only interesting thing that can happen between here and
        // there is a wait: the caller may be held at BEGIN IMMEDIATE for as long as another writer
        // holds the write lock, and a row stamped from before that wait records a state it reached
        // at some other time.
        var applied = await _guard
            .ExecuteAsync(
                intent,
                token => WriteAsync(businessId, membershipId, target, token),
                cancellationToken)
            .ConfigureAwait(false);

        if (applied is null)
        {
            // The row stopped being this business's between the two reads. There is no path that
            // deletes or re-owns a membership, so this is unreachable in practice; answering "not
            // found" rather than inventing a result is the fail-closed direction if it ever is.
            return null;
        }

        if (applied.Changed)
        {
            var actor = _actorAccessor.GetCurrentActor().Actor
                ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

            _audit.Record(new MemberChangeAuditEntry(
                actor.DirectoryTenantId,
                actor.ObjectId,
                businessId.Value,
                membershipId,
                change,
                applied.PreviousRole,
                applied.NewRole,
                applied.PreviouslyActive,
                applied.NowActive,
                // The instant the change was really made at, sampled with the write lock held, so
                // the trail describes the change rather than the wait that preceded it.
                applied.OccurredAtUtc));
        }

        return applied.Member;
    }

    /// <summary>
    /// The in-transaction half: the authoritative read, the operation's decision measured against
    /// it, and the write of whichever column differs.
    ///
    /// <para><strong>The instant is sampled here, and that is the point</strong> (issue #681,
    /// applying the pattern issue #608 established for the invite redemption). This method runs with
    /// the <c>BEGIN IMMEDIATE</c> write lock already held, so it is the first instant at which this
    /// request could possibly act - however long it waited for the lock. Reading the clock before
    /// the wait stamps <c>StatusChangedAtUtc</c>, and audits the change, with a time that had
    /// already passed by the time the row was written.</para>
    /// </summary>
    private async Task<Applied?> WriteAsync(
        BusinessId businessId,
        int membershipId,
        Func<MemberMembership, MemberTargetState> target,
        CancellationToken cancellationToken)
    {
        var occurredAtUtc = _clock.UtcNow;

        var current = await _store.FindAsync(businessId, membershipId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        var wanted = target(current);

        var roleChanges = wanted.Role != current.StoredRole;
        var stateChanges = wanted.IsActive != current.Member.IsActive;

        if (roleChanges)
        {
            await _store
                .ChangeRoleAsync(businessId, membershipId, wanted.Role, cancellationToken)
                .ConfigureAwait(false);
        }

        if (stateChanges)
        {
            await _store
                .ChangeActiveAsync(businessId, membershipId, wanted.IsActive, occurredAtUtc, cancellationToken)
                .ConfigureAwait(false);
        }

        return new Applied(
            current.Member with
            {
                Role = BusinessRoles.IsSupported(wanted.Role) ? wanted.Role : null,
                IsActive = wanted.IsActive,
            },
            roleChanges || stateChanges,
            current.StoredRole,
            wanted.Role,
            current.Member.IsActive,
            wanted.IsActive,
            occurredAtUtc);
    }

    /// <summary>
    /// Which rules the shared write path re-checks, derived from what the change is rather than
    /// from the rows it touches.
    ///
    /// A role change and a revocation can both leave a business without an active Owner, so both
    /// declare the business that must keep one; neither activates anything, so neither runs the
    /// eligibility check. A reinstatement is the mirror image: it gives an identity an active
    /// membership, so eligibility is re-checked for that identity with its own row excluded, and it
    /// cannot remove an Owner, so the Owner rule has nothing to judge.
    ///
    /// All three additionally declare the business that must still be <see cref="BusinessStatus.Active"/>
    /// (issue #680). Changing who may do what inside a business is only meaningful while that
    /// business trades, and a reinstatement in flight when the Super Admin deactivates and releases
    /// its members would otherwise re-activate a membership that had just been released.
    /// </summary>
    private static MembershipWriteIntent IntentFor(
        MemberChange change,
        BusinessId businessId,
        MemberMembership existing) =>
        change switch
        {
            MemberChange.Reinstated => new MembershipWriteIntent(
                IdentityGainingAnActiveMembership: existing.Identity
                    // Only a hand-written row can store a pair that is not an Entra identity, and
                    // such a row can never resolve at sign-in anyway. Reinstating it would mean
                    // skipping the one-active-membership check entirely, so it is refused instead.
                    ?? throw new DomainConflictException(UnusableStoredIdentityMessage),
                ExcludedMembershipId: existing.Member.MembershipId,
                BusinessThatMustStillBeActive: businessId),
            _ => new MembershipWriteIntent(
                BusinessKeepingAnActiveOwner: businessId,
                BusinessThatMustStillBeActive: businessId),
        };

    /// <summary>
    /// The refusal for a membership whose stored Entra pair is not an identity. It describes the
    /// record rather than the person, and names no business, member or role. Published for the same
    /// reason the other two refusals are: it is the sentence the <c>409</c> response body carries.
    /// </summary>
    public const string UnusableStoredIdentityMessage =
        "This membership's stored sign-in identity is not usable, so it cannot be reinstated.";

    /// <summary>
    /// What the in-transaction write actually did, so the audit can describe it exactly - including
    /// the instant it was made at, which is sampled with the write lock held and is the instant the
    /// row and the audit both record.
    /// </summary>
    private sealed record Applied(
        BusinessMember Member,
        bool Changed,
        BusinessRole PreviousRole,
        BusinessRole NewRole,
        bool PreviouslyActive,
        bool NowActive,
        DateTime OccurredAtUtc);
}
