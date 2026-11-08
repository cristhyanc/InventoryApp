using Inventory.Application.NayaxSqs;
using Inventory.Application.PlatformNayaxSqs;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformNayaxSqs;

/// <summary>
/// The Super Admin's Nayax SQS sender identity (issue #1101), without ASP.NET Core, EF Core or a
/// database: what a decision writes, what it records, and every refusal that writes nothing.
///
/// <para>The authorization that decides <em>who</em> may reach it is the boundary's and is asserted
/// over real HTTP in <c>SuperAdminNayaxSqsSenderApiTests</c>; the relational guarantees - the write
/// lock, the ownership lease, the global unique index and the sender row and its audit row committing
/// together - are asserted against real SQLite in <c>EfPlatformNayaxSqsSenderStoreTests</c>.</para>
/// </summary>
public class ManageBusinessNayaxSqsSenderTests
{
    private const int BusinessId = 7;
    private const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
    private const string AdministratorObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string SenderId = "AIDAEXAMPLEUSERID123";
    private const string OtherSenderId = "AIDAEXAMPLEUSERID456";

    private static readonly DateTime DecidedAt = new(2026, 11, 6, 3, 45, 0, DateTimeKind.Utc);

    #region Reading

    /// <summary>
    /// "404 when no row has ever existed" is the use case's answer rather than the controller's: a
    /// business that has never been recorded is the absence of a resource, not an empty one, which is
    /// also what makes `expectedRevision: null` the only way to create the first row.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_recorded_sender_reads_as_not_found()
    {
        var store = FakePlatformNayaxSqsSenderStore.WithBusiness(BusinessId);

        Assert.Null(await Subject(store).GetAsync(BusinessId, CancellationToken.None));
    }

    [Fact]
    public async Task A_recorded_sender_reads_back_with_its_revision_and_last_message()
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Sender(SenderId, isEnabled: true, revision: 3));

        var view = await Subject(store).GetAsync(BusinessId, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(BusinessId, view.BusinessId);
        Assert.Equal(SenderId, view.Sender.AwsSenderId);
        Assert.True(view.Sender.IsEnabled);
        Assert.Equal(3, view.Sender.Revision);
        Assert.Equal(DecidedAt.AddDays(-1), view.Sender.LastMessageReceivedAtUtc);
    }

    [Fact]
    public async Task An_unknown_business_is_not_found_and_nothing_is_written()
    {
        var store = FakePlatformNayaxSqsSenderStore.WithBusiness(BusinessId);
        var subject = Subject(store);

        Assert.Null(await subject.GetAsync(404, CancellationToken.None));
        Assert.Null(await subject.SetAsync(
            404, SenderId, isEnabled: true, expectedRevision: null, CancellationToken.None));

        Assert.Empty(store.Writes);
        Assert.Empty(store.Transactions);
    }

    #endregion

    #region Recording, changing and removing

    /// <summary>
    /// The first recording: the trimmed id, the flag, revision 1, both instants from the clock, and
    /// one audit entry naming the administrator - with no sender id on it, because that entry is what
    /// reaches a log.
    /// </summary>
    [Fact]
    public async Task A_first_recording_is_written_with_revision_one_and_audited_once()
    {
        var store = FakePlatformNayaxSqsSenderStore.WithBusiness(BusinessId);
        var audit = new FakePlatformNayaxSqsSenderAudit();

        var view = await Subject(store, audit).SetAsync(
            BusinessId, $"  {SenderId}  ", isEnabled: true, expectedRevision: null, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(SenderId, view.Sender.AwsSenderId);
        Assert.True(view.Sender.IsEnabled);
        Assert.Equal(1, view.Sender.Revision);
        Assert.Equal(DecidedAt, view.Sender.CreatedAtUtc);
        Assert.Equal(DecidedAt, view.Sender.UpdatedAtUtc);
        Assert.Null(view.Sender.LastMessageReceivedAtUtc);

        var write = Assert.Single(store.Writes);
        Assert.Equal(BusinessId, write.BusinessId);
        Assert.Equal(SenderId, write.AwsSenderId);
        Assert.True(write.IsEnabled);
        Assert.Equal(1, write.Revision);
        Assert.Null(write.FromAwsSenderId);
        Assert.False(write.FromIsEnabled);
        Assert.Equal(DecidedAt, write.OccurredAtUtc);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(Administrator().DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(Administrator().ObjectId, entry.ActorObjectId);
        Assert.Equal(BusinessId, entry.BusinessId);
        Assert.False(entry.HadSenderId);
        Assert.True(entry.HasSenderId);
        Assert.False(entry.WasEnabled);
        Assert.True(entry.IsEnabled);
        Assert.Equal(1, entry.Revision);
        Assert.Equal(DecidedAt, entry.OccurredAtUtc);

        var transaction = Assert.Single(store.Transactions);
        Assert.True(transaction.Committed);
        Assert.True(transaction.Disposed);
    }

    /// <summary>
    /// Changing the recorded id keeps the row and its creation instant, increments the revision, and
    /// records both sides on the audit - which is how "what was it before?" stays answerable.
    /// </summary>
    [Fact]
    public async Task Changing_the_sender_increments_the_revision_and_records_both_sides()
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Sender(SenderId, isEnabled: true, revision: 1));
        var audit = new FakePlatformNayaxSqsSenderAudit();

        var view = await Subject(store, audit).SetAsync(
            BusinessId, OtherSenderId, isEnabled: true, expectedRevision: 1, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(OtherSenderId, view.Sender.AwsSenderId);
        Assert.Equal(2, view.Sender.Revision);
        // The row is reused, so its creation instant is the one the first decision wrote.
        Assert.Equal(DecidedAt.AddDays(-7), view.Sender.CreatedAtUtc);
        Assert.Equal(DecidedAt, view.Sender.UpdatedAtUtc);

        var write = Assert.Single(store.Writes);
        Assert.Equal(SenderId, write.FromAwsSenderId);
        Assert.Equal(OtherSenderId, write.AwsSenderId);

        var entry = Assert.Single(audit.Entries);
        Assert.True(entry.HadSenderId);
        Assert.True(entry.HasSenderId);
    }

    /// <summary>
    /// Turning the mapping off keeps the id: "off" and "removed" are different decisions, and a
    /// business whose sender is paused has not lost which sender it was.
    /// </summary>
    [Fact]
    public async Task Turning_the_mapping_off_keeps_the_recorded_sender()
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Sender(SenderId, isEnabled: true, revision: 4));

        var view = await Subject(store).SetAsync(
            BusinessId, SenderId, isEnabled: false, expectedRevision: 4, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(SenderId, view.Sender.AwsSenderId);
        Assert.False(view.Sender.IsEnabled);
        Assert.Equal(5, view.Sender.Revision);
    }

    /// <summary>
    /// Remove is an update, never a delete: the row survives with its history, the id is cleared, the
    /// mapping is off, the revision moves on, and the audit records what was lost.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Remove_clears_the_sender_and_keeps_the_row(string? submitted)
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Sender(SenderId, isEnabled: true, revision: 2));
        var audit = new FakePlatformNayaxSqsSenderAudit();

        var view = await Subject(store, audit).SetAsync(
            BusinessId, submitted, isEnabled: false, expectedRevision: 2, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Null(view.Sender.AwsSenderId);
        Assert.False(view.Sender.IsEnabled);
        Assert.Equal(3, view.Sender.Revision);

        // The row is still there, with the revision the removal produced - nothing was deleted.
        var stored = store.SenderOf(BusinessId);
        Assert.NotNull(stored);
        Assert.Null(stored.AwsSenderId);
        Assert.Equal(3, stored.Revision);

        var entry = Assert.Single(audit.Entries);
        Assert.True(entry.HadSenderId);
        Assert.False(entry.HasSenderId);
        Assert.True(entry.WasEnabled);
        Assert.False(entry.IsEnabled);
    }

    /// <summary>
    /// And setting again after a removal reuses the row: the revision continues from where the
    /// removal left it rather than restarting at 1, which is what makes the trail readable.
    /// </summary>
    [Fact]
    public async Task Setting_again_after_a_removal_reuses_the_row_and_its_revision_series()
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Sender(senderId: null, isEnabled: false, revision: 3));

        var view = await Subject(store).SetAsync(
            BusinessId, SenderId, isEnabled: true, expectedRevision: 3, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(SenderId, view.Sender.AwsSenderId);
        Assert.True(view.Sender.IsEnabled);
        Assert.Equal(4, view.Sender.Revision);
    }

    /// <summary>
    /// A decision never writes the last-message instant: when a message arrived is the queue reader's
    /// fact (#1103) and nothing on this surface may change it.
    /// </summary>
    [Fact]
    public async Task A_decision_reports_the_last_message_instant_and_never_changes_it()
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Sender(SenderId, isEnabled: true, revision: 1));

        var view = await Subject(store).SetAsync(
            BusinessId, SenderId, isEnabled: false, expectedRevision: 1, CancellationToken.None);

        Assert.Equal(DecidedAt.AddDays(-1), view!.Sender.LastMessageReceivedAtUtc);
        Assert.Equal(DecidedAt.AddDays(-1), store.SenderOf(BusinessId)!.LastMessageReceivedAtUtc);
    }

    #endregion

    #region The expected-revision guard

    /// <summary>
    /// `null` presented against a business that already holds a row is a stale view, not a second
    /// create: the caller is looking at a page from before the row existed.
    /// </summary>
    [Fact]
    public async Task Creating_a_first_row_is_refused_when_one_already_exists()
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Sender(SenderId, isEnabled: true, revision: 1));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).SetAsync(
            BusinessId, OtherSenderId, isEnabled: true, expectedRevision: null, CancellationToken.None));

        Assert.Equal(NayaxSqsSenderPolicy.StaleRevisionMessage, refusal.Message);
        AssertNothingCommitted(store);
    }

    /// <summary>And a number presented against a business that holds no row is the same answer.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    public async Task Presenting_a_revision_is_refused_when_no_row_exists(int presented)
    {
        var store = FakePlatformNayaxSqsSenderStore.WithBusiness(BusinessId);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).SetAsync(
            BusinessId, SenderId, isEnabled: true, presented, CancellationToken.None));

        Assert.Equal(NayaxSqsSenderPolicy.StaleRevisionMessage, refusal.Message);
        AssertNothingCommitted(store);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_revision_that_is_not_the_stored_one_is_refused(int presented)
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Sender(SenderId, isEnabled: true, revision: 2));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).SetAsync(
            BusinessId, OtherSenderId, isEnabled: true, presented, CancellationToken.None));

        Assert.Equal(NayaxSqsSenderPolicy.StaleRevisionMessage, refusal.Message);
        AssertNothingCommitted(store);

        // And the stored row is exactly as it was.
        Assert.Equal(SenderId, store.SenderOf(BusinessId)!.AwsSenderId);
    }

    /// <summary>
    /// The comparison is made on the read taken <em>inside</em> the transaction, which is the whole
    /// point of taking the write lock first: a decision committed between the caller's read and this
    /// one is what the guard has to notice, and it does.
    /// </summary>
    [Fact]
    public async Task A_decision_committed_while_the_lock_was_awaited_refuses_this_one()
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Sender(SenderId, isEnabled: true, revision: 2));

        // Another administrator's committed write, landing after this caller read revision 2.
        store.BeforeInTransactionRead = () =>
            store.Replace(BusinessId, Sender(OtherSenderId, isEnabled: true, revision: 3));

        await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).SetAsync(
            BusinessId, SenderId, isEnabled: true, expectedRevision: 2, CancellationToken.None));

        AssertNothingCommitted(store);

        // Two reads: the one before the transaction, and the authoritative one inside it.
        Assert.Equal(2, store.Reads);
    }

    #endregion

    #region Refusals that write nothing

    [Theory]
    [InlineData("AIDASHORT")]
    [InlineData("AIDA-EXAMPLE-USER-123")]
    [InlineData("arn:aws:iam::123456789012:user/restockroo")]
    public async Task A_sender_id_that_is_not_letters_and_digits_is_refused_before_anything_is_read(
        string submitted)
    {
        var store = FakePlatformNayaxSqsSenderStore.WithBusiness(BusinessId);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => Subject(store).SetAsync(
            BusinessId, submitted, isEnabled: true, expectedRevision: null, CancellationToken.None));

        Assert.Equal(NayaxSqsSenderPolicy.InvalidSenderIdMessage, refusal.Message);

        // Nothing was read, locked or written: an invalid request must not reach the database at all.
        Assert.Equal(0, store.Reads);
        AssertNothingCommitted(store);
    }

    [Fact]
    public async Task Turning_the_mapping_on_with_no_sender_id_is_refused()
    {
        var store = FakePlatformNayaxSqsSenderStore.WithBusiness(BusinessId);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => Subject(store).SetAsync(
            BusinessId, awsSenderId: null, isEnabled: true, expectedRevision: null, CancellationToken.None));

        Assert.Equal(NayaxSqsSenderPolicy.EnabledNeedsSenderIdMessage, refusal.Message);
        Assert.Equal(0, store.Reads);
        AssertNothingCommitted(store);
    }

    /// <summary>
    /// The lifecycle gate, re-read inside the transaction: only an approved, trading business may
    /// have the answer to "whose sales are these?" changed.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    [InlineData((BusinessStatus)0)]
    public async Task A_business_that_is_not_active_is_refused(BusinessStatus status)
    {
        var store = FakePlatformNayaxSqsSenderStore.WithBusiness(BusinessId, status);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).SetAsync(
            BusinessId, SenderId, isEnabled: true, expectedRevision: null, CancellationToken.None));

        Assert.Equal(NayaxSqsSenderPolicy.BusinessNotActiveMessage, refusal.Message);
        AssertNothingCommitted(store);
    }

    /// <summary>
    /// A business deactivated while the write lock was awaited is refused too, because the gate is
    /// decided on the in-transaction read rather than on the one before it.
    /// </summary>
    [Fact]
    public async Task A_business_deactivated_while_the_lock_was_awaited_is_refused()
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Sender(SenderId, isEnabled: true, revision: 1));

        store.BeforeInTransactionRead = () => store.AndBusiness(BusinessId, BusinessStatus.Deactivated);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).SetAsync(
            BusinessId, OtherSenderId, isEnabled: true, expectedRevision: 1, CancellationToken.None));

        Assert.Equal(NayaxSqsSenderPolicy.BusinessNotActiveMessage, refusal.Message);
        AssertNothingCommitted(store);
    }

    /// <summary>
    /// A sender id another business already holds is the index's refusal, surfaced as this surface's
    /// documented conflict - and the sentence names no business, because which business holds an
    /// identity is not a fact this surface may publish.
    /// </summary>
    [Fact]
    public async Task A_sender_id_another_business_holds_is_refused_without_naming_it()
    {
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .TakenElsewhere(SenderId);
        var audit = new FakePlatformNayaxSqsSenderAudit();

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store, audit).SetAsync(
            BusinessId, SenderId, isEnabled: true, expectedRevision: null, CancellationToken.None));

        Assert.Equal(NayaxSqsSenderPolicy.DuplicateSenderIdMessage, refusal.Message);

        // The transaction was opened and then abandoned, so nothing committed - and nothing was
        // audited either, because no decision was made.
        Assert.False(Assert.Single(store.Transactions).Committed);
        Assert.True(Assert.Single(store.Transactions).Disposed);
        Assert.Empty(store.Writes);
        Assert.Empty(audit.Entries);
        Assert.Null(store.SenderOf(BusinessId));
    }

    /// <summary>
    /// A request whose actor cannot be named writes nothing: an audit trail that could not say who
    /// decided whose sales these are would be worse than none, so the actor is read before the
    /// business.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_identifiable_actor_writes_nothing()
    {
        var store = FakePlatformNayaxSqsSenderStore.WithBusiness(BusinessId);
        var subject = new ManageBusinessNayaxSqsSender(
            FakeAuthenticatedActorAccessor.NotAuthenticated(),
            store,
            new FakePlatformNayaxSqsSenderAudit(),
            new NayaxSqsReaderHealth(),
            new FakeClock(DecidedAt));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() => subject.SetAsync(
            BusinessId, SenderId, isEnabled: true, expectedRevision: null, CancellationToken.None));

        Assert.Equal(0, store.Reads);
        AssertNothingCommitted(store);
    }

    /// <summary>
    /// The two-business claim at this layer: a decision names one business, and the store is never
    /// asked about another - which is what the relational isolation test proves end to end.
    /// </summary>
    [Fact]
    public async Task A_decision_is_addressed_to_the_business_the_route_named_and_no_other()
    {
        const int otherBusinessId = 9;
        var store = FakePlatformNayaxSqsSenderStore
            .WithBusiness(BusinessId)
            .AndBusiness(otherBusinessId, BusinessStatus.Active)
            .Holding(otherBusinessId, Sender(OtherSenderId, isEnabled: true, revision: 5));

        await Subject(store).SetAsync(
            BusinessId, SenderId, isEnabled: true, expectedRevision: null, CancellationToken.None);

        Assert.Equal(BusinessId, Assert.Single(store.Writes).BusinessId);

        // The other business's row is untouched, revision included.
        var other = store.SenderOf(otherBusinessId);
        Assert.Equal(OtherSenderId, other!.AwsSenderId);
        Assert.Equal(5, other.Revision);
    }

    #endregion

    private static void AssertNothingCommitted(FakePlatformNayaxSqsSenderStore store)
    {
        Assert.Empty(store.Writes);
        Assert.All(store.Transactions, transaction => Assert.False(transaction.Committed));
    }

    private static ActorIdentity Administrator()
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, AdministratorObjectId, out var identity));
        return identity!;
    }

    private static PlatformNayaxSqsSenderState Sender(string? senderId, bool isEnabled, int revision) =>
        new(
            senderId,
            isEnabled,
            LastMessageReceivedAtUtc: DecidedAt.AddDays(-1),
            LastReconciliationSucceededAtUtc: DecidedAt.AddHours(-1),
            revision,
            CreatedAtUtc: DecidedAt.AddDays(-7),
            UpdatedAtUtc: DecidedAt.AddDays(-2));

    private static ManageBusinessNayaxSqsSender Subject(
        FakePlatformNayaxSqsSenderStore store,
        FakePlatformNayaxSqsSenderAudit? audit = null) =>
        new(
            FakeAuthenticatedActorAccessor.Identified(Administrator()),
            store,
            audit ?? new FakePlatformNayaxSqsSenderAudit(),
            new NayaxSqsReaderHealth(),
            new FakeClock(DecidedAt));
}
