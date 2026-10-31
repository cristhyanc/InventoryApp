using Inventory.Application.PlatformAdmin;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformAdmin;

/// <summary>
/// Approving and refusing a business application (issue #544): the rules, the order they are
/// applied in, what each one writes, and what each refusal leaves behind.
///
/// <para>These tests are over the real <see cref="MembershipWriteGuard"/> with the membership write
/// port faked, because the guard's transaction is where the rules that matter are re-checked. The
/// eligibility exclusion is the central one: the applicant already holds the active membership the
/// approval confirms, so without excluding that row every approval would be refused by the
/// applicant's own membership. The relational and concurrent halves of the same rules are in
/// <c>PlatformBusinessDecisionConcurrencyTests</c> and
/// <c>EfPlatformBusinessDirectoryStoreTests</c>.</para>
/// </summary>
public class BusinessApplicationDecisionTests
{
    private const int PendingBusinessId = 7;
    private const int ApplicantMembershipId = 42;
    private const string DirectoryTenantId = "11111111-1111-1111-1111-111111111111";
    private const string AdministratorObjectId = "99999999-9999-9999-9999-999999999999";
    private const string ApplicantObjectId = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTime DecidedAt = new(2026, 10, 12, 3, 30, 0, DateTimeKind.Utc);

    private readonly FakePlatformBusinessDirectoryStore _store = new();
    private readonly FakeBusinessMembershipWriteStore _writeStore = new();
    private readonly FakePlatformBusinessDecisionAudit _audit = new();
    private readonly FakeBusinessSupplierProvisioningStore _provisioning = new();

    public BusinessApplicationDecisionTests()
    {
        // So the copy of issue #849 appears in the write store's order of operations, which is where
        // "inside the transaction, before the commit" is asserted.
        _provisioning.Recorder = _writeStore.Record;
    }

    #region Approving

    /// <summary>
    /// The criterion the exclusion exists for: the applicant's only active membership is the very
    /// one being approved, so the approval succeeds. Without
    /// <c>MembershipWriteIntent.ExcludedMembershipId</c> this is refused with "already a member of a
    /// business" - the applicant's own row counted against them - which is exactly the failure this
    /// test was written to produce first.
    /// </summary>
    [Fact]
    public async Task Approving_succeeds_when_the_applicants_only_active_membership_is_the_one_being_approved()
    {
        GivenPendingApplication();
        GivenApplicantMemberships(new IdentityMembership(ApplicantMembershipId, IsActive: true));

        var decision = await Approve().Handle(PendingBusinessId, CancellationToken.None);

        Assert.NotNull(decision);
        Assert.Equal(PendingBusinessId, decision!.BusinessId);
        Assert.Equal(BusinessStatus.Active, decision.Status);
        Assert.Equal(DecidedAt, decision.DecidedAtUtc);
        Assert.Null(decision.RejectionReason);
        Assert.Equal(DecidedAt, _store.ApprovedAtUtc);

        // The applicant's own row is what the eligibility check was asked about, and it is the
        // membership that was excluded from the count.
        Assert.Equal(ApplicantObjectId, _writeStore.LastQueriedIdentity?.ObjectId);
    }

    /// <summary>
    /// The decision is committed before it is audited, and the event carries the transition and the
    /// absence of a reason - never a name, an address or the applicant's identity.
    /// </summary>
    [Fact]
    public async Task An_approval_is_audited_once_with_the_actor_and_the_transition()
    {
        GivenPendingApplication();
        GivenApplicantMemberships(new IdentityMembership(ApplicantMembershipId, IsActive: true));

        await Approve().Handle(PendingBusinessId, CancellationToken.None);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(AdministratorObjectId, entry.ActorObjectId);
        Assert.Equal(PendingBusinessId, entry.BusinessId);
        Assert.Equal(BusinessStatus.Pending, entry.FromStatus);
        Assert.Equal(BusinessStatus.Active, entry.ToStatus);
        Assert.False(entry.ReasonRecorded);
        Assert.Equal(DecidedAt, entry.OccurredAtUtc);
    }

    /// <summary>
    /// The criterion "approve after the applicant joined elsewhere": their membership in the pending
    /// business is no longer active, so there is nothing to approve and no trading tenant is created
    /// with nobody who can reach it.
    /// </summary>
    [Fact]
    public async Task Approving_is_refused_when_the_applicants_membership_is_no_longer_active()
    {
        GivenPendingApplication(applicantActive: false);
        GivenApplicantMemberships(new IdentityMembership(ApplicantMembershipId, IsActive: false));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Approve().Handle(PendingBusinessId, CancellationToken.None));

        Assert.Equal(BusinessDecisionPolicy.ApplicantUnavailableMessage, conflict.Message);
        AssertNothingWasDecided();
    }

    /// <summary>
    /// The same criterion from the other direction: the applicant now holds an active membership
    /// that is <em>not</em> the one being approved, so <see cref="MembershipEligibility"/> refuses -
    /// the exclusion leaves out one row, never every row.
    /// </summary>
    [Fact]
    public async Task Approving_is_refused_when_the_applicant_holds_an_active_membership_elsewhere()
    {
        GivenPendingApplication();
        GivenApplicantMemberships(
            new IdentityMembership(ApplicantMembershipId, IsActive: true),
            new IdentityMembership(ApplicantMembershipId + 1, IsActive: true));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Approve().Handle(PendingBusinessId, CancellationToken.None));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);
        AssertNothingWasDecided();
    }

    /// <summary>
    /// A pending business with no single active membership has no identifiable applicant, so it is
    /// refused rather than approved by picking a row. Only a hand-written world reaches this: an
    /// application creates exactly one membership, and no invite can be redeemed into a business
    /// that does not resolve.
    /// </summary>
    [Fact]
    public async Task Approving_is_refused_when_the_application_has_no_identifiable_applicant()
    {
        _store.Targets[PendingBusinessId] = new PlatformBusinessDecisionTarget(
            PendingBusinessId,
            "Coastal Vending",
            BusinessStatus.Pending,
            Applicant: null);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Approve().Handle(PendingBusinessId, CancellationToken.None));

        Assert.Equal(BusinessDecisionPolicy.ApplicantUnavailableMessage, conflict.Message);
        AssertNothingWasDecided();
    }

    /// <summary>
    /// A membership whose stored Entra pair is not an identity cannot be the subject of the
    /// one-active-membership check, so approving it would mean skipping that check entirely. It is
    /// refused instead - the same fail-closed answer a reinstatement gives.
    /// </summary>
    [Fact]
    public async Task Approving_is_refused_when_the_applicants_stored_identity_is_not_usable()
    {
        _store.Targets[PendingBusinessId] = new PlatformBusinessDecisionTarget(
            PendingBusinessId,
            "Coastal Vending",
            BusinessStatus.Pending,
            new PlatformBusinessApplicant(ApplicantMembershipId, Identity: null, IsActive: true));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Approve().Handle(PendingBusinessId, CancellationToken.None));

        Assert.Equal(BusinessDecisionPolicy.ApplicantUnavailableMessage, conflict.Message);
        AssertNothingWasDecided();
    }

    #endregion

    #region Provisioning the approved business (issue #849)

    /// <summary>
    /// An approval hands the copy of the enabled supplier presets to the provisioning port, once,
    /// inside the write transaction and before the commit, with the decision instant and with the
    /// business id of the row the transaction itself read - never the route value the request
    /// carried.
    /// </summary>
    [Fact]
    public async Task An_approval_provisions_the_business_inside_the_transaction_before_committing()
    {
        GivenPendingApplication();
        GivenApplicantMemberships(new IdentityMembership(ApplicantMembershipId, IsActive: true));

        await Approve().Handle(PendingBusinessId, CancellationToken.None);

        var copy = Assert.Single(_provisioning.Copies);
        Assert.Equal(PendingBusinessId, copy.BusinessId);
        Assert.Equal(DecidedAt, copy.CopiedAtUtc);

        var operations = _writeStore.Operations;
        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeBusinessMembershipWriteStore.IdentityRead,
                FakeBusinessSupplierProvisioningStore.Copy,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            operations);
    }

    /// <summary>
    /// A rejection provisions nothing: there is no business starting to trade, so there is nothing
    /// to equip, and a refused applicant must not leave suppliers behind on a business nobody can
    /// reach.
    /// </summary>
    [Fact]
    public async Task A_rejection_provisions_nothing()
    {
        GivenPendingApplication();

        await Reject().Handle(PendingBusinessId, "Not enough detail.", CancellationToken.None);

        Assert.Empty(_provisioning.Copies);
    }

    /// <summary>
    /// All or nothing: a copy that fails takes the approval with it. Nothing is committed and
    /// nothing is audited, so the application is still waiting for a decision rather than trading
    /// without the suppliers it was supposed to start with.
    /// </summary>
    [Fact]
    public async Task A_failed_copy_refuses_the_approval_and_audits_nothing()
    {
        GivenPendingApplication();
        GivenApplicantMemberships(new IdentityMembership(ApplicantMembershipId, IsActive: true));
        _provisioning.FailWith = new InvalidOperationException("The supplier preset copy failed.");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Approve().Handle(PendingBusinessId, CancellationToken.None));

        Assert.Equal("The supplier preset copy failed.", failure.Message);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, _writeStore.Operations);
        Assert.Contains(FakeBusinessMembershipWriteStore.Dispose, _writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// A refused approval never reaches the copy at all. The refusal here is the in-transaction one -
    /// the applicant changed while the write lock was awaited - because that is the case where the
    /// decision had already started.
    /// </summary>
    [Fact]
    public async Task An_approval_refused_inside_the_transaction_provisions_nothing()
    {
        GivenPendingApplication();
        GivenApplicantMemberships(new IdentityMembership(ApplicantMembershipId, IsActive: true));

        // Another membership becomes the business's single active one while this approval waits for
        // the write lock, so the exclusion this transaction was entered with no longer describes it.
        _writeStore.WhileWaitingForTheWriteLock = () =>
            _store.Targets[PendingBusinessId] = _store.Targets[PendingBusinessId] with
            {
                Applicant = new PlatformBusinessApplicant(
                    ApplicantMembershipId + 1,
                    _store.Targets[PendingBusinessId].Applicant!.Identity,
                    IsActive: true),
            };

        await Assert.ThrowsAsync<DomainConflictException>(
            () => Approve().Handle(PendingBusinessId, CancellationToken.None));

        Assert.Empty(_provisioning.Copies);
        AssertNothingWasDecided();
    }

    #endregion

    #region Refusing

    /// <summary>
    /// A rejection records the status, the reason and the instant, and releases the applicant's
    /// membership with the same instant - which is what lets them apply again.
    /// </summary>
    [Fact]
    public async Task Rejecting_records_the_reason_and_releases_the_applicants_membership()
    {
        GivenPendingApplication();

        var decision = await Reject().Handle(
            PendingBusinessId,
            "  Not enough detail about the sites.  ",
            CancellationToken.None);

        Assert.NotNull(decision);
        Assert.Equal(BusinessStatus.Rejected, decision!.Status);
        Assert.Equal("Not enough detail about the sites.", decision.RejectionReason);
        Assert.Equal(DecidedAt, decision.DecidedAtUtc);
        Assert.Equal("Not enough detail about the sites.", _store.RejectedReason);
        Assert.Equal(DecidedAt, _store.RejectedAtUtc);
        Assert.Equal(ApplicantMembershipId, _store.DeactivatedMembershipId);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(BusinessStatus.Pending, entry.FromStatus);
        Assert.Equal(BusinessStatus.Rejected, entry.ToStatus);
        Assert.True(entry.ReasonRecorded);
    }

    /// <summary>
    /// A rejection needs no reason. "No reason" has exactly one stored representation, so a blank
    /// one is <c>null</c> rather than a second kind of absence, and the audit trail says no reason
    /// was recorded.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejecting_without_a_reason_is_still_a_rejection(string? reason)
    {
        GivenPendingApplication();

        var decision = await Reject().Handle(PendingBusinessId, reason, CancellationToken.None);

        Assert.Equal(BusinessStatus.Rejected, decision!.Status);
        Assert.Null(decision.RejectionReason);
        Assert.Null(_store.RejectedReason);
        Assert.False(Assert.Single(_audit.Entries).ReasonRecorded);
    }

    /// <summary>
    /// An over-long reason refuses the whole decision before anything is read or written, rather
    /// than being silently truncated into the applicant's explanation.
    /// </summary>
    [Fact]
    public async Task A_reason_longer_than_the_limit_refuses_the_decision_and_writes_nothing()
    {
        GivenPendingApplication();

        var validation = await Assert.ThrowsAsync<DomainValidationException>(
            () => Reject().Handle(
                PendingBusinessId,
                new string('x', BusinessDecisionPolicy.MaxRejectionReasonLength + 1),
                CancellationToken.None));

        Assert.Equal(BusinessDecisionPolicy.RejectionReasonTooLongMessage, validation.Message);
        Assert.Empty(_store.Operations);
        Assert.Empty(_writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// Rejecting runs no eligibility check: it activates nothing. The membership read inside the
    /// transaction is therefore never made, which is the visible difference between the two
    /// decisions' intents.
    /// </summary>
    [Fact]
    public async Task Rejecting_runs_no_eligibility_check()
    {
        GivenPendingApplication();

        await Reject().Handle(PendingBusinessId, reason: null, CancellationToken.None);

        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.IdentityRead, _writeStore.Operations);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.BusinessRead, _writeStore.Operations);
        Assert.Contains(FakeBusinessMembershipWriteStore.Commit, _writeStore.Operations);
    }

    /// <summary>
    /// An applicant whose membership is already inactive has nothing to release, so the rejection
    /// records the business's decision and leaves that row - and its state-change instant - exactly
    /// as it was. Rewriting the instant would claim a transition that never happened.
    /// </summary>
    [Fact]
    public async Task Rejecting_leaves_an_already_inactive_membership_untouched()
    {
        GivenPendingApplication(applicantActive: false);

        var decision = await Reject().Handle(PendingBusinessId, "No.", CancellationToken.None);

        Assert.Equal(BusinessStatus.Rejected, decision!.Status);
        Assert.Null(_store.DeactivatedMembershipId);
    }

    #endregion

    #region Both decisions

    /// <summary>An unknown business is "not found", for either decision, and writes nothing.</summary>
    [Fact]
    public async Task An_unknown_business_is_not_found()
    {
        Assert.Null(await Approve().Handle(PendingBusinessId, CancellationToken.None));
        Assert.Null(await Reject().Handle(PendingBusinessId, reason: null, CancellationToken.None));

        Assert.Empty(_writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// Only a pending application may be decided. An already-active, already-rejected, deactivated
    /// or undeclared status is refused with one sentence that names no status, and nothing is
    /// written or audited.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Active)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    [InlineData((BusinessStatus)0)]
    [InlineData((BusinessStatus)99)]
    public async Task Only_a_pending_application_may_be_decided(BusinessStatus status)
    {
        GivenPendingApplication();
        _store.Targets[PendingBusinessId] = _store.Targets[PendingBusinessId] with { Status = status };

        var approving = await Assert.ThrowsAsync<DomainConflictException>(
            () => Approve().Handle(PendingBusinessId, CancellationToken.None));
        var rejecting = await Assert.ThrowsAsync<DomainConflictException>(
            () => Reject().Handle(PendingBusinessId, reason: null, CancellationToken.None));

        Assert.Equal(BusinessDecisionPolicy.NotPendingMessage, approving.Message);
        Assert.Equal(BusinessDecisionPolicy.NotPendingMessage, rejecting.Message);
        AssertNothingWasDecided();
    }

    /// <summary>
    /// The authoritative read is inside the write transaction, not wherever the caller last looked.
    /// The status changes while this decision waits for the write lock - which is what a concurrent
    /// decision committing looks like - and the re-read inside the lock refuses it. Without that
    /// second read the approval would be written over a business somebody else had already decided.
    /// </summary>
    [Fact]
    public async Task A_status_that_changes_while_the_write_lock_is_awaited_refuses_the_decision()
    {
        GivenPendingApplication();
        GivenApplicantMemberships(new IdentityMembership(ApplicantMembershipId, IsActive: true));

        _writeStore.WhileWaitingForTheWriteLock = () =>
            _store.Targets[PendingBusinessId] = _store.Targets[PendingBusinessId] with
            {
                Status = BusinessStatus.Rejected,
            };

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Approve().Handle(PendingBusinessId, CancellationToken.None));

        Assert.Equal(BusinessDecisionPolicy.NotPendingMessage, conflict.Message);
        Assert.DoesNotContain(FakePlatformBusinessDirectoryStore.Approve, _store.Operations);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, _writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// The applicant changing between the two reads refuses the approval too: an eligibility
    /// exclusion granted for one membership key must never be spent on another.
    /// </summary>
    [Fact]
    public async Task An_applicant_that_changes_while_the_write_lock_is_awaited_refuses_the_approval()
    {
        GivenPendingApplication();
        GivenApplicantMemberships(new IdentityMembership(ApplicantMembershipId, IsActive: true));

        _writeStore.WhileWaitingForTheWriteLock = () =>
            _store.Targets[PendingBusinessId] = _store.Targets[PendingBusinessId] with
            {
                Applicant = new PlatformBusinessApplicant(
                    ApplicantMembershipId + 5,
                    ApplicantIdentity(),
                    IsActive: true),
            };

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Approve().Handle(PendingBusinessId, CancellationToken.None));

        Assert.Equal(BusinessDecisionPolicy.ApplicantUnavailableMessage, conflict.Message);
        Assert.DoesNotContain(FakePlatformBusinessDirectoryStore.Approve, _store.Operations);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// A request with no identifiable actor records no decision at all. The platform-admin policy
    /// has already established that the caller is the configured administrator, so this is the
    /// fail-closed answer to a pipeline that somehow let an unidentified caller through - never a
    /// decision nobody can be held to.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_identifiable_actor_decides_nothing()
    {
        GivenPendingApplication();

        var writer = new BusinessApplicationDecisionWriter(
            FakeAuthenticatedActorAccessor.NotAuthenticated(),
            _store,
            _provisioning,
            new MembershipWriteGuard(_writeStore),
            _audit,
            new FakeClock(DecidedAt));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new ApproveBusinessApplication(writer).Handle(PendingBusinessId, CancellationToken.None));

        Assert.Empty(_store.Operations);
        Assert.Empty(_audit.Entries);
    }

    #endregion

    #region Reads

    /// <summary>
    /// The directory read is a pass-through of the store's fixed list, and the pending count answers
    /// the same question about the same rows. Neither takes a filter, a page or a sort.
    /// </summary>
    [Fact]
    public async Task The_directory_lists_what_the_store_reports_and_counts_the_pending_ones()
    {
        _store.Businesses.Add(Summary(1, BusinessStatus.Active));
        _store.Businesses.Add(Summary(2, BusinessStatus.Pending));
        _store.Businesses.Add(Summary(3, BusinessStatus.Pending));

        var listed = await new ListPlatformBusinesses(_store).Handle(CancellationToken.None);

        Assert.Equal([1, 2, 3], listed.Select(business => business.BusinessId));
        Assert.Equal(2, await new CountPendingBusinessApplications(_store).Handle(CancellationToken.None));
    }

    private static PlatformBusinessSummary Summary(int businessId, BusinessStatus status) =>
        new(
            businessId,
            $"Business {businessId}",
            status,
            DecidedAt,
            AppliedAtUtc: null,
            DecidedAtUtc: null,
            ContactEmail: null,
            Abn: null,
            State: "NSW",
            ActiveMemberCount: 1,
            NayaxConfigured: false,
            NayaxConnectionStatus: NayaxConnectionStatus.NotConfigured);

    #endregion

    #region Arrangement

    private void GivenPendingApplication(bool applicantActive = true) =>
        _store.Targets[PendingBusinessId] = new PlatformBusinessDecisionTarget(
            PendingBusinessId,
            "Coastal Vending",
            BusinessStatus.Pending,
            new PlatformBusinessApplicant(ApplicantMembershipId, ApplicantIdentity(), applicantActive));

    private void GivenApplicantMemberships(params IdentityMembership[] memberships) =>
        _writeStore.IdentityMemberships.AddRange(memberships);

    private static ActorIdentity ApplicantIdentity()
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, ApplicantObjectId, out var identity));
        return identity!;
    }

    private static ActorIdentity Administrator()
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, AdministratorObjectId, out var identity));
        return identity!;
    }

    private ApproveBusinessApplication Approve() => new(Writer());

    private RejectBusinessApplication Reject() => new(Writer());

    private BusinessApplicationDecisionWriter Writer() =>
        new(
            FakeAuthenticatedActorAccessor.Identified(Administrator()),
            _store,
            _provisioning,
            new MembershipWriteGuard(_writeStore),
            _audit,
            new FakeClock(DecidedAt));

    private void AssertNothingWasDecided()
    {
        Assert.DoesNotContain(FakePlatformBusinessDirectoryStore.Approve, _store.Operations);
        Assert.DoesNotContain(FakePlatformBusinessDirectoryStore.Reject, _store.Operations);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, _writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    #endregion
}
