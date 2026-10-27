using Inventory.Application.Onboarding;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Onboarding;

/// <summary>
/// The decisions the self-service business application makes (issue #541), over fakes: the order
/// the flag, the validation, the allowance, the eligibility read and the write happen in, what each
/// refusal is, and what is audited.
///
/// <para>The real <see cref="MembershipWriteGuard"/> is used rather than faked, because where the
/// creation happens relative to the transaction and its in-transaction eligibility re-check are
/// exactly what these tests are about. Relational behaviour - the write lock, the filtered index,
/// the rollback - is proved against a real SQLite file in
/// <c>BusinessApplicationConcurrencyTests</c> and <c>EfBusinessApplicationStoreTests</c>.</para>
/// </summary>
public class ApplyForBusinessTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";
    private const string ValidAbn = "51824753556";

    private static readonly DateTime Now = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = new(2026, 10, 11, 1, 0, 0, DateTimeKind.Utc);

    #region The flag

    /// <summary>
    /// With onboarding off the use case does nothing at all: no actor is read, no submission is
    /// validated, no membership is looked up, no allowance is spent and nothing is written. That is
    /// the point of checking it first - a deployment with the feature off must not answer a probe
    /// one way for a well-formed body and another way for a malformed one.
    /// </summary>
    [Fact]
    public async Task With_onboarding_disabled_nothing_is_read_validated_or_written()
    {
        var world = new World { Onboarding = FakeSelfServiceOnboardingFlag.Disabled };

        // Deliberately an invalid submission: it must not be the thing that decides the answer.
        var outcome = await world.Apply(new BusinessApplicationSubmission(null, null, null, null, null, null));

        Assert.False(outcome.Enabled);
        Assert.Null(outcome.Application);
        Assert.Null(outcome.RetryAfter);
        Assert.Equal(0, world.Actors.CallCount);
        Assert.Equal(0, world.Memberships.QueryCount);
        Assert.Empty(world.Store.Operations);
        Assert.Empty(world.WriteStore.Operations);
        Assert.Empty(world.Audit.Entries);
        Assert.Equal(0, world.RateLimiter.TrackedIdentities);
    }

    #endregion

    #region Recording an application

    /// <summary>
    /// The eligible path, and the order it happens in: the caller's records are read first, then the
    /// transaction opens, the eligibility rule is re-checked inside it, the business and the
    /// membership are created, and only then is anything committed.
    /// </summary>
    [Fact]
    public async Task An_eligible_applicant_creates_a_pending_business_inside_the_write_transaction()
    {
        var world = new World();

        var outcome = await world.Apply(Submission());

        Assert.True(outcome.Enabled);
        Assert.NotNull(outcome.Application);
        Assert.Equal(BusinessStatus.Pending, outcome.Application!.Status);
        Assert.Equal(Now, outcome.Application.AppliedAtUtc);

        // One read of the caller's own records, before the transaction, and the write inside it.
        Assert.Equal(1, world.Memberships.QueryCount);
        Assert.Equal(world.Identity, world.Memberships.LastQueriedActor);
        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeBusinessMembershipWriteStore.IdentityRead,
                FakeBusinessApplicationStore.Create,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            world.WriteStore.Operations);

        // The caller's own identity and their token's display values, and the validated settings.
        Assert.Equal(world.Identity, world.Store.CreatedIdentity);
        Assert.Equal("Dana Doe", world.Store.CreatedProfile?.DisplayName);
        Assert.Equal("dana@example.test", world.Store.CreatedProfile?.Email);
        Assert.Equal("Coastal Vending", world.Store.CreatedSettings?.Name);
        Assert.Equal(ValidAbn, world.Store.CreatedSettings?.Abn);
    }

    /// <summary>
    /// One audit event, after the commit, carrying the actor and the new business id - and
    /// structurally nothing else, which is what keeps the business name, the contact address and the
    /// applicant's own name and email out of the trail.
    /// </summary>
    [Fact]
    public async Task A_recorded_application_is_audited_once_with_the_actor_and_the_new_business()
    {
        var world = new World();
        world.Store.CreatedBusinessId = BusinessId.From(42);

        await world.Apply(Submission());

        var entry = Assert.Single(world.Audit.Entries);
        Assert.Equal(world.Identity.DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(world.Identity.ObjectId, entry.ActorObjectId);
        Assert.Equal(42, entry.BusinessId);
        Assert.Equal(Now, entry.OccurredAtUtc);
    }

    /// <summary>
    /// The application instant is sampled <em>inside</em> the transaction, after the write lock was
    /// granted, which is issue #608's lesson applied to this write: the clock moves on while this
    /// caller waits for another writer, and the rows must record when the application really
    /// happened rather than when the request arrived.
    /// </summary>
    [Fact]
    public async Task The_application_instant_is_the_one_the_write_lock_was_granted_at()
    {
        var world = new World();
        var waitedUntil = Now.AddMinutes(3);
        world.WriteStore.WhileWaitingForTheWriteLock = () => world.Clock.UtcNow = waitedUntil;

        var outcome = await world.Apply(Submission());

        Assert.Equal(waitedUntil, world.Store.CreatedAtUtc);
        Assert.Equal(waitedUntil, outcome.Application?.AppliedAtUtc);
        Assert.Equal(waitedUntil, Assert.Single(world.Audit.Entries).OccurredAtUtc);
    }

    #endregion

    #region Validation

    /// <summary>
    /// A refused submission spends no allowance, reads no membership record and writes nothing. The
    /// allowance matters: a person correcting a typo in a form must not be the one who runs out of
    /// attempts.
    /// </summary>
    [Fact]
    public async Task An_invalid_submission_is_refused_before_an_allowance_is_spent()
    {
        var world = new World();

        await Assert.ThrowsAsync<DomainValidationException>(() => world.Apply(
            new BusinessApplicationSubmission("Coastal Vending", "owner@", null, true, "Australia/Sydney", "NSW")));

        Assert.Equal(0, world.RateLimiter.TrackedIdentities);
        Assert.Equal(0, world.Memberships.QueryCount);
        Assert.Empty(world.Store.Operations);
        Assert.Empty(world.WriteStore.Operations);
        Assert.Empty(world.Audit.Entries);
    }

    /// <summary>
    /// A request with no identifiable actor has nobody to record an application for, and is refused
    /// before anything is validated or written.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_identifiable_actor_is_refused()
    {
        var world = new World { Actors = FakeActorProfileAccessor.Unidentifiable() };

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() => world.Apply(Submission()));

        Assert.Equal(0, world.RateLimiter.TrackedIdentities);
        Assert.Empty(world.Store.Operations);
        Assert.Empty(world.WriteStore.Operations);
    }

    #endregion

    #region Eligibility

    /// <summary>
    /// A caller who already holds an active membership is refused with the standing membership
    /// sentence, and nothing is written.
    /// </summary>
    [Fact]
    public async Task A_caller_who_already_holds_an_active_membership_is_refused()
    {
        var world = new World
        {
            Memberships = new FakeBusinessMembershipStore(Membership(7)),
        };

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => world.Apply(Submission()));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);
        Assert.Empty(world.Store.Operations);
        Assert.Empty(world.WriteStore.Operations);
        Assert.Empty(world.Audit.Entries);
    }

    /// <summary>
    /// Two active memberships - the ambiguous account only legacy data predating issue #522's index
    /// can hold - is refused the same way rather than resolved by picking one.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_account_is_refused()
    {
        var world = new World
        {
            Memberships = new FakeBusinessMembershipStore(Membership(7), Membership(8)),
        };

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => world.Apply(Submission()));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);
        Assert.Empty(world.WriteStore.Operations);
    }

    /// <summary>
    /// The caller whose own business is closed to them gets their own sentence: only the platform
    /// administrator can release them, because such a business has no reachable Owner who could
    /// revoke their membership. <see cref="BusinessStatus.Rejected"/> is included because an active
    /// membership in a rejected business is the fail-closed case
    /// <see cref="AccountStatePolicy"/> maps to <see cref="AccountState.BusinessDeactivated"/>.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Deactivated)]
    [InlineData(BusinessStatus.Rejected)]
    public async Task A_caller_whose_business_is_closed_to_them_is_told_to_be_released_first(
        BusinessStatus businessStatus)
    {
        var world = new World
        {
            Memberships = new FakeBusinessMembershipStore(
                Membership(7, isActive: true, businessStatus: businessStatus)),
        };

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => world.Apply(Submission()));

        Assert.Equal(MemberInviteRedemptionPolicy.ReleaseRequiredMessage, conflict.Message);
        Assert.Empty(world.WriteStore.Operations);
    }

    /// <summary>
    /// The three eligible states, which are exactly the states with no active membership: never a
    /// member, revoked (including somebody released from a deactivated business), and a rejected
    /// applicant.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Active)]
    [InlineData(BusinessStatus.Deactivated)]
    [InlineData(BusinessStatus.Rejected)]
    public async Task A_caller_whose_every_membership_is_inactive_may_apply(BusinessStatus businessStatus)
    {
        var world = new World
        {
            Memberships = new FakeBusinessMembershipStore(
                Membership(7, isActive: false, businessStatus: businessStatus)),
        };

        var outcome = await world.Apply(Submission());

        Assert.Equal(BusinessStatus.Pending, outcome.Application?.Status);
        Assert.Contains(FakeBusinessApplicationStore.Create, world.Store.Operations);
    }

    #endregion

    #region Resubmission

    /// <summary>
    /// The identical application submitted again while it is pending answers with the application
    /// that is already waiting: no second business, no write, and no second audit event, because
    /// one application is one event.
    /// </summary>
    [Fact]
    public async Task An_identical_resubmission_returns_the_application_already_waiting()
    {
        var world = PendingApplicant(Validated());

        var outcome = await world.Apply(Submission());

        Assert.True(outcome.Enabled);
        Assert.Equal(BusinessId.From(7), outcome.Application?.BusinessId);
        Assert.Equal(Earlier, outcome.Application?.AppliedAtUtc);
        Assert.Equal([FakeBusinessApplicationStore.Find], world.Store.Operations);
        Assert.Empty(world.WriteStore.Operations);
        Assert.Empty(world.Audit.Entries);
    }

    /// <summary>
    /// Issue #681: an identical resubmission costs no allowance.
    ///
    /// <para>This branch exists so a lost response cannot create a second business, which makes it
    /// the branch a person on a bad connection reaches repeatedly - and it writes nothing and opens
    /// no transaction. Charging it an attempt locked such an applicant out of an endpoint that had
    /// been answering "here is the application you already have" correctly the whole time.</para>
    /// </summary>
    [Fact]
    public async Task An_identical_resubmission_costs_no_rate_limit_allowance()
    {
        var world = PendingApplicant(Validated());

        // More resubmissions than the window allows attempts: every one of them must be answered.
        for (var attempt = 0; attempt < BusinessApplicationRateLimiter.MaxApplicationsPerWindow + 3; attempt++)
        {
            var outcome = await world.Apply(Submission());

            Assert.Null(outcome.RetryAfter);
            Assert.Equal(BusinessId.From(7), outcome.Application?.BusinessId);
        }

        // Nothing is left tracked either: the only attempts recorded were given straight back.
        Assert.Equal(0, world.RateLimiter.TrackedIdentities);
        Assert.Empty(world.WriteStore.Operations);
        Assert.Empty(world.Audit.Entries);
    }

    /// <summary>
    /// The contrast that keeps the limit meaningful: a resubmission whose details <em>differ</em> is
    /// a refusal, not a replay, so it keeps its allowance - a person editing the form over and over
    /// is exactly the case the limit is for.
    /// </summary>
    [Fact]
    public async Task A_refused_resubmission_still_costs_its_allowance()
    {
        var world = PendingApplicant(Validated());

        await Assert.ThrowsAsync<DomainConflictException>(
            () => world.Apply(new BusinessApplicationSubmission(
                "Inland Vending",
                "owner@coastal.example.com",
                "51 824 753 556",
                true,
                "Australia/Brisbane",
                "QLD")));

        Assert.Equal(1, world.RateLimiter.TrackedIdentities);
    }

    /// <summary>
    /// The pending application is read by the id the caller's <em>own</em> membership row reported,
    /// never by anything a request supplied - there is nothing in the contract that could supply
    /// one.
    /// </summary>
    [Fact]
    public async Task The_waiting_application_is_read_by_the_id_the_callers_own_membership_reported()
    {
        var world = PendingApplicant(Validated());

        await world.Apply(Submission());

        Assert.Equal(BusinessId.From(7), world.Store.LastFoundBusinessId);
    }

    /// <summary>
    /// A submission that differs from the pending application is refused with its own sentence:
    /// changing details an operator is reviewing is a different request, and nothing may silently
    /// overwrite them or create a second application.
    /// </summary>
    [Fact]
    public async Task A_resubmission_with_different_details_is_refused()
    {
        var world = PendingApplicant(Validated());

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => world.Apply(
            Submission() with { Name = "Inland Vending" }));

        Assert.Equal(BusinessApplicationPolicy.PendingApplicationDiffersMessage, conflict.Message);
        Assert.Empty(world.WriteStore.Operations);
        Assert.Empty(world.Audit.Entries);
    }

    /// <summary>
    /// A pending membership whose business row cannot be read is refused rather than treated as an
    /// application that is waiting - the fail-closed direction between the two statements.
    /// </summary>
    [Fact]
    public async Task A_pending_membership_whose_business_is_gone_is_refused()
    {
        var world = new World
        {
            Memberships = new FakeBusinessMembershipStore(
                Membership(7, isActive: true, businessStatus: BusinessStatus.Pending)),
        };

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => world.Apply(Submission()));

        Assert.Equal(BusinessApplicationPolicy.PendingApplicationDiffersMessage, conflict.Message);
        Assert.Empty(world.WriteStore.Operations);
    }

    /// <summary>
    /// And so is one whose business stopped being Pending between the membership read and the
    /// business read: an approved business is not an application somebody may resubmit.
    /// </summary>
    [Fact]
    public async Task A_pending_membership_whose_business_is_no_longer_pending_is_refused()
    {
        var world = PendingApplicant(Validated(), storedStatus: BusinessStatus.Active);

        await Assert.ThrowsAsync<DomainConflictException>(() => world.Apply(Submission()));

        Assert.Empty(world.WriteStore.Operations);
    }

    #endregion

    #region Rate limit

    /// <summary>
    /// The allowance is claimed before the membership read and the write, so a form submitted in a
    /// loop stops costing anything. The refusal is an outcome rather than an exception, because the
    /// boundary turns it into a <c>429</c> with <c>Retry-After</c>.
    /// </summary>
    [Fact]
    public async Task Too_many_submissions_are_refused_without_reading_or_writing_anything()
    {
        var world = new World();

        for (var attempt = 0; attempt < BusinessApplicationRateLimiter.MaxApplicationsPerWindow; attempt++)
        {
            Assert.True(world.RateLimiter.TryClaim(world.Identity, out _));
        }

        var outcome = await world.Apply(Submission());

        Assert.True(outcome.Enabled);
        Assert.Null(outcome.Application);
        Assert.NotNull(outcome.RetryAfter);
        Assert.True(outcome.RetryAfter > TimeSpan.Zero);
        Assert.Equal(0, world.Memberships.QueryCount);
        Assert.Empty(world.Store.Operations);
        Assert.Empty(world.WriteStore.Operations);
        Assert.Empty(world.Audit.Entries);
    }

    /// <summary>
    /// The limit is the applicant's own: one person exhausting their allowance does not stop
    /// anybody else applying.
    /// </summary>
    [Fact]
    public async Task One_applicants_exhausted_allowance_does_not_refuse_another()
    {
        var world = new World();
        var somebodyElse = Actor("33333333-3333-3333-3333-333333333333");

        for (var attempt = 0; attempt < BusinessApplicationRateLimiter.MaxApplicationsPerWindow; attempt++)
        {
            Assert.True(world.RateLimiter.TryClaim(somebodyElse, out _));
        }

        var outcome = await world.Apply(Submission());

        Assert.Null(outcome.RetryAfter);
        Assert.Equal(BusinessStatus.Pending, outcome.Application?.Status);
    }

    #endregion

    private static ActorIdentity Actor(string objectId = Oid)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, objectId, out var actor));
        return actor!;
    }

    private static BusinessApplicationSubmission Submission() =>
        new("Coastal Vending", "owner@coastal.example.com", "51 824 753 556", true, "Australia/Brisbane", "QLD");

    private static BusinessSettings Validated() =>
        new("Coastal Vending", "owner@coastal.example.com", ValidAbn, true, "Australia/Brisbane", "QLD");

    private static ActorBusinessMembership Membership(
        int businessId,
        bool isActive = true,
        BusinessStatus businessStatus = BusinessStatus.Active) =>
        new(BusinessId.From(businessId), BusinessRole.Owner, isActive, businessStatus, Earlier, null);

    /// <summary>
    /// A caller who already has an application waiting: one active membership in a Pending business,
    /// and that business's stored application.
    /// </summary>
    private static World PendingApplicant(
        BusinessSettings stored,
        BusinessStatus storedStatus = BusinessStatus.Pending)
    {
        var world = new World
        {
            Memberships = new FakeBusinessMembershipStore(
                Membership(7, isActive: true, businessStatus: BusinessStatus.Pending)),
        };

        world.Store.Applications[BusinessId.From(7)] =
            new BusinessApplication(BusinessId.From(7), stored, storedStatus, Earlier);

        return world;
    }

    /// <summary>
    /// The fakes one application runs against, assembled so each test states only what it changes.
    /// </summary>
    private sealed class World
    {
        public World()
        {
            RateLimiter = new BusinessApplicationRateLimiter(Clock);
        }

        public ActorIdentity Identity { get; } = Actor();

        public FakeClock Clock { get; } = new(Now);

        public BusinessApplicationRateLimiter RateLimiter { get; }

        public ISelfServiceOnboardingFlag Onboarding { get; init; } = FakeSelfServiceOnboardingFlag.Enabled;

        public FakeActorProfileAccessor Actors { get; init; } =
            FakeActorProfileAccessor.For(Actor(), MemberProfile.Create("Dana Doe", "dana@example.test"));

        public FakeBusinessMembershipStore Memberships { get; init; } = new();

        public FakeBusinessApplicationStore Store { get; } = new();

        public FakeBusinessMembershipWriteStore WriteStore { get; } = new();

        public FakeBusinessApplicationAudit Audit { get; } = new();

        public Task<BusinessApplicationOutcome> Apply(BusinessApplicationSubmission submission)
        {
            // The creation is recorded on the write store's own ordered log, so one list shows
            // whether the write happened inside the transaction the guard opened.
            Store.WhileCreating = () => WriteStore.Record(FakeBusinessApplicationStore.Create);

            return new ApplyForBusiness(
                    Onboarding,
                    Actors,
                    Memberships,
                    Store,
                    RateLimiter,
                    new MembershipWriteGuard(WriteStore),
                    Audit,
                    Clock)
                .Handle(submission, CancellationToken.None);
        }
    }
}
