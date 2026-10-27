using Inventory.Application.Members;
using Inventory.Application.Onboarding;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Members;
using InventoryApi.Tests.Application.Onboarding;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The self-service business application of issue #541 under real concurrency, on two independent
/// connections to one real SQLite file through <see cref="MembershipWriteRaceHarness"/>.
///
/// <para>These are the races the issue names, and the first of them is the one it names explicitly:
/// one identity redeeming an invite and applying for a business at the very same instant. Both are
/// check-then-write, both end with that identity holding an active membership, and a
/// check-then-write rule that is not serialised is not a rule - without issue #522's
/// <c>BEGIN IMMEDIATE</c> transaction and the filtered unique index behind it, the person would end
/// up a member of somebody else's business <em>and</em> the Owner of a new one, which denies them
/// both.</para>
///
/// <para>The whole use cases are exercised, not just the adapters: the real
/// <see cref="MembershipWriteGuard"/>, the real <see cref="EfBusinessApplicationStore"/>, the real
/// <see cref="EfMemberInviteRedemptionStore"/> and the real
/// <see cref="EfBusinessMembershipWriteStore"/> over the migrated schema, with only the
/// authenticated actor, the clock, the onboarding flag and the audit sinks faked - because those
/// are the things a test has to decide rather than observe. One test adds the use case's own
/// eligibility <em>read</em> to that list, for the same reason: which instant that read saw is the
/// thing a barrier cannot decide, and the rule it is the test of lives after it.</para>
///
/// <para>What a test here may assert is what the rules guarantee, and not which thread won. Where
/// an operation has two correct answers depending on the interleaving - as a double application
/// does, one being the issue's own idempotent resubmission - the assertion is the state the
/// database is left in and the set of answers allowed, and the interleaving that is only reachable
/// by timing is pinned down by a separate deterministic test instead. An assertion that names the
/// winner passes on a quiet machine and fails under load without anything being wrong.</para>
/// </summary>
public class BusinessApplicationConcurrencyTests : IAsyncDisposable
{
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private const string ApplicantOid = "22222222-2222-2222-2222-222222222222";
    private const string SecondApplicantOid = "33333333-3333-3333-3333-333333333333";

    private const string ValidAbn = "51824753556";

    private static readonly DateTime Now = MembershipWriteRaceHarness.SeededAt.AddHours(1);

    private readonly MembershipWriteRaceHarness _harness =
        MembershipWriteRaceHarness.Create(FirstBusinessId, SecondBusinessId);

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    /// <summary>
    /// The race the issue asks for: one identity presenting an invite and applying for a business at
    /// the same instant. Exactly one succeeds, the identity ends with exactly one active membership,
    /// and the loser leaves nothing behind - either the invite is still unused, or no new business
    /// row exists.
    /// </summary>
    [Fact]
    public async Task An_invite_and_an_application_by_one_identity_leave_exactly_one_membership()
    {
        _harness.SeedInvite(FirstBusinessId, "the-link", BusinessRole.Manager);

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => RedeemAsync("the-link", ApplicantOid, token),
            token => ApplyAsync(ApplicantOid, Submission(), token));

        var conflict = Assert.IsType<DomainConflictException>(
            MembershipWriteRaceHarness.SingleFailure(failures));
        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);

        var membership = Assert.Single(_harness.Memberships());
        Assert.True(membership.IsActive);

        var invite = Assert.Single(_harness.Invites());
        var businesses = _harness.Businesses();

        if (invite.Status == MemberInviteStatus.Used)
        {
            // The redemption won: the applicant joined the inviting business, and the application
            // rolled back with its transaction rather than leaving a Pending tenant behind.
            Assert.Equal(FirstBusinessId, membership.BusinessId);
            Assert.Equal(BusinessRole.Manager, membership.Role);
            Assert.Equal(membership.Id, invite.RedeemedByMembershipId);
            Assert.Equal(2, businesses.Count);
            Assert.DoesNotContain(businesses, business => business.Status == BusinessStatus.Pending);
        }
        else
        {
            // The application won: the new Pending business owns the only active membership, and
            // the business that issued the link has not silently spent it.
            Assert.Equal(MemberInviteStatus.Pending, invite.Status);
            Assert.Null(invite.UsedAtUtc);
            Assert.Null(invite.RedeemedByMembershipId);
            Assert.Equal(3, businesses.Count);

            var created = Assert.Single(businesses.Where(business => business.Status == BusinessStatus.Pending));
            Assert.Equal(created.Id, membership.BusinessId);
            Assert.Equal(BusinessRole.Owner, membership.Role);
        }
    }

    /// <summary>
    /// The same race for somebody the inviting business had revoked: the reactivation path and the
    /// creation path are serialised against each other exactly as two reactivations are, so the
    /// identity still ends with one active membership rather than a reactivated row <em>and</em> a
    /// new tenant.
    /// </summary>
    [Fact]
    public async Task An_invite_and_an_application_by_one_revoked_identity_leave_exactly_one_active_membership()
    {
        _harness.SeedInvite(FirstBusinessId, "the-link", BusinessRole.Manager);
        _harness.SeedMembership(FirstBusinessId, ApplicantOid, BusinessRole.Operator, isActive: false);

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => RedeemAsync("the-link", ApplicantOid, token),
            token => ApplyAsync(ApplicantOid, Submission(), token));

        Assert.IsType<DomainConflictException>(MembershipWriteRaceHarness.SingleFailure(failures));

        var memberships = _harness.Memberships();
        Assert.Single(memberships.Where(membership => membership.IsActive));
    }

    /// <summary>
    /// Two applications submitted by one identity at the same instant. Exactly one business is
    /// created, it has exactly one active Owner membership, and every attempt is answered either
    /// with that one application or with the membership conflict - so a double submission can never
    /// leave two Pending tenants for one person.
    ///
    /// <para><strong>Which of the two answers the second attempt gets is the interleaving's to
    /// decide, and asserting one of them would be asserting the scheduler.</strong> The resubmission
    /// shortcut is answered from a read that writes nothing and therefore takes no write lock: when
    /// that read runs <em>after</em> the other attempt committed it sees the application already
    /// waiting and reports it back, which is the ordinary idempotent retry of the issue's
    /// acceptance criteria and a second <c>200</c> for the same single application; when it runs
    /// <em>before</em>, the creation reaches <see cref="MembershipWriteGuard"/> and the eligibility
    /// re-check inside the write transaction refuses it as a conflict. Both are legitimate answers
    /// to "I submitted this form twice", and the guarantee underneath them is the same and is what
    /// this test asserts: one application, one business, one active membership. A run that demanded
    /// exactly one failure asserted the slower of two threads instead, which is why it failed
    /// intermittently in CI while the invariant held.</para>
    ///
    /// <para>The guard refusal that the second ordering produces is not left to the scheduler
    /// either; it is proved deterministically by
    /// <see cref="An_application_whose_eligibility_read_missed_the_committed_one_creates_nothing"/>.</para>
    /// </summary>
    [Fact]
    public async Task Two_applications_by_one_identity_create_exactly_one_business()
    {
        var outcomes = new BusinessApplicationOutcome?[2];

        var failures = await MembershipWriteRaceHarness.RunAsync(
            async token => outcomes[0] = await ApplyAsync(ApplicantOid, Submission(), token),
            async token => outcomes[1] = await ApplyAsync(ApplicantOid, Submission(), token));

        // The invariant, whichever way the two interleaved: one Pending business for this identity,
        // one membership, and it is that business's active Owner.
        var created = Assert.Single(_harness.Businesses().Where(business => business.Status == BusinessStatus.Pending));
        var membership = Assert.Single(_harness.Memberships());
        Assert.Equal(created.Id, membership.BusinessId);
        Assert.Equal(BusinessRole.Owner, membership.Role);
        Assert.True(membership.IsActive);

        // At least one attempt recorded it - two conflicts would mean the application was lost.
        Assert.NotEmpty(failures.Where(failure => failure is null));

        for (var attempt = 0; attempt < failures.Length; attempt++)
        {
            if (failures[attempt] is { } failure)
            {
                // Refused by the re-check inside the write transaction, with the one fixed sentence
                // and nothing written: the single business above is the other attempt's.
                var conflict = Assert.IsType<DomainConflictException>(failure);
                Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);
                Assert.Null(outcomes[attempt]);
                continue;
            }

            // Answered with an application - and it has to be the one that exists, not a second
            // one: same row, same submission instant, still Pending.
            var application = outcomes[attempt]?.Application;
            Assert.NotNull(application);
            Assert.Equal(created.Id, application.BusinessId.Value);
            Assert.Equal(created.AppliedAtUtc, application.AppliedAtUtc);
            Assert.Equal(BusinessStatus.Pending, application.Status);
        }
    }

    /// <summary>
    /// The interleaving above that a barrier cannot pin down, made deterministic: an application
    /// whose eligibility read answered "no membership" because it ran a moment before the other
    /// applicant's transaction committed. The resubmission shortcut and the early refusal both pass
    /// it through, so the creation reaches the guard - and the re-check inside the write transaction
    /// is what refuses it, with the business row it had already added going back with the
    /// transaction rather than surviving as a second Pending tenant.
    ///
    /// <para>This is the assertion the race test above cannot make reliably, and the one that
    /// matters: the stale read is the <em>only</em> way two applications by one identity can both
    /// reach a write, and this is what happens when one does - every time, rather than one run in
    /// twenty. It does not distinguish which of the two rules refused, because nothing a caller can
    /// observe does: the re-check inside the transaction and the filtered unique index behind it
    /// refuse with the same sentence by design, and the order the guard applies its rules in is
    /// <c>MembershipWriteGuardTests</c>' subject.</para>
    /// </summary>
    [Fact]
    public async Task An_application_whose_eligibility_read_missed_the_committed_one_creates_nothing()
    {
        await ApplyAsync(ApplicantOid, Submission(), CancellationToken.None);

        var created = Assert.Single(_harness.Businesses().Where(business => business.Status == BusinessStatus.Pending));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => ApplyAsync(
                ApplicantOid,
                Submission(),
                CancellationToken.None,
                StaleMembershipStore.SeesNothing));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);

        // Nothing of the refused attempt is left: the two seeded businesses and the one that was
        // created, the one membership, and the application instant unchanged.
        var stillOne = Assert.Single(_harness.Businesses().Where(business => business.Status == BusinessStatus.Pending));
        Assert.Equal(created.Id, stillOne.Id);
        Assert.Equal(created.AppliedAtUtc, stillOne.AppliedAtUtc);
        Assert.Equal(3, _harness.Businesses().Count);
        Assert.Single(_harness.Memberships());
    }

    /// <summary>
    /// Two different people applying at the same instant is not a race at all, and must not be
    /// turned into one: the limit is per identity and the rule is per identity, so both succeed and
    /// two separate Pending businesses exist.
    /// </summary>
    [Fact]
    public async Task Two_applications_by_two_identities_both_succeed()
    {
        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => ApplyAsync(ApplicantOid, Submission(), token),
            token => ApplyAsync(SecondApplicantOid, Submission() with { Name = "Inland Vending" }, token));

        Assert.All(failures, failure => Assert.Null(failure));

        var pending = _harness.Businesses().Where(business => business.Status == BusinessStatus.Pending).ToList();
        Assert.Equal(2, pending.Count);

        var memberships = _harness.Memberships();
        Assert.Equal(2, memberships.Count);
        Assert.All(memberships, membership => Assert.True(membership.IsActive));
        Assert.All(memberships, membership => Assert.Equal(BusinessRole.Owner, membership.Role));
        Assert.Equal(
            pending.Select(business => business.Id).Order(),
            memberships.Select(membership => membership.BusinessId).Order());
    }

    /// <summary>
    /// The sequential half, and the one a person meets in practice: submitting the identical
    /// application again is answered with the one that is already waiting, and changes nothing - no
    /// second business, no second membership, and the same application instant.
    /// </summary>
    [Fact]
    public async Task Resubmitting_the_identical_application_changes_nothing()
    {
        await ApplyAsync(ApplicantOid, Submission(), CancellationToken.None);

        var created = Assert.Single(_harness.Businesses().Where(business => business.Status == BusinessStatus.Pending));

        await ApplyAsync(ApplicantOid, Submission(), CancellationToken.None);

        var stillOne = Assert.Single(_harness.Businesses().Where(business => business.Status == BusinessStatus.Pending));
        Assert.Equal(created.Id, stillOne.Id);
        Assert.Equal(created.AppliedAtUtc, stillOne.AppliedAtUtc);
        Assert.Single(_harness.Memberships());
    }

    private static BusinessApplicationSubmission Submission() =>
        new("Coastal Vending", "owner@coastal.example.com", ValidAbn, true, "Australia/Brisbane", "QLD");

    /// <summary>
    /// Runs one real application on its own connection, as a request would: a denied business scope,
    /// the real guard and the real stores, and the caller's identity supplied the way the API
    /// boundary supplies it.
    ///
    /// <para><c>membershipStore</c> is what the use case's <em>own</em> eligibility read uses, for
    /// the one test that has to decide what that read saw rather than observe it: the real
    /// relational store by default, and <see cref="StaleMembershipStore"/> to stand for the instant
    /// before another applicant's transaction committed. The guard's re-check is always the real one
    /// over the real database, because that is the rule under test.</para>
    /// </summary>
    private async Task<BusinessApplicationOutcome> ApplyAsync(
        string objectId,
        BusinessApplicationSubmission submission,
        CancellationToken cancellationToken,
        IBusinessMembershipStore? membershipStore = null)
    {
        var identity = MembershipWriteRaceHarness.Actor(objectId);

        // Denied, because somebody asking to start a business is not a member of one - which is
        // exactly the scope the membership-not-required endpoint gives this request.
        await using var db = TestAppDbContext.Denied(_harness.Options);

        var apply = new ApplyForBusiness(
            FakeSelfServiceOnboardingFlag.Enabled,
            FakeActorProfileAccessor.For(identity, MemberProfile.None),
            membershipStore ?? new EfBusinessMembershipStore(db),
            new EfBusinessApplicationStore(db),
            new BusinessApplicationRateLimiter(new FakeClock(Now)),
            new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
            new FakeBusinessApplicationAudit(),
            new FakeClock(Now));

        var outcome = await apply.Handle(submission, cancellationToken);

        // A rate-limit refusal would make a race test pass for the wrong reason, so it is asserted
        // away rather than ignored: every operation here gets its own limiter and claims once.
        Assert.Null(outcome.RetryAfter);
        Assert.True(outcome.Enabled);

        return outcome;
    }

    /// <summary>The same for one real invite redemption, exactly as issue #532's own races run it.</summary>
    private async Task RedeemAsync(string token, string objectId, CancellationToken cancellationToken)
    {
        var identity = MembershipWriteRaceHarness.Actor(objectId);

        await using var db = TestAppDbContext.Denied(_harness.Options);

        var redeem = new RedeemMemberInvite(
            FakeActorProfileAccessor.For(identity, MemberProfile.None),
            new EfBusinessMembershipStore(db),
            new EfMemberInviteRedemptionStore(db),
            new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
            new FakeMemberInviteAudit(),
            new FakeClock(Now));

        await redeem.Handle(token, cancellationToken);
    }

    /// <summary>
    /// One membership read that answers "this identity has no membership", which is what the real
    /// read answers for the instant before another applicant's transaction commits. It stands in for
    /// that instant and for nothing else: everything the use case then does - the write, the
    /// re-check and the transaction - is the real thing over the real database.
    /// </summary>
    private sealed class StaleMembershipStore : IBusinessMembershipStore
    {
        public static StaleMembershipStore SeesNothing { get; } = new();

        public Task<IReadOnlyList<ActorBusinessMembership>> FindMembershipsAsync(
            ActorIdentity actor,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ActorBusinessMembership>>([]);
    }
}
