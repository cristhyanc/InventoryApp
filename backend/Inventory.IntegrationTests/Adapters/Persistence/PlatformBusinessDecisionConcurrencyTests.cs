using Inventory.Application.PlatformAdmin;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.PlatformAdmin;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The Super Admin's two decisions of issue #544 under real concurrency, on two independent
/// connections to one real SQLite file through <see cref="MembershipWriteRaceHarness"/>.
///
/// <para>The race the issue names is an approve and a reject arriving at the same instant. Both are
/// check-then-write over the same business row - "is it still Pending?" then "set it to Active" or
/// "set it to Rejected and release the applicant" - and a check-then-write that is not serialised is
/// not a check: without issue #522's <c>BEGIN IMMEDIATE</c> transaction and the re-read inside it,
/// both would pass and the second write would overwrite the first, leaving a trading tenant whose
/// applicant has been released, or a rejected application whose applicant is still a member of
/// it.</para>
///
/// <para>The whole use cases are exercised, not just the adapters: the real
/// <see cref="MembershipWriteGuard"/>, the real <see cref="EfPlatformBusinessDirectoryStore"/> and
/// the real <see cref="EfBusinessMembershipWriteStore"/> over the migrated schema, with only the
/// authenticated actor, the clock and the audit sink faked - because those are the things a test has
/// to decide rather than observe.</para>
///
/// <para>What a test here may assert is what the rules guarantee, and not which decision won. Both
/// outcomes are correct answers to two operators pressing a button at once, so the assertion is the
/// state the database is left in and the one refusal that is allowed; an assertion that named the
/// winner would pass on a quiet machine and fail under load without anything being wrong.</para>
/// </summary>
public class PlatformBusinessDecisionConcurrencyTests : IAsyncDisposable
{
    private const int PendingBusinessId = 1;
    private const int TradingBusinessId = 2;

    private const string ApplicantOid = "22222222-2222-2222-2222-222222222222";
    private const string AdministratorOid = "99999999-9999-9999-9999-999999999999";

    private static readonly DateTime Now = MembershipWriteRaceHarness.SeededAt.AddHours(1);

    private readonly MembershipWriteRaceHarness _harness =
        MembershipWriteRaceHarness.Create(PendingBusinessId, TradingBusinessId);

    private readonly int _applicantMembershipId;

    public PlatformBusinessDecisionConcurrencyTests()
    {
        _harness.SetBusinessStatus(PendingBusinessId, BusinessStatus.Pending);
        _applicantMembershipId = _harness.SeedMembership(PendingBusinessId, ApplicantOid);
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    /// <summary>
    /// The race the issue asks for: exactly one decision is recorded, the other is refused with the
    /// one "no longer waiting for a decision" sentence, and the business and its applicant are left
    /// consistent with whichever won.
    /// </summary>
    [Fact]
    public async Task A_simultaneous_approve_and_reject_leave_exactly_one_decision()
    {
        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => ApproveAsync(PendingBusinessId, token),
            token => RejectAsync(PendingBusinessId, "Not enough detail.", token));

        var conflict = Assert.IsType<DomainConflictException>(
            MembershipWriteRaceHarness.SingleFailure(failures));
        Assert.Equal(BusinessDecisionPolicy.NotPendingMessage, conflict.Message);

        var business = _harness.Businesses().Single(candidate => candidate.Id == PendingBusinessId);
        var applicant = _harness.Memberships().Single(membership => membership.Id == _applicantMembershipId);

        Assert.Equal(Now, business.DecidedAtUtc);

        if (business.Status == BusinessStatus.Active)
        {
            // The approval won: the applicant is still the business's active Owner, and the
            // rejection rolled back rather than releasing them from a business that now trades.
            Assert.True(applicant.IsActive);
            Assert.Null(business.RejectionReason);
            Assert.Equal(MembershipWriteRaceHarness.SeededAt, applicant.StatusChangedAtUtc);
        }
        else
        {
            // The rejection won: the applicant was released, so they are free to apply again, and
            // the approval rolled back rather than making a refused application trade.
            Assert.Equal(BusinessStatus.Rejected, business.Status);
            Assert.Equal("Not enough detail.", business.RejectionReason);
            Assert.False(applicant.IsActive);
            Assert.Equal(Now, applicant.StatusChangedAtUtc);
        }
    }

    /// <summary>
    /// Two simultaneous approvals: one records the decision and the other is refused, so an
    /// application cannot be approved twice and its decision instant cannot be rewritten by a second
    /// press of the same button.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_approvals_record_one_decision()
    {
        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => ApproveAsync(PendingBusinessId, token),
            token => ApproveAsync(PendingBusinessId, token));

        var conflict = Assert.IsType<DomainConflictException>(
            MembershipWriteRaceHarness.SingleFailure(failures));
        Assert.Equal(BusinessDecisionPolicy.NotPendingMessage, conflict.Message);

        var business = _harness.Businesses().Single(candidate => candidate.Id == PendingBusinessId);
        Assert.Equal(BusinessStatus.Active, business.Status);
        Assert.True(_harness.Memberships().Single(membership => membership.Id == _applicantMembershipId).IsActive);
    }

    /// <summary>
    /// The sequential half, and the one an operator meets in practice: deciding an application that
    /// has already been decided is refused and changes nothing - not the status, not the decision
    /// instant, and not the applicant's membership.
    /// </summary>
    [Fact]
    public async Task Deciding_an_already_decided_application_changes_nothing()
    {
        await ApproveAsync(PendingBusinessId, CancellationToken.None);

        var afterApproval = _harness.Businesses().Single(candidate => candidate.Id == PendingBusinessId);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => RejectAsync(PendingBusinessId, "Changed my mind.", CancellationToken.None));
        Assert.Equal(BusinessDecisionPolicy.NotPendingMessage, conflict.Message);

        var afterRejection = _harness.Businesses().Single(candidate => candidate.Id == PendingBusinessId);
        Assert.Equal(BusinessStatus.Active, afterRejection.Status);
        Assert.Equal(afterApproval.DecidedAtUtc, afterRejection.DecidedAtUtc);
        Assert.Null(afterRejection.RejectionReason);
        Assert.True(_harness.Memberships().Single(membership => membership.Id == _applicantMembershipId).IsActive);
    }

    /// <summary>
    /// An approval and an invite redemption by the applicant cannot both land. Approving confirms
    /// that this person's one active membership is the one in the business being approved, so if they
    /// join somewhere else at the same instant exactly one of the two succeeds - which is the
    /// one-active-membership rule holding across two different operations, not a rule local to
    /// either.
    /// </summary>
    [Fact]
    public async Task An_approval_and_the_applicant_joining_another_business_cannot_both_land()
    {
        // The applicant was released from their pending application - the state somebody who has
        // been let go of a stalled application is in - and the business is still waiting.
        _harness.SeedInvite(TradingBusinessId, "the-link", BusinessRole.Manager);
        await ReleaseApplicantAsync();

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => ApproveAsync(PendingBusinessId, token),
            token => RedeemAsync("the-link", token));

        // The approval is the one that must fail: its applicant's membership is no longer active, so
        // there is nothing to approve whichever order the two ran in.
        var conflict = Assert.IsType<DomainConflictException>(
            MembershipWriteRaceHarness.SingleFailure(failures));
        Assert.Equal(BusinessDecisionPolicy.ApplicantUnavailableMessage, conflict.Message);

        Assert.Equal(
            BusinessStatus.Pending,
            _harness.Businesses().Single(candidate => candidate.Id == PendingBusinessId).Status);

        var active = _harness.Memberships().Where(membership => membership.IsActive).ToList();
        Assert.Equal(TradingBusinessId, Assert.Single(active).BusinessId);
    }

    /// <summary>
    /// Releases the applicant outside any boundary, which is the state #545's "release a member"
    /// will leave and the state a test of "the applicant joined elsewhere" needs.
    /// </summary>
    private async Task ReleaseApplicantAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_harness.Options);
        var membership = db.BusinessMemberships.Single(candidate => candidate.Id == _applicantMembershipId);
        membership.IsActive = false;
        membership.StatusChangedAtUtc = MembershipWriteRaceHarness.SeededAt;
        await db.SaveChangesAsync();
    }

    private Task<PlatformBusinessDecision?> ApproveAsync(int businessId, CancellationToken cancellationToken) =>
        WithWriter(writer => new ApproveBusinessApplication(writer).Handle(businessId, cancellationToken));

    private Task<PlatformBusinessDecision?> RejectAsync(
        int businessId,
        string? reason,
        CancellationToken cancellationToken) =>
        WithWriter(writer => new RejectBusinessApplication(writer).Handle(businessId, reason, cancellationToken));

    /// <summary>
    /// Runs one real decision on its own connection, as a request would: a denied business scope -
    /// the platform administrator has no membership anywhere, so nothing resolves a business for
    /// them - the real guard and the real stores, and the actor supplied the way the API boundary
    /// supplies it.
    /// </summary>
    private async Task<PlatformBusinessDecision?> WithWriter(
        Func<BusinessApplicationDecisionWriter, Task<PlatformBusinessDecision?>> decide)
    {
        await using var db = TestAppDbContext.Denied(_harness.Options);

        return await decide(new BusinessApplicationDecisionWriter(
            FakeAuthenticatedActorAccessor.Identified(MembershipWriteRaceHarness.Actor(AdministratorOid)),
            new EfPlatformBusinessDirectoryStore(db),
            new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
            new FakePlatformBusinessDecisionAudit(),
            new FakeClock(Now)));
    }

    /// <summary>The same for one real invite redemption, exactly as issue #532's own races run it.</summary>
    private async Task RedeemAsync(string token, CancellationToken cancellationToken)
    {
        await using var db = TestAppDbContext.Denied(_harness.Options);

        var redeem = new Inventory.Application.Members.RedeemMemberInvite(
            FakeActorProfileAccessor.For(MembershipWriteRaceHarness.Actor(ApplicantOid), MemberProfile.None),
            new EfBusinessMembershipStore(db),
            new EfMemberInviteRedemptionStore(db),
            new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
            new InventoryApi.Tests.Application.Members.FakeMemberInviteAudit(),
            new FakeClock(Now));

        await redeem.Handle(token, cancellationToken);
    }
}
