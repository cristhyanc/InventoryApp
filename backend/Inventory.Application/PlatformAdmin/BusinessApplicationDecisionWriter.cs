using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// What approving and rejecting a business application have in common (issue #544): open issue
/// #522's shared write transaction, re-read the application inside it, refuse unless it is still
/// waiting for a decision with the applicant it had, write the decision, and audit it once it is
/// committed.
///
/// <para>It exists so the two use cases beside it hold their own rule and nothing else - the same
/// reason <c>MemberChangeWriter</c> exists for the three member changes. The parts collected here
/// are the parts that must not vary between them: a decision that read the status outside the
/// transaction, or audited before the commit, or forgot to say which membership rules apply, would
/// be a quiet hole in exactly one of the two.</para>
///
/// <para><strong>The authoritative read is inside the transaction.</strong> The two mutable facts a
/// decision is made from - the business's status and whether the applicant's membership is still
/// active - are re-read after <c>BEGIN IMMEDIATE</c> has taken the write lock, so the comparison and
/// the write are one operation. That is what makes a simultaneous approve and reject resolve to one
/// winner and one <c>409</c> rather than two writes, and it is why the read the use case takes
/// before the transaction opens decides nothing: it exists to answer "no such business" with a
/// <c>404</c>, to refuse a stale view early, and to supply the immutable fact the eligibility
/// re-check needs - the applicant's Entra identity and the membership key that is excluded from
/// it.</para>
///
/// <para><strong>Approving is a membership gain, so eligibility is re-checked for the
/// applicant.</strong> Their membership is already active, but it becomes a membership in a
/// <em>trading</em> business, and a person belongs to one business at a time
/// (<see cref="MembershipEligibility"/>). The check therefore runs with that same membership
/// <em>excluded</em> - without the exclusion the applicant's own row would count against them and
/// no application could ever be approved - and the exclusion is sound because the in-transaction
/// read refuses unless the applicant is still the very membership the exclusion names. Rejecting
/// activates nothing, so it runs no eligibility check, and it requires no active Owner afterwards:
/// a refused application is not a business that has to stay reachable.</para>
///
/// <para><strong>An approval also provisions the business it approves (issue #849).</strong> Inside
/// the same transaction, immediately after the status write,
/// <see cref="IBusinessSupplierProvisioningStore"/> copies the enabled platform supplier presets
/// into the business as its own suppliers and stamps the copy instant on it. It is one operation
/// with the approval on purpose: an approved business that did not get them would be a half-finished
/// tenant, and a copy that committed without the approval would hand suppliers to a business that is
/// still waiting. A failure there therefore rolls the status change back with it and no audit event
/// is emitted. A rejection provisions nothing - there is no business to equip.</para>
///
/// <para><strong>The audit event is emitted after the commit, and carries no name, address or
/// reason text.</strong> See <see cref="PlatformBusinessDecisionAuditEntry"/>.</para>
/// </summary>
public sealed class BusinessApplicationDecisionWriter
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPlatformBusinessDirectoryStore _store;
    private readonly IBusinessSupplierProvisioningStore _provisioning;
    private readonly MembershipWriteGuard _guard;
    private readonly IPlatformBusinessDecisionAudit _audit;
    private readonly IClock _clock;

    public BusinessApplicationDecisionWriter(
        IAuthenticatedActorAccessor actorAccessor,
        IPlatformBusinessDirectoryStore store,
        IBusinessSupplierProvisioningStore provisioning,
        MembershipWriteGuard guard,
        IPlatformBusinessDecisionAudit audit,
        IClock clock)
    {
        _actorAccessor = actorAccessor;
        _store = store;
        _provisioning = provisioning;
        _guard = guard;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Records one decision on one business application.
    /// </summary>
    /// <param name="businessId">
    /// The application being decided. It is the subject of the decision, never a tenancy scope: the
    /// request keeps its denied <c>BusinessScope</c>, and nothing this writes is stamped with it.
    /// </param>
    /// <param name="approved">
    /// <see langword="true"/> to set <see cref="BusinessStatus.Active"/>, <see langword="false"/> to
    /// set <see cref="BusinessStatus.Rejected"/> and release the applicant's membership.
    /// </param>
    /// <param name="reason">
    /// The validated rejection reason, or <see langword="null"/>. It must already have been through
    /// <see cref="BusinessDecisionPolicy.ValidateRejectionReason"/>, and it is always
    /// <see langword="null"/> for an approval - an approved application has nothing to explain.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    /// <returns>
    /// The decision as it was recorded, or <see langword="null"/> when no such business exists.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is nobody to record the decision for.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The application is no longer waiting for a decision, its applicant can no longer be
    /// approved, or the applicant now holds an active membership elsewhere. Nothing is committed.
    /// </exception>
    public async Task<PlatformBusinessDecision?> DecideAsync(
        int businessId,
        bool approved,
        string? reason,
        CancellationToken cancellationToken)
    {
        // Read before anything else, so a decision can never be committed for a request whose actor
        // the audit trail could not name. The policy at the boundary has already established that
        // this is the configured platform administrator; this is who they are.
        var actor = _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        var existing = await _store.FindDecisionTargetAsync(businessId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        // The early refusals. They decide nothing the in-transaction read does not decide again;
        // they exist so a caller looking at a stale list is told so without a write lock being
        // taken on their behalf, and so an approval that cannot name its applicant never reaches
        // the eligibility check at all. The status is judged first: "this application has already
        // been decided" is the fact about a decided business, whatever its membership rows now say.
        if (!BusinessDecisionPolicy.IsDecidable(existing.Status))
        {
            throw new DomainConflictException(BusinessDecisionPolicy.NotPendingMessage);
        }

        var intent = default(MembershipWriteIntent);
        if (approved)
        {
            var applicant = RequireApprovableApplicant(existing);
            intent = new MembershipWriteIntent(
                IdentityGainingAnActiveMembership: applicant.Identity,
                ExcludedMembershipId: applicant.MembershipId);
        }

        var applied = await _guard
            .ExecuteAsync(
                intent,
                token => WriteAsync(businessId, approved, reason, existing, token),
                cancellationToken)
            .ConfigureAwait(false);

        if (applied is null)
        {
            // The business row stopped existing between the two reads. There is no path that
            // deletes one, so this is unreachable in practice; answering "not found" rather than
            // inventing a decision is the fail-closed direction if it ever is.
            return null;
        }

        _audit.Record(new PlatformBusinessDecisionAuditEntry(
            actor.DirectoryTenantId,
            actor.ObjectId,
            businessId,
            applied.FromStatus,
            applied.Decision.Status,
            applied.Decision.RejectionReason is not null,
            applied.Decision.DecidedAtUtc));

        return applied.Decision;
    }

    /// <summary>
    /// The in-transaction half: the authoritative read, the rules measured against it, and the
    /// write.
    ///
    /// <paramref name="expected"/> is the view the caller was handed before the transaction opened.
    /// It is used for one thing only - to insist that the applicant has not changed - because an
    /// exclusion granted for one membership key must never be spent on another.
    /// </summary>
    private async Task<Applied?> WriteAsync(
        int businessId,
        bool approved,
        string? reason,
        PlatformBusinessDecisionTarget expected,
        CancellationToken cancellationToken)
    {
        var current = await _store.FindDecisionTargetAsync(businessId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        if (!BusinessDecisionPolicy.IsDecidable(current.Status))
        {
            throw new DomainConflictException(BusinessDecisionPolicy.NotPendingMessage);
        }

        // Sampled here rather than when the request arrived: the write lock is already held, so the
        // instant the rows record is when the decision really happened rather than when the caller
        // started waiting for it (issue #608's lesson).
        var decidedAtUtc = _clock.UtcNow;

        if (approved)
        {
            var applicant = RequireApprovableApplicant(current);

            if (applicant.MembershipId != expected.Applicant?.MembershipId)
            {
                // A different membership is now the applicant, so the eligibility exclusion this
                // transaction was entered with does not describe this row. Refusing is the only
                // safe answer: proceeding would approve a membership whose one-active-membership
                // check was skipped for somebody else's key.
                throw new DomainConflictException(BusinessDecisionPolicy.ApplicantUnavailableMessage);
            }

            await _store.ApproveAsync(businessId, decidedAtUtc, cancellationToken).ConfigureAwait(false);

            // The new business's own copies of the enabled supplier presets (issue #849), in this
            // same transaction and with the same instant, so an approved business never exists
            // without them: if this throws, the status change rolls back with it, the business is
            // still Pending and no audit event is emitted.
            //
            // The id handed over is current.BusinessId - the key of the row this transaction read -
            // and never the route value the request carried, exactly as the deactivation's invite
            // cancellation hands over the id it read (AGENTS.md § Tenant ownership).
            await _provisioning
                .CopyEnabledSupplierPresetsAsync(current.BusinessId, decidedAtUtc, cancellationToken)
                .ConfigureAwait(false);

            return new Applied(
                current.Status,
                new PlatformBusinessDecision(
                    businessId,
                    current.Name,
                    BusinessStatus.Active,
                    decidedAtUtc,
                    RejectionReason: null));
        }

        // A rejection asks nothing of the applicant: it releases them. An applicant that cannot be
        // identified, or whose membership is already inactive, is therefore not a refusal - there is
        // simply nothing to release, and the state-change instant of a row that did not change must
        // not be rewritten (issue #522).
        var release = current.Applicant is { IsActive: true } releasable ? releasable.MembershipId : (int?)null;

        await _store
            .RejectAsync(businessId, reason, decidedAtUtc, release, cancellationToken)
            .ConfigureAwait(false);

        return new Applied(
            current.Status,
            new PlatformBusinessDecision(
                businessId,
                current.Name,
                BusinessStatus.Rejected,
                decidedAtUtc,
                reason));
    }

    /// <summary>
    /// The applicant an approval may confirm: one identifiable membership, still active, whose
    /// stored Entra pair is a usable identity.
    ///
    /// All three are refused with the one sentence, deliberately. The caller is told that this
    /// application no longer has an approvable applicant; which of the three it is would describe
    /// the membership rows of a business the caller is not a member of.
    /// </summary>
    private static ApprovableApplicant RequireApprovableApplicant(PlatformBusinessDecisionTarget target) =>
        target.Applicant is { IsActive: true, Identity: { } identity } applicant
            ? new ApprovableApplicant(applicant.MembershipId, identity)
            : throw new DomainConflictException(BusinessDecisionPolicy.ApplicantUnavailableMessage);

    private readonly record struct ApprovableApplicant(int MembershipId, ActorIdentity Identity);

    /// <summary>What the in-transaction write actually did, so the audit can describe it exactly.</summary>
    private sealed record Applied(BusinessStatus FromStatus, PlatformBusinessDecision Decision);
}
