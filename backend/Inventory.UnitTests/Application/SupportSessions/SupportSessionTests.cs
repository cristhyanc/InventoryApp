using Inventory.Application.PlatformAdmin;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Members;
using InventoryApi.Tests.Application.PlatformAdmin;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.SupportSessions;

/// <summary>
/// Starting, reading and ending a support session, and what a business deactivation does to one
/// (issue #550, a slice of #512 under #328): the rules, the order they are applied in, what each
/// call writes, and what each refusal leaves behind.
///
/// <para>These tests are over the real <see cref="MembershipWriteGuard"/> with the membership write
/// port faked, because the guard's transaction is where the facts that matter are re-read. Two
/// properties are what the fakes exist to make visible: that the business status and the "do I
/// already have a session?" check are read <em>inside</em> the write transaction, and that an end -
/// whether the administrator's own or a deactivation's - is audited from the rows that were actually
/// closed. The relational halves (the columns each statement touches, the cross-business boundary)
/// are in <c>EfSupportSessionStoreTests</c>, and the HTTP half in
/// <c>SuperAdminSupportSessionsApiTests</c>.</para>
/// </summary>
public class SupportSessionTests
{
    private const int BusinessId = 7;
    private const int AnotherBusinessId = 8;
    private const string BusinessName = "Vending Co";
    private const string DirectoryTenantId = "11111111-1111-1111-1111-111111111111";
    private const string AdministratorObjectId = "99999999-9999-9999-9999-999999999999";
    private const string AnotherAdministratorObjectId = "88888888-8888-8888-8888-888888888888";
    private const string Reason = "Investigating the reported dashboard error";

    private static readonly DateTime ActedAt = new(2026, 10, 13, 2, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime StartedEarlier = ActedAt.AddMinutes(-10);

    private readonly FakePlatformBusinessDirectoryStore _businesses = new();
    private readonly FakeSupportSessionStore _sessions = new();
    private readonly FakeBusinessMembershipWriteStore _writeStore = new();
    private readonly FakeSupportSessionAudit _audit = new();

    public SupportSessionTests()
    {
        // Both report into the write store's own list, so one assertion can read the whole order:
        // the transaction opening, the reads, the write, the commit, and the audit event after it.
        _sessions.Observer = _writeStore.Record;
        _audit.Observer = _writeStore.Record;
    }

    #region Starting a session

    /// <summary>
    /// The criterion: a start for an active business stores the session with the actor, the business,
    /// the reason, the instant and the server's own expiry, and answers with the session id, the
    /// business name and that expiry. The id is stored only as a hash, so the row holds nothing a
    /// request could present.
    /// </summary>
    [Fact]
    public async Task Starting_a_session_stores_it_and_returns_the_id_name_and_expiry()
    {
        GivenBusiness(BusinessStatus.Active);

        var started = await Start().Handle(BusinessId, Reason, CancellationToken.None);

        Assert.NotNull(started);
        Assert.Equal(BusinessId, started!.BusinessId);
        Assert.Equal(BusinessName, started.BusinessName);
        Assert.Equal(ActedAt, started.StartedAtUtc);
        Assert.Equal(SupportSessionPolicy.ExpiresAt(ActedAt), started.ExpiresAtUtc);

        // The id is a credential: it is returned once, and what is stored is its hash.
        Assert.False(string.IsNullOrWhiteSpace(started.SessionId));
        Assert.Equal(SupportSessionId.Hash(started.SessionId), _sessions.LastSessionIdHash);

        var row = Assert.Single(_sessions.Rows);
        Assert.Equal(BusinessId, row.BusinessId);
        Assert.Equal(DirectoryTenantId, row.ActorDirectoryTenantId);
        Assert.Equal(AdministratorObjectId, row.ActorObjectId);
        Assert.Equal(Reason, row.Reason);
        Assert.Equal(ActedAt, row.StartedAtUtc);
        Assert.Equal(SupportSessionPolicy.ExpiresAt(ActedAt), row.ExpiresAtUtc);
        Assert.Null(row.EndedAtUtc);
    }

    /// <summary>
    /// Two sessions started a moment apart never share an id. The id is drawn from the cryptographic
    /// generator, so this asserts the one property a guessable id would break - and it is asserted on
    /// the hashes as well, because that is what a lookup will match on.
    /// </summary>
    [Fact]
    public async Task Two_sessions_never_share_an_id()
    {
        GivenBusiness(BusinessStatus.Active);

        var first = await Start().Handle(BusinessId, Reason, CancellationToken.None);
        var firstHash = _sessions.LastSessionIdHash;
        await End().Handle(CancellationToken.None);
        var second = await Start().Handle(BusinessId, Reason, CancellationToken.None);

        Assert.NotEqual(first!.SessionId, second!.SessionId);
        Assert.NotEqual(firstHash, _sessions.LastSessionIdHash);
    }

    /// <summary>
    /// The status read, the one-active-session check and the insert are all inside one transaction,
    /// in that order, and the audit event comes after the commit. The order is the point: a check
    /// made before the write lock was taken is a check another start could overtake, and a session
    /// audited before the commit would be a trail of access that may never have existed.
    /// </summary>
    [Fact]
    public async Task Starting_a_session_reads_and_checks_inside_the_transaction_and_audits_after_it()
    {
        GivenBusiness(BusinessStatus.Active);

        await Start().Handle(BusinessId, Reason, CancellationToken.None);

        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeSupportSessionStore.ActiveRead,
                FakeSupportSessionStore.Start,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
                FakeSupportSessionAudit.Recorded,
            ],
            _writeStore.Operations);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(AdministratorObjectId, entry.ActorObjectId);
        Assert.Equal(BusinessId, entry.BusinessId);
        Assert.Equal(SupportSessionEvent.Started, entry.Event);
        Assert.Equal(Reason, entry.Reason);
        Assert.Equal(ActedAt, entry.StartedAtUtc);
        Assert.Equal(SupportSessionPolicy.ExpiresAt(ActedAt), entry.ExpiresAtUtc);
        Assert.Null(entry.EndedAtUtc);
        Assert.Null(entry.EndReason);
    }

    /// <summary>
    /// The criterion: a session may be started only for an active business. A pending application, a
    /// refused one, a deactivated business and an undeclared stored status are each refused with the
    /// one sentence, and nothing is stored or audited.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    [InlineData((BusinessStatus)0)]
    [InlineData((BusinessStatus)99)]
    public async Task A_session_may_be_started_only_for_an_active_business(BusinessStatus status)
    {
        GivenBusiness(status);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Start().Handle(BusinessId, Reason, CancellationToken.None));

        Assert.Equal(SupportSessionPolicy.BusinessNotActiveMessage, conflict.Message);
        AssertNothingWasWritten();
    }

    /// <summary>
    /// The criterion: one active session per administrator. A second start while one is still active
    /// is refused, the first session is left exactly as it is, and nothing is audited - so a refusal
    /// cannot read as a second grant of access.
    /// </summary>
    [Fact]
    public async Task A_second_session_is_refused_while_one_is_still_active()
    {
        GivenBusiness(BusinessStatus.Active);
        GivenBusiness(BusinessStatus.Active, AnotherBusinessId, "Coastal Vending");
        var existing = GivenSession();

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Start().Handle(AnotherBusinessId, Reason, CancellationToken.None));

        Assert.Equal(SupportSessionPolicy.AlreadyActiveMessage, conflict.Message);
        Assert.Single(_sessions.Rows);
        Assert.Null(_sessions.Row(existing).EndedAtUtc);
        AssertNothingWasWritten();
    }

    /// <summary>
    /// A session that has expired, and one that was ended, both leave the administrator free to start
    /// another: the rule is about <em>active</em> sessions, which is derived from the expiry rather
    /// than swept, so nothing has to run for an expired session to stop counting.
    /// </summary>
    [Fact]
    public async Task An_expired_or_ended_session_does_not_block_a_new_one()
    {
        GivenBusiness(BusinessStatus.Active);
        GivenSession(startedAtUtc: ActedAt.AddHours(-3), expiresAtUtc: ActedAt.AddHours(-2));
        GivenSession(endedAtUtc: ActedAt.AddMinutes(-1));

        var started = await Start().Handle(BusinessId, Reason, CancellationToken.None);

        Assert.NotNull(started);
        Assert.Equal(3, _sessions.Rows.Count);
    }

    /// <summary>
    /// Another administrator's active session is not this one's. The rule is per administrator, and
    /// the read is by the authenticated actor, which is also what makes a session unreachable to
    /// anybody but the person it was granted to.
    /// </summary>
    [Fact]
    public async Task Another_administrators_active_session_does_not_block_a_start()
    {
        GivenBusiness(BusinessStatus.Active);
        GivenSession(actor: Administrator(AnotherAdministratorObjectId));

        var started = await Start().Handle(BusinessId, Reason, CancellationToken.None);

        Assert.NotNull(started);
        Assert.Equal(Administrator(AdministratorObjectId), _sessions.LastQueriedActor);
    }

    /// <summary>
    /// The authoritative reads are inside the write transaction, not wherever the caller last looked.
    /// A deactivation committing while this start waits for the write lock refuses it - which is the
    /// case the whole frame exists for, because the business it would have read is no longer one a
    /// supporter may read.
    /// </summary>
    [Fact]
    public async Task A_status_that_changes_while_the_write_lock_is_awaited_refuses_the_start()
    {
        GivenBusiness(BusinessStatus.Active);
        _writeStore.WhileWaitingForTheWriteLock = () => GivenBusiness(BusinessStatus.Deactivated);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Start().Handle(BusinessId, Reason, CancellationToken.None));

        Assert.Equal(SupportSessionPolicy.BusinessNotActiveMessage, conflict.Message);
        AssertNothingWasWritten();
    }

    /// <summary>
    /// The same for the one-session rule: a session committed by this administrator's other request
    /// while this start waits for the lock refuses it, rather than being overtaken by a view that was
    /// accurate a moment earlier.
    /// </summary>
    [Fact]
    public async Task A_session_started_while_the_write_lock_is_awaited_refuses_the_start()
    {
        GivenBusiness(BusinessStatus.Active);
        _writeStore.WhileWaitingForTheWriteLock = () => GivenSession();

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Start().Handle(BusinessId, Reason, CancellationToken.None));

        Assert.Equal(SupportSessionPolicy.AlreadyActiveMessage, conflict.Message);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, _writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// A reason is required and bounded, and both refusals happen before anything is read, generated
    /// or written: no business read, no transaction, no session id and no audit event.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_start_with_no_reason_is_refused_before_anything_is_read(string? reason)
    {
        GivenBusiness(BusinessStatus.Active);

        var invalid = await Assert.ThrowsAsync<DomainValidationException>(
            () => Start().Handle(BusinessId, reason, CancellationToken.None));

        Assert.Equal(SupportSessionPolicy.ReasonRequiredMessage, invalid.Message);
        Assert.Empty(_businesses.Operations);
        Assert.Empty(_writeStore.Operations);
        Assert.Empty(_sessions.Rows);
    }

    [Fact]
    public async Task A_start_with_an_over_long_reason_is_refused_before_anything_is_read()
    {
        GivenBusiness(BusinessStatus.Active);

        var invalid = await Assert.ThrowsAsync<DomainValidationException>(
            () => Start().Handle(
                BusinessId,
                new string('x', SupportSessionPolicy.MaxReasonLength + 1),
                CancellationToken.None));

        Assert.Equal(SupportSessionPolicy.ReasonTooLongMessage, invalid.Message);
        Assert.Empty(_businesses.Operations);
        Assert.Empty(_writeStore.Operations);
        Assert.Empty(_sessions.Rows);
    }

    /// <summary>The stored reason is the trimmed one, so one reason has one representation.</summary>
    [Fact]
    public async Task The_stored_reason_is_trimmed()
    {
        GivenBusiness(BusinessStatus.Active);

        await Start().Handle(BusinessId, $"  {Reason}\t", CancellationToken.None);

        Assert.Equal(Reason, Assert.Single(_sessions.Rows).Reason);
    }

    /// <summary>An unknown business is "not found", and writes nothing.</summary>
    [Fact]
    public async Task An_unknown_business_is_not_found()
    {
        Assert.Null(await Start().Handle(BusinessId, Reason, CancellationToken.None));

        AssertNothingWasWritten();
    }

    /// <summary>
    /// A request with no identifiable actor starts nothing at all. The platform-admin policy has
    /// already established that the caller is the configured administrator, so this is the
    /// fail-closed answer to a pipeline that somehow let an unidentified caller through - never a
    /// session nobody can be held to.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_identifiable_actor_starts_reads_and_ends_nothing()
    {
        GivenBusiness(BusinessStatus.Active);
        var unauthenticated = FakeAuthenticatedActorAccessor.NotAuthenticated();

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => Start(unauthenticated).Handle(BusinessId, Reason, CancellationToken.None));
        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => End(unauthenticated).Handle(CancellationToken.None));
        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => Active(unauthenticated).Handle(CancellationToken.None));

        AssertNothingWasWritten();
    }

    #endregion

    #region Reading the active session

    /// <summary>
    /// The criterion: the active session is reported with the business, its name, the reason and both
    /// instants - and never the session id, which exists in one response only. It is a read: no
    /// transaction, and no audit event.
    /// </summary>
    [Fact]
    public async Task The_active_session_is_reported_without_a_transaction_or_an_audit_event()
    {
        GivenBusiness(BusinessStatus.Active);
        GivenSession();

        var active = await Active().Handle(CancellationToken.None);

        Assert.NotNull(active);
        Assert.Equal(BusinessId, active!.BusinessId);
        Assert.Equal(BusinessName, active.BusinessName);
        Assert.Equal(Reason, active.Reason);
        Assert.Equal(StartedEarlier, active.StartedAtUtc);
        Assert.Equal(SupportSessionPolicy.ExpiresAt(StartedEarlier), active.ExpiresAtUtc);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Begin, _writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// An expired session, an ended one and another administrator's are each "no active session", and
    /// so is having none at all. Expiry needs nothing to have run: the rule reads the stored expiry.
    /// </summary>
    [Fact]
    public async Task An_expired_ended_or_other_administrators_session_is_not_reported()
    {
        GivenBusiness(BusinessStatus.Active);

        Assert.Null(await Active().Handle(CancellationToken.None));

        GivenSession(startedAtUtc: ActedAt.AddHours(-3), expiresAtUtc: ActedAt.AddHours(-2));
        GivenSession(endedAtUtc: ActedAt.AddMinutes(-1));
        GivenSession(actor: Administrator(AnotherAdministratorObjectId));

        Assert.Null(await Active().Handle(CancellationToken.None));
    }

    /// <summary>
    /// A session is active up to - but not at - its expiry instant. Sixty minutes of access is the
    /// promise, and a session that answered on the first tick after it would have outlived it.
    /// </summary>
    [Fact]
    public async Task A_session_stops_being_active_at_its_expiry_instant()
    {
        GivenBusiness(BusinessStatus.Active);
        GivenSession(startedAtUtc: ActedAt.AddMinutes(-60), expiresAtUtc: ActedAt);

        Assert.Null(await Active().Handle(CancellationToken.None));

        GivenSession(startedAtUtc: ActedAt.AddMinutes(-60), expiresAtUtc: ActedAt.AddTicks(1));

        Assert.NotNull(await Active().Handle(CancellationToken.None));
    }

    #endregion

    #region Ending a session

    /// <summary>
    /// The criterion: an explicit end closes the session at the instant it was asked for, reports
    /// what it closed, and audits it once with that instant and the reason it ended for.
    /// </summary>
    [Fact]
    public async Task Ending_a_session_records_the_instant_and_audits_the_end()
    {
        GivenBusiness(BusinessStatus.Active);
        var session = GivenSession();

        var ended = await End().Handle(CancellationToken.None);

        Assert.NotNull(ended);
        Assert.Equal(BusinessId, ended!.BusinessId);
        Assert.Equal(BusinessName, ended.BusinessName);
        Assert.Equal(StartedEarlier, ended.StartedAtUtc);
        Assert.Equal(ActedAt, ended.EndedAtUtc);
        Assert.Equal(ActedAt, _sessions.Row(session).EndedAtUtc);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(SupportSessionEvent.Ended, entry.Event);
        Assert.Equal(SupportSessionEndReason.EndedByAdministrator, entry.EndReason);
        Assert.Equal(AdministratorObjectId, entry.ActorObjectId);
        Assert.Equal(BusinessId, entry.BusinessId);
        Assert.Equal(ActedAt, entry.EndedAtUtc);

        // The start and the end name the same session row, which is what makes the trail readable as
        // one session rather than two unrelated events.
        Assert.Equal(session, entry.SessionKey);
    }

    /// <summary>
    /// The end is inside the write transaction and audited after the commit, exactly as the start is:
    /// an end audited before the commit would claim access had stopped when it may not have.
    /// </summary>
    [Fact]
    public async Task Ending_a_session_writes_inside_the_transaction_and_audits_after_it()
    {
        GivenBusiness(BusinessStatus.Active);
        GivenSession();

        await End().Handle(CancellationToken.None);

        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeSupportSessionStore.EndForActor,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
                FakeSupportSessionAudit.Recorded,
            ],
            _writeStore.Operations);
    }

    /// <summary>
    /// Ending when nothing is active is accepted and writes nothing: the administrator asked for the
    /// state they are already in. An expired session is not re-closed - rewriting its instant would
    /// claim a transition that never happened - and nothing is audited, because nothing changed.
    /// </summary>
    [Fact]
    public async Task Ending_when_nothing_is_active_writes_nothing()
    {
        GivenBusiness(BusinessStatus.Active);
        var expired = GivenSession(startedAtUtc: ActedAt.AddHours(-3), expiresAtUtc: ActedAt.AddHours(-2));

        Assert.Null(await End().Handle(CancellationToken.None));

        Assert.Null(_sessions.Row(expired).EndedAtUtc);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// An end closes the caller's own session and nobody else's, which is the same boundary the start
    /// check rests on: the actor is the authenticated one, never a value a request supplied.
    /// </summary>
    [Fact]
    public async Task Ending_a_session_leaves_another_administrators_session_running()
    {
        GivenBusiness(BusinessStatus.Active);
        var mine = GivenSession();
        var theirs = GivenSession(actor: Administrator(AnotherAdministratorObjectId));

        await End().Handle(CancellationToken.None);

        Assert.Equal(ActedAt, _sessions.Row(mine).EndedAtUtc);
        Assert.Null(_sessions.Row(theirs).EndedAtUtc);
        Assert.Single(_audit.Entries);
    }

    #endregion

    #region A deactivation ends the sessions of that business

    /// <summary>
    /// The criterion: deactivating a business ends every active session for it, at the instant the
    /// deactivation recorded, and audits each one as ended by the deactivation. Another business's
    /// session keeps running, and a session that had already ended is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_deactivation_ends_every_active_session_of_that_business()
    {
        var mine = GivenSession();
        var anothers = GivenSession(actor: Administrator(AnotherAdministratorObjectId));
        var alreadyEnded = GivenSession(endedAtUtc: StartedEarlier);
        var otherBusiness = GivenSession(businessId: AnotherBusinessId);

        await Hook().OnBusinessDeactivatedAsync(
            new BusinessDeactivated(BusinessId, ActedAt),
            CancellationToken.None);

        Assert.Equal(ActedAt, _sessions.Row(mine).EndedAtUtc);
        Assert.Equal(ActedAt, _sessions.Row(anothers).EndedAtUtc);
        Assert.Equal(StartedEarlier, _sessions.Row(alreadyEnded).EndedAtUtc);
        Assert.Null(_sessions.Row(otherBusiness).EndedAtUtc);

        Assert.Equal(2, _audit.Entries.Count);
        Assert.All(_audit.Entries, entry =>
        {
            Assert.Equal(SupportSessionEvent.Ended, entry.Event);
            Assert.Equal(SupportSessionEndReason.BusinessDeactivated, entry.EndReason);
            Assert.Equal(BusinessId, entry.BusinessId);
            Assert.Equal(ActedAt, entry.EndedAtUtc);
        });

        // Each end is attributed to the administrator whose session it was, not to whoever
        // deactivated the business: the trail answers "whose access stopped".
        Assert.Equal(
            [AdministratorObjectId, AnotherAdministratorObjectId],
            _audit.Entries.Select(entry => entry.ActorObjectId));
    }

    /// <summary>
    /// A deactivation with no session to end writes nothing and audits nothing, so the hook cannot
    /// make an ordinary deactivation look like one that cut somebody's access off.
    /// </summary>
    [Fact]
    public async Task A_deactivation_with_no_active_session_audits_nothing()
    {
        GivenSession(businessId: AnotherBusinessId);

        await Hook().OnBusinessDeactivatedAsync(
            new BusinessDeactivated(BusinessId, ActedAt),
            CancellationToken.None);

        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// The hook is what the real deactivation transition calls, inside its own transaction: this is
    /// the whole feature end to end, with the lifecycle writer of issue #545 unchanged. The session
    /// ends, the deactivation is recorded, and the reactivation that follows does <em>not</em> revive
    /// the session - a business coming back is not a reason to restore access somebody else had to
    /// it.
    /// </summary>
    [Fact]
    public async Task A_deactivation_through_the_real_transition_ends_the_session_and_a_reactivation_does_not_revive_it()
    {
        GivenBusiness(BusinessStatus.Active);
        var session = GivenSession();

        var deactivation = await new DeactivateBusiness(LifecycleWriter())
            .Handle(BusinessId, CancellationToken.None);

        Assert.NotNull(deactivation);
        Assert.Equal(ActedAt, _sessions.Row(session).EndedAtUtc);

        // Inside the transaction the deactivation opened: the end is between the lock being taken
        // and the commit, so a rolled-back deactivation could not have ended it.
        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeSupportSessionStore.EndForBusiness,
                FakeSupportSessionAudit.Recorded,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            _writeStore.Operations);

        await new ReactivateBusiness(LifecycleWriter()).Handle(BusinessId, CancellationToken.None);

        Assert.Equal(ActedAt, _sessions.Row(session).EndedAtUtc);
        Assert.Null(await Active().Handle(CancellationToken.None));
    }

    /// <summary>
    /// A deactivation that is refused leaves the session running: the hook never runs at all, because
    /// the status gate refuses before the transaction writes anything. The direction matters - a
    /// session ended by a deactivation that did not happen would have been cut off for nothing.
    /// </summary>
    [Fact]
    public async Task A_refused_deactivation_leaves_the_session_running()
    {
        GivenBusiness(BusinessStatus.Deactivated);
        var session = GivenSession();

        await Assert.ThrowsAsync<DomainConflictException>(
            () => new DeactivateBusiness(LifecycleWriter()).Handle(BusinessId, CancellationToken.None));

        Assert.Null(_sessions.Row(session).EndedAtUtc);
        Assert.Empty(_audit.Entries);
    }

    #endregion

    #region Arrangement

    private void GivenBusiness(
        BusinessStatus status,
        int businessId = BusinessId,
        string name = BusinessName) =>
        _businesses.LifecycleTargets[businessId] = new PlatformBusinessLifecycleTarget(
            businessId,
            name,
            status,
            [new PlatformBusinessMember(41, "Robin Owner", BusinessRole.Owner, IsActive: true)]);

    private int GivenSession(
        int businessId = BusinessId,
        ActorIdentity? actor = null,
        DateTime? startedAtUtc = null,
        DateTime? expiresAtUtc = null,
        DateTime? endedAtUtc = null)
    {
        var startedAt = startedAtUtc ?? StartedEarlier;

        return _sessions.Add(
            businessId,
            actor ?? Administrator(AdministratorObjectId),
            startedAt,
            expiresAtUtc ?? SupportSessionPolicy.ExpiresAt(startedAt),
            endedAtUtc,
            Reason);
    }

    private StartSupportSession Start() => Start(IdentifiedAdministrator());

    private StartSupportSession Start(IAuthenticatedActorAccessor actorAccessor) =>
        new(
            actorAccessor,
            _businesses,
            _sessions,
            new MembershipWriteGuard(_writeStore),
            _audit,
            new FakeClock(ActedAt));

    private EndSupportSession End() => End(IdentifiedAdministrator());

    private EndSupportSession End(IAuthenticatedActorAccessor actorAccessor) =>
        new(
            actorAccessor,
            _businesses,
            _sessions,
            new MembershipWriteGuard(_writeStore),
            _audit,
            new FakeClock(ActedAt));

    private GetActiveSupportSession Active() => Active(IdentifiedAdministrator());

    private GetActiveSupportSession Active(IAuthenticatedActorAccessor actorAccessor) =>
        new(actorAccessor, _businesses, _sessions, new FakeClock(ActedAt));

    private EndSupportSessionsOnBusinessDeactivation Hook() => new(_sessions, _audit);

    /// <summary>
    /// Issue #545's deactivation transition exactly as it ships, with this slice's hook registered
    /// where the composition root registers it. Nothing in that transition is changed by #550 - the
    /// only difference is which implementation of its hook port runs.
    /// </summary>
    private BusinessLifecycleWriter LifecycleWriter() =>
        new(
            IdentifiedAdministrator(),
            _businesses,
            new FakeMemberInviteStore(),
            new MembershipWriteGuard(_writeStore),
            Hook(),
            new FakePlatformBusinessLifecycleAudit(),
            new FakeClock(ActedAt));

    private static FakeAuthenticatedActorAccessor IdentifiedAdministrator() =>
        FakeAuthenticatedActorAccessor.Identified(Administrator(AdministratorObjectId));

    private static ActorIdentity Administrator(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, objectId, out var identity));
        return identity!;
    }

    private void AssertNothingWasWritten()
    {
        Assert.DoesNotContain(FakeSupportSessionStore.Start, _writeStore.Operations);
        Assert.DoesNotContain(FakeSupportSessionStore.EndForActor, _writeStore.Operations);
        Assert.DoesNotContain(FakeSupportSessionStore.EndForBusiness, _writeStore.Operations);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, _writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    #endregion
}
