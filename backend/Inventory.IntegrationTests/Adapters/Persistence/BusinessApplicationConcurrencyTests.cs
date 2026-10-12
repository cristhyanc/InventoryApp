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
/// are the things a test has to decide rather than observe.</para>
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
    /// created: the loser is refused as a conflict and its own business row goes back with the
    /// transaction, so a double submission can never leave two Pending tenants for one person.
    ///
    /// <para>The refusal rather than a second <c>200</c> is the deliberate trade. The resubmission
    /// shortcut is answered from a read that writes nothing, so it cannot be serialised against a
    /// creation that has not happened yet; what has to be one operation is the creation, and the
    /// rule that makes it so is the eligibility re-check inside the write transaction. A simultaneous
    /// double submission therefore fails closed - one application, one business - and the person's
    /// next attempt is the ordinary idempotent resubmission.</para>
    /// </summary>
    [Fact]
    public async Task Two_applications_by_one_identity_create_exactly_one_business()
    {
        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => ApplyAsync(ApplicantOid, Submission(), token),
            token => ApplyAsync(ApplicantOid, Submission(), token));

        var conflict = Assert.IsType<DomainConflictException>(
            MembershipWriteRaceHarness.SingleFailure(failures));
        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);

        var created = Assert.Single(_harness.Businesses().Where(business => business.Status == BusinessStatus.Pending));
        var membership = Assert.Single(_harness.Memberships());
        Assert.Equal(created.Id, membership.BusinessId);
        Assert.Equal(BusinessRole.Owner, membership.Role);
        Assert.True(membership.IsActive);
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
        new("Coastal Vending", "owner@coastal.example.com", ValidAbn, true, "Australia/Brisbane");

    /// <summary>
    /// Runs one real application on its own connection, as a request would: a denied business scope,
    /// the real guard and the real stores, and the caller's identity supplied the way the API
    /// boundary supplies it.
    /// </summary>
    private async Task ApplyAsync(
        string objectId,
        BusinessApplicationSubmission submission,
        CancellationToken cancellationToken)
    {
        var identity = MembershipWriteRaceHarness.Actor(objectId);

        // Denied, because somebody asking to start a business is not a member of one - which is
        // exactly the scope the membership-not-required endpoint gives this request.
        await using var db = TestAppDbContext.Denied(_harness.Options);

        var apply = new ApplyForBusiness(
            FakeSelfServiceOnboardingFlag.Enabled,
            FakeActorProfileAccessor.For(identity, MemberProfile.None),
            new EfBusinessMembershipStore(db),
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
}
