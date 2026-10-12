using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Onboarding;

/// <summary>
/// The application a person submitted, exactly as it arrived and before any rule has looked at it
/// (issue #541).
///
/// Every member is nullable on purpose, for the reason <c>BusinessSettingsSubmission</c>'s are:
/// "the caller did not send this" has to be distinguishable from "the caller sent an empty value",
/// and both are refusals rather than silent defaults - a missing <see cref="GstRegistered"/> that
/// bound to <see langword="false"/> would record a tax fact nobody stated. It carries no business
/// identifier and no status: which business this becomes and which status it starts in are the
/// server's to decide, not a value a request may name.
/// </summary>
public sealed record BusinessApplicationSubmission(
    string? Name,
    string? ContactEmail,
    string? Abn,
    bool? GstRegistered,
    string? TimeZoneId);

/// <summary>
/// The outcome of submitting a business application (issue #541): it was recorded, this deployment
/// has no such endpoint, or the applicant has submitted too many too recently.
///
/// The flag and the rate limit are reported as results rather than thrown, for the reason
/// <c>NayaxConnectionTestOutcome</c> reports its own limit that way: both are expected answers the
/// HTTP boundary turns into a specific status - a bare <c>404</c> and a <c>429</c> with
/// <c>Retry-After</c> - rather than failures another caller would want to catch. The refusals that
/// *are* exceptions are the ones with a caller-safe sentence: a validation failure and a conflict.
/// </summary>
public sealed record BusinessApplicationOutcome
{
    private BusinessApplicationOutcome(BusinessApplication? application, TimeSpan? retryAfter, bool enabled)
    {
        Application = application;
        RetryAfter = retryAfter;
        Enabled = enabled;
    }

    /// <summary>
    /// Whether self-service onboarding is switched on at all. When it is not, nothing was read,
    /// validated or written, and the boundary answers as though the endpoint did not exist.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// The application as it now stands - freshly created, or the one that was already waiting -
    /// and <see langword="null"/> when the submission was refused.
    /// </summary>
    public BusinessApplication? Application { get; }

    /// <summary>
    /// How long until this applicant may submit again, or <see langword="null"/> when the
    /// submission was not rate-limited.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The application the submission produced, or the one it matched.</summary>
    public static BusinessApplicationOutcome Recorded(BusinessApplication application) =>
        new(application, null, enabled: true);

    public static BusinessApplicationOutcome RateLimited(TimeSpan retryAfter) =>
        new(null, retryAfter, enabled: true);

    /// <summary>Self-service onboarding is switched off for this deployment.</summary>
    public static BusinessApplicationOutcome Disabled { get; } = new(null, null, enabled: false);
}

/// <summary>
/// Applies for a new business (issue #541, one slice of #507 under #328): it creates a
/// <see cref="BusinessStatus.Pending"/> business and the applicant's own active Owner membership,
/// so somebody signing in for the first time can ask to start trading and wait for the Super
/// Admin's decision.
///
/// <para><strong>It is behind a flag that is off by default.</strong>
/// <see cref="ISelfServiceOnboardingFlag"/> is read first, before anything is validated, read or
/// written, and a deployment with onboarding off answers as though this endpoint did not exist.
/// The flag is one port, so it is one decision: the same value is what
/// <c>GET /api/me/account-state</c> reports as <c>onboardingEnabled</c>, so the screen that offers
/// "Start a new business" and the endpoint behind it cannot disagree. The HTTP boundary reads the
/// same port once more, before MVC binds the request body
/// (<c>InventoryApi.Http.SelfServiceOnboardingGateAttribute</c>), because a body that cannot be bound
/// never reaches this use case; this check stays so the use case is closed whoever calls it.</para>
///
/// <para><strong>Nothing about the new tenant comes from tenancy resolution, because there is
/// nothing to resolve.</strong> This is the second request path reached by a caller with no
/// business at all, after the invite redemption: the endpoint carries
/// <c>MembershipNotRequiredEndpointAttribute</c>, so the request keeps authentication and the
/// delegated scope but runs with a <em>denied</em> <c>BusinessScope</c>. The query filters and
/// <c>BusinessOwnershipEnforcer</c> stay fully in force, and the two tables this writes - businesses
/// and memberships - are the structurally non-tenant-owned ones. The business id is the one the
/// database assigns to the row this creates, and for a resubmission it comes from the applicant's
/// own membership row; it is never a value a request supplied, and it never reaches a
/// response.</para>
///
/// <para><strong>Eligibility is <see cref="MembershipEligibility"/> and nothing else.</strong> An
/// applicant becomes the new business's Owner, so applying is a membership gain, and the states the
/// issue lists as eligible - no membership, a revoked one (including somebody released from a
/// deactivated business) and a rejected application - are exactly the states with no active
/// membership. A caller who holds one anywhere, in an Active, Pending or Deactivated business
/// alike, is refused with a conflict. The authoritative check is
/// <see cref="MembershipWriteGuard"/>'s, re-made inside the write transaction; the one here runs
/// first so a caller who cannot possibly apply is refused before an allowance is spent on them, and
/// because it is the only place the pending-application and deactivated-business sentences can be
/// chosen from.</para>
///
/// <para><strong>Resubmitting the same form returns the application already waiting.</strong> A
/// lost response or a double submission must not create a second business or refuse the person who
/// is simply looking at the same screen again, so an identical submission while an application is
/// Pending is answered with that application and writes nothing - not even an audit event, because
/// nothing happened. A submission that *differs* is refused: changing details an operator is
/// reviewing is a different request, and this slice does not implement it. That comparison writes
/// nothing at all, so there is nothing about it to serialise; what has to be one operation is the
/// creation, and that is what runs inside the guard's transaction. Two <em>simultaneous</em>
/// identical submissions are therefore answered either with two successes reporting the one
/// application - the second read ran after the first commit and found it - or with one success and
/// one conflict - the second read ran before it, so the creation reached the guard and its re-check
/// refused it. Both are correct answers to one form submitted twice, and neither can leave two
/// tenants: what is fixed either way is that exactly one business with exactly one active Owner
/// membership exists afterwards, and a refused attempt leaves no business row behind.</para>
///
/// <para><strong>The new business starts empty.</strong> No machine, product, purchase, sale, site,
/// invite or Nayax connection record is created, so its Nayax status is <c>NotConfigured</c> by
/// virtue of nothing being stored, and no decision instant or rejection reason is written - nobody
/// has decided anything. Approving or rejecting it is #509's work, and until then no endpoint
/// serves its data: only <see cref="BusinessStatus.Active"/> resolves, so the applicant's very next
/// request is answered with the ordinary <c>403</c> and their own account state reports
/// <see cref="AccountState.ApplicationPending"/>.</para>
///
/// <para><strong>Nothing about the existing businesses is touched.</strong> This use case reads no
/// other business, changes no membership but the applicant's own new one, and cannot reach a
/// tenant-owned row at all.</para>
/// </summary>
public sealed class ApplyForBusiness
{
    private readonly ISelfServiceOnboardingFlag _onboarding;
    private readonly IActorProfileAccessor _actorProfileAccessor;
    private readonly IBusinessMembershipStore _membershipStore;
    private readonly IBusinessApplicationStore _store;
    private readonly BusinessApplicationRateLimiter _rateLimiter;
    private readonly MembershipWriteGuard _guard;
    private readonly IBusinessApplicationAudit _audit;
    private readonly IClock _clock;

    public ApplyForBusiness(
        ISelfServiceOnboardingFlag onboarding,
        IActorProfileAccessor actorProfileAccessor,
        IBusinessMembershipStore membershipStore,
        IBusinessApplicationStore store,
        BusinessApplicationRateLimiter rateLimiter,
        MembershipWriteGuard guard,
        IBusinessApplicationAudit audit,
        IClock clock)
    {
        _onboarding = onboarding;
        _actorProfileAccessor = actorProfileAccessor;
        _membershipStore = membershipStore;
        _store = store;
        _rateLimiter = rateLimiter;
        _guard = guard;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Submits <paramref name="submission"/> as the authenticated caller's own business
    /// application.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The submission breaks one of <see cref="BusinessApplicationPolicy"/>'s rules, or - from the
    /// central enforcer on the way into the database - names a time zone the host cannot resolve.
    /// Nothing is written.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is nobody to record an application for.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The caller already holds an active membership, or has an application waiting that this
    /// submission does not match, or a membership rule refused the write. Nothing is committed.
    /// </exception>
    public async Task<BusinessApplicationOutcome> Handle(
        BusinessApplicationSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // First, and before anything is read or validated. A deployment with onboarding off has no
        // such endpoint, and must not answer a probe one way for a well-formed body and another way
        // for a malformed one.
        if (!_onboarding.IsEnabled)
        {
            return BusinessApplicationOutcome.Disabled;
        }

        // The identity and the display-only profile come from one principal, so the membership row
        // that is created and the claims it is created from cannot be two different people's
        // (issue #524).
        var actor = _actorProfileAccessor.GetCurrentActorProfile()
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        // Validation before the allowance is claimed: a refused form writes nothing and takes no
        // write lock, so a person correcting a typo must not be the one who runs out of attempts.
        var submitted = BusinessApplicationPolicy.Validate(
            submission.Name,
            submission.ContactEmail,
            submission.Abn,
            submission.GstRegistered,
            submission.TimeZoneId);

        // Claimed before the membership read and the write, so several simultaneous submissions cost
        // one allowance each rather than all passing a count nothing has incremented yet.
        if (!_rateLimiter.TryClaim(actor.Identity, out var retryAfter))
        {
            return BusinessApplicationOutcome.RateLimited(retryAfter);
        }

        if (await FindResubmissionOrRefuseAsync(actor.Identity, submitted, cancellationToken)
            .ConfigureAwait(false) is { } waiting)
        {
            // The application they already have, reported back unchanged. Nothing was written, so
            // nothing is audited: one application is one audit event.
            return BusinessApplicationOutcome.Recorded(waiting);
        }

        var created = await _guard
            .ExecuteAsync(
                // The applicant gains an active membership, so eligibility is re-checked for them
                // inside the transaction. No exclusion: the membership this creates is a new row
                // that cannot count against itself. No business has to keep an Owner either - the
                // business this creates has one, and the write cannot remove anybody else's.
                new MembershipWriteIntent(IdentityGainingAnActiveMembership: actor.Identity),
                // The instant is sampled inside the transaction, after the write lock was granted
                // (issue #608's lesson): it is stamped on the business and the membership alike, so
                // the rows record when the application really happened rather than when the request
                // arrived and started waiting.
                writeToken => _store.CreateAsync(
                    submitted,
                    actor.Identity,
                    actor.Profile,
                    _clock.UtcNow,
                    writeToken),
                cancellationToken)
            .ConfigureAwait(false);

        // After the commit, because a rolled-back application did not happen. The event carries the
        // actor and the new business id, and structurally no name and no email address - see
        // BusinessApplicationAuditEntry.
        _audit.Record(new BusinessApplicationAuditEntry(
            actor.Identity.DirectoryTenantId,
            actor.Identity.ObjectId,
            created.BusinessId.Value,
            created.AppliedAtUtc ?? _clock.UtcNow));

        return BusinessApplicationOutcome.Recorded(created);
    }

    /// <summary>
    /// The application this identity already has waiting and has just submitted again, or nothing
    /// when they are free to apply - and a refusal when they are neither.
    ///
    /// <para>The records are read once and answer both questions, the way the invite redemption's
    /// immediate refusal does: <see cref="MembershipEligibility"/> decides whether to refuse, and
    /// <see cref="AccountStatePolicy"/> decides which sentence. They have to come from one read,
    /// because a caller attached to a deactivated business told "you are already a member of a
    /// business" would be sent looking for an Owner who cannot be reached.</para>
    /// </summary>
    private async Task<BusinessApplication?> FindResubmissionOrRefuseAsync(
        ActorIdentity identity,
        BusinessSettings submitted,
        CancellationToken cancellationToken)
    {
        var memberships = await _membershipStore
            .FindMembershipsAsync(identity, cancellationToken)
            .ConfigureAwait(false);

        if (MembershipEligibility.AllowsActiveMembership(memberships))
        {
            return null;
        }

        var state = AccountStatePolicy.Determine(memberships).State;

        if (state == AccountState.ApplicationPending)
        {
            // That state is reached only for exactly one active membership whose business is
            // Pending, and the status came from the same statement as the membership, so this is
            // the business the application is - not a choice between candidates.
            var pending = await _store
                .FindAsync(memberships.Single(membership => membership.IsActive).BusinessId, cancellationToken)
                .ConfigureAwait(false);

            // The status is re-read here and checked again deliberately: a row that stopped being
            // Pending between the two statements is refused rather than reported back as the
            // application that is waiting, which is the fail-closed direction.
            if (pending is { Status: BusinessStatus.Pending }
                && BusinessApplicationPolicy.IsResubmission(pending.Settings, submitted))
            {
                return pending;
            }
        }

        throw new DomainConflictException(BusinessApplicationPolicy.IneligibleMessage(state));
    }
}
