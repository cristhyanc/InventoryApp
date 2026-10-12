using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// What one completed redemption produced (issue #532): the role the caller now holds.
///
/// It deliberately carries no business name and no business id. The caller is a member from this
/// moment on and reads their business's own name from <c>GET /api/me/access</c> like every other
/// member; naming it here would mean the redemption endpoint - the one endpoint a person with no
/// membership can call - published a business, which is exactly what its refusals must not do
/// either. The membership key is left out for the same reason it is left out of every other
/// self-describing response: the caller has nothing to do with it.
/// </summary>
/// <param name="Role">The role the invite granted, which is what the joining person is told.</param>
public sealed record RedeemedMemberInvite(BusinessRole Role);

/// <summary>
/// Redeems one invite link for the signed-in person who opened it (issue #532, one slice of #504
/// under #328): it creates their membership, or brings a revoked one back, and marks the invite
/// used - atomically.
///
/// <para><strong>It is the one path reached by a caller with no business.</strong> The endpoint
/// carries <c>MembershipNotRequiredEndpointAttribute</c>, so the request keeps authentication and
/// the delegated scope but runs with a <em>denied</em> business scope: nothing resolved a business
/// for it, and nothing may. The business this use case writes in therefore comes from the invite
/// row the presented token identifies, through <see cref="IMemberInviteRedemptionStore"/>, and never
/// from the request - there is no business id, no business name and no membership id in the
/// contract at all. That is also why the invite table is structurally non-tenant-owned
/// (docs/architecture.md § Tenant ownership): an invite found by its token under a denied scope is
/// the one thing a central query filter cannot express.</para>
///
/// <para><strong>Eligibility is <see cref="MembershipEligibility"/> and nothing else.</strong> A
/// person may hold one active membership, in any business, so a caller who already holds one is
/// refused - and the state their own account is in chooses only the <em>wording</em>
/// (<see cref="MemberInviteRedemptionPolicy.IneligibleMessage"/>), never whether the refusal
/// happens. The authoritative check is <see cref="MembershipWriteGuard"/>'s, re-made inside the
/// write transaction; the one here runs first so a caller who cannot possibly redeem is refused
/// before any invite is looked up, and because it is the only place the deactivated-business
/// sentence can be chosen from.</para>
///
/// <para><strong>The invite is read, judged and closed inside one transaction, and so is the
/// clock.</strong> The authoritative read happens after <c>BEGIN IMMEDIATE</c> has taken the write
/// lock, and the <c>UPDATE</c> that marks the invite used carries its own "only while still live"
/// condition, so the decision and the mutation are one operation twice over. A second redemption of
/// the same invite therefore matches nothing and is refused with its membership write rolled back,
/// and two invites redeemed at once by one identity leave exactly one membership and one unused
/// invite. The <em>instants</em> both of those are judged at are read inside the transaction too
/// (issue #608): the decision at the moment the lock was granted, and the consumption immediately
/// before the close. An invite that expires while this request waits for the write lock, or while
/// its membership is being prepared, is therefore refused rather than admitted on the strength of
/// an instant from before the wait - and what the row and the audit trail record is when the
/// redemption happened, not when the request arrived.</para>
///
/// <para><strong>Every refusal is one of two sentences.</strong> An unusable invite - expired, used,
/// cancelled, unknown, of a business that is not active, or granting a role this deployment does
/// not declare - is <see cref="MemberInviteRedemptionPolicy.InvalidInviteMessage"/>, so none of
/// those cases can be told apart by whoever submitted the token and none of them reveals a
/// business. An ineligible caller gets a conflict. Nothing is written in either case.</para>
/// </summary>
public sealed class RedeemMemberInvite
{
    private readonly IActorProfileAccessor _actorProfileAccessor;
    private readonly IBusinessMembershipStore _membershipStore;
    private readonly IMemberInviteRedemptionStore _store;
    private readonly MembershipWriteGuard _guard;
    private readonly IMemberInviteAudit _audit;
    private readonly IClock _clock;

    public RedeemMemberInvite(
        IActorProfileAccessor actorProfileAccessor,
        IBusinessMembershipStore membershipStore,
        IMemberInviteRedemptionStore store,
        MembershipWriteGuard guard,
        IMemberInviteAudit audit,
        IClock clock)
    {
        _actorProfileAccessor = actorProfileAccessor;
        _membershipStore = membershipStore;
        _store = store;
        _guard = guard;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Redeems <paramref name="token"/> for the authenticated caller.
    /// </summary>
    /// <param name="token">
    /// The plaintext invite token from the link. It is hashed here and the hash is all that crosses
    /// the persistence port, so no adapter, row or log sees the plaintext - the same guarantee the
    /// creation side gives. A missing or blank token is refused with the one generic invalid-invite
    /// message rather than a different one, so the endpoint cannot be used to tell a malformed
    /// submission from an unknown token.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    /// <returns>The role the caller now holds.</returns>
    /// <exception cref="DomainValidationException">
    /// The invite cannot be used. Nothing is written.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is nobody to create a membership for.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The caller already holds an active membership, or a membership rule refused the write.
    /// Nothing is committed.
    /// </exception>
    public async Task<RedeemedMemberInvite> Handle(string? token, CancellationToken cancellationToken)
    {
        // Before anything is read: a blank token identifies no invite, and hashing it would produce
        // a perfectly valid-looking lookup key for the empty string.
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new DomainValidationException(MemberInviteRedemptionPolicy.InvalidInviteMessage);
        }

        var tokenHash = MemberInviteToken.Hash(token);

        // The identity and the display-only profile come from one principal, so the row that is
        // created and the claims it is created from cannot be two different people's (issue #524).
        var actor = _actorProfileAccessor.GetCurrentActorProfile()
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        await RefuseIfIneligibleAsync(actor.Identity, cancellationToken).ConfigureAwait(false);

        // Deliberately no instant read here (issue #608). Everything this request is judged and
        // stamped by is sampled inside the transaction below, because the only interesting thing
        // that can happen between here and there is a wait: the caller who is eligible now may be
        // held at BEGIN IMMEDIATE for as long as another writer holds the write lock.
        var redeemed = await _guard
            .ExecuteAsync(
                // The caller gains an active membership, so eligibility is re-checked for them
                // inside the transaction. No exclusion: a redemption either creates a row or
                // reactivates a revoked one, and neither is an active row that could count against
                // itself. No business has to keep an Owner either - a redemption can only add
                // access, never remove an Owner's.
                new MembershipWriteIntent(IdentityGainingAnActiveMembership: actor.Identity),
                writeToken => RedeemAsync(tokenHash, actor, writeToken),
                cancellationToken)
            .ConfigureAwait(false);

        // After the commit, because a rolled-back redemption did not happen. The event carries the
        // actor, the business, the invite and the role it granted - and structurally no token and no
        // name (see MemberInviteAuditEntry). Its instant is the one the invite was really consumed
        // at, so the trail describes the redemption rather than the wait that preceded it.
        _audit.Record(new MemberInviteAuditEntry(
            actor.Identity.DirectoryTenantId,
            actor.Identity.ObjectId,
            redeemed.BusinessId.Value,
            redeemed.InviteId,
            MemberInviteChange.Redeemed,
            redeemed.Role,
            redeemed.ConsumedAtUtc));

        return new RedeemedMemberInvite(redeemed.Role);
    }

    /// <summary>
    /// The immediate refusal for a caller who cannot gain an active membership at all, and the one
    /// place a refusal's wording is chosen.
    ///
    /// The records are read once and answer both questions: <see cref="MembershipEligibility"/>
    /// decides whether to refuse, and <see cref="AccountStatePolicy"/> decides which sentence. They
    /// have to come from one read, because a deactivated caller told "you are already a member of a
    /// business" would be sent looking for an Owner who cannot be reached.
    /// </summary>
    private async Task RefuseIfIneligibleAsync(ActorIdentity identity, CancellationToken cancellationToken)
    {
        var memberships = await _membershipStore
            .FindMembershipsAsync(identity, cancellationToken)
            .ConfigureAwait(false);

        if (MembershipEligibility.AllowsActiveMembership(memberships))
        {
            return;
        }

        throw new DomainConflictException(
            MemberInviteRedemptionPolicy.IneligibleMessage(AccountStatePolicy.Determine(memberships).State));
    }

    /// <summary>
    /// The in-transaction half: the authoritative read of the invite, the policy applied to it, the
    /// membership created or reactivated, and the invite closed - in that order, so an invite that
    /// stops being live between the read and the close takes the membership write back with it.
    ///
    /// <para><strong>Both instants are sampled here, and that is the point</strong> (issue #608).
    /// This method runs with the <c>BEGIN IMMEDIATE</c> write lock already held, so
    /// <c>decisionAtUtc</c> is the first instant at which this request could possibly act - however
    /// long it waited for the lock. Reading the clock before the wait and judging the invite by that
    /// value is a time-of-check defect: a link that expired during the wait would still satisfy the
    /// old instant and be consumed after expiry. The consumption is then sampled again, immediately
    /// before the conditional close, because preparing the membership takes time too and the close
    /// is the moment the invite is actually spent.</para>
    /// </summary>
    private async Task<Redeemed> RedeemAsync(
        string tokenHash,
        ActorProfile actor,
        CancellationToken cancellationToken)
    {
        var decisionAtUtc = _clock.UtcNow;

        var invite = await _store.FindByTokenHashAsync(tokenHash, cancellationToken).ConfigureAwait(false);

        if (invite is null
            || !MemberInviteRedemptionPolicy.IsRedeemable(
                invite.StoredStatus,
                invite.ExpiresAtUtc,
                invite.BusinessStatus,
                invite.Role,
                decisionAtUtc))
        {
            throw new DomainValidationException(MemberInviteRedemptionPolicy.InvalidInviteMessage);
        }

        var membershipId = await JoinAsync(invite, actor, decisionAtUtc, cancellationToken)
            .ConfigureAwait(false);

        var consumedAtUtc = _clock.UtcNow;

        var closed = await _store
            .MarkUsedAsync(invite.BusinessId, invite.InviteId, membershipId, consumedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        if (!closed)
        {
            // The invite stopped being live between the read above and this statement: another
            // redemption of the same link committed first, it was cancelled, or - judged at the
            // fresh instant this statement carries - it expired while this attempt was preparing
            // the membership. Refusing here rolls that membership write back with the transaction,
            // whether it created a row or brought a revoked one back, which is what keeps one
            // invite to one membership and keeps an expired link from granting anything: without
            // the condition in that UPDATE, both callers would be let in and the row would record
            // only one of them.
            throw new DomainValidationException(MemberInviteRedemptionPolicy.InvalidInviteMessage);
        }

        return new Redeemed(invite.BusinessId, invite.InviteId, invite.Role, consumedAtUtc);
    }

    /// <summary>
    /// Creates the caller's membership, or reactivates the one this business revoked, with the
    /// invite's role either way.
    ///
    /// A revoked row is reactivated rather than duplicated: the per-(actor, business) unique index
    /// allows one row, and that row is the record that this person was once approved here. An
    /// <em>active</em> row is unreachable - the eligibility rule refused the caller before and
    /// inside the transaction - and is refused rather than quietly re-granted, because reaching it
    /// would mean the rule that is supposed to be authoritative was not.
    ///
    /// <para><paramref name="joinedAtUtc"/> is the instant the transaction's authoritative decision
    /// was made at, so the row records the state it reached when the business really admitted this
    /// person. It is not the instant the invite is consumed at, which is sampled afterwards: this
    /// write may still be rolled back.</para>
    /// </summary>
    private async Task<int> JoinAsync(
        RedeemableMemberInvite invite,
        ActorProfile actor,
        DateTime joinedAtUtc,
        CancellationToken cancellationToken)
    {
        var existing = await _store
            .FindMembershipAsync(invite.BusinessId, actor.Identity, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return await _store
                .CreateActiveMembershipAsync(
                    invite.BusinessId,
                    actor.Identity,
                    actor.Profile,
                    invite.Role,
                    joinedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (existing.Value.IsActive)
        {
            throw new DomainConflictException(MembershipEligibility.AlreadyAMemberMessage);
        }

        var reactivated = await _store
            .ReactivateMembershipAsync(
                invite.BusinessId,
                existing.Value.MembershipId,
                invite.Role,
                actor.Profile,
                joinedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        if (!reactivated)
        {
            // The conditional reactivation matched nothing, so the row is no longer the revoked one
            // this decision was made from. Refusing rolls the transaction back rather than
            // reporting a membership nobody granted.
            throw new DomainConflictException(MembershipEligibility.AlreadyAMemberMessage);
        }

        return existing.Value.MembershipId;
    }

    /// <summary>
    /// What the committed redemption was about, so the audit can describe it exactly - including
    /// the instant the invite was really consumed at, which is the instant the audit reports.
    /// </summary>
    private sealed record Redeemed(
        BusinessId BusinessId,
        int InviteId,
        BusinessRole Role,
        DateTime ConsumedAtUtc);
}
