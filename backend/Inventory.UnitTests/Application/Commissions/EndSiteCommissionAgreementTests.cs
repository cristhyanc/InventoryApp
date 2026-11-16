using Inventory.Application.Commissions;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Commissions;

/// <summary>
/// Ending a site commission agreement on a date (issue #1254).
///
/// <para>The behaviour these tests pin is the one the issue asks for: the agreement is re-read
/// <em>inside</em> the write-locked transaction the end date is written in, the end date may only
/// narrow the agreement, a refusal commits nothing and audits nothing, and the committed change is
/// audited once with the old and the new end date.</para>
/// </summary>
public class EndSiteCommissionAgreementTests
{
    private const int BusinessId = 7;
    private const int AgreementId = 42;
    private const long SiteId = 50;

    private static readonly DateTime Start = new(2026, 1, 1);
    private static readonly DateTime Now = new(2026, 11, 16, 3, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task An_open_ended_agreement_gains_the_end_date_and_the_change_is_committed()
    {
        var store = new FakeSiteCommissionStore(Agreement(null));
        var audit = new FakeSiteCommissionAgreementAudit();

        var ended = await UseCase(store, audit).Handle(
            new EndSiteCommissionAgreementInput(AgreementId, new DateTime(2026, 6, 30)),
            CancellationToken.None);

        Assert.NotNull(ended);
        Assert.Equal(new DateTime(2026, 6, 30), ended!.EffectiveTo);
        Assert.Equal(new DateTime(2026, 6, 30), store.WrittenEndDate);
        Assert.Equal(1, store.Commits);
    }

    /// <summary>The submitted value is a date: a time of day on it never reaches the row.</summary>
    [Fact]
    public async Task The_submitted_end_date_is_stored_as_a_date()
    {
        var store = new FakeSiteCommissionStore(Agreement(null));

        await UseCase(store, new FakeSiteCommissionAgreementAudit()).Handle(
            new EndSiteCommissionAgreementInput(AgreementId, new DateTime(2026, 6, 30, 13, 45, 0)),
            CancellationToken.None);

        Assert.Equal(new DateTime(2026, 6, 30), store.WrittenEndDate);
    }

    [Fact]
    public async Task An_agreement_that_already_ends_can_be_ended_earlier()
    {
        var store = new FakeSiteCommissionStore(Agreement(new DateTime(2026, 6, 30)));

        var ended = await UseCase(store, new FakeSiteCommissionAgreementAudit()).Handle(
            new EndSiteCommissionAgreementInput(AgreementId, new DateTime(2026, 3, 31)),
            CancellationToken.None);

        Assert.Equal(new DateTime(2026, 3, 31), ended!.EffectiveTo);
    }

    [Fact]
    public async Task An_agreement_that_already_ends_cannot_be_extended_and_nothing_is_written()
    {
        var store = new FakeSiteCommissionStore(Agreement(new DateTime(2026, 6, 30)));
        var audit = new FakeSiteCommissionAgreementAudit();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            UseCase(store, audit).Handle(
                new EndSiteCommissionAgreementInput(AgreementId, new DateTime(2026, 7, 1)),
                CancellationToken.None));

        Assert.Equal(SiteCommissionAgreementEndPolicy.LaterThanExistingEndMessage, refusal.Message);
        Assert.Null(store.WrittenEndDate);
        Assert.Equal(0, store.Commits);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task An_end_date_before_the_start_date_is_refused_and_nothing_is_written()
    {
        var store = new FakeSiteCommissionStore(Agreement(null));
        var audit = new FakeSiteCommissionAgreementAudit();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            UseCase(store, audit).Handle(
                new EndSiteCommissionAgreementInput(AgreementId, Start.AddDays(-1)),
                CancellationToken.None));

        Assert.Equal(SiteCommissionAgreementEndPolicy.BeforeStartMessage, refusal.Message);
        Assert.Null(store.WrittenEndDate);
        Assert.Equal(0, store.Commits);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// An agreement the caller's own business does not hold - another business's key included - is
    /// not found rather than refused, and nothing is written or audited.
    /// </summary>
    [Fact]
    public async Task An_agreement_the_business_does_not_hold_is_not_found()
    {
        var store = new FakeSiteCommissionStore(null);
        var audit = new FakeSiteCommissionAgreementAudit();

        var ended = await UseCase(store, audit).Handle(
            new EndSiteCommissionAgreementInput(AgreementId, new DateTime(2026, 6, 30)),
            CancellationToken.None);

        Assert.Null(ended);
        Assert.Equal(0, store.Commits);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// The authoritative read, the comparison it decides and the write are one operation: the read
    /// happens after the write lock is taken, and the commit after the write.
    /// </summary>
    [Fact]
    public async Task The_read_and_the_write_both_happen_inside_the_write_locked_transaction()
    {
        var store = new FakeSiteCommissionStore(Agreement(null));

        await UseCase(store, new FakeSiteCommissionAgreementAudit()).Handle(
            new EndSiteCommissionAgreementInput(AgreementId, new DateTime(2026, 6, 30)),
            CancellationToken.None);

        Assert.Equal(["begin", "find", "write", "commit"], store.Trace);
    }

    /// <summary>
    /// The committed change is audited once, with who made it, when, and both end dates - and the
    /// instant it records is the one the row was stamped with, sampled inside the lock.
    /// </summary>
    [Fact]
    public async Task The_committed_change_is_audited_with_the_old_and_the_new_end_date()
    {
        var store = new FakeSiteCommissionStore(Agreement(new DateTime(2026, 12, 31)));
        var audit = new FakeSiteCommissionAgreementAudit();

        await UseCase(store, audit).Handle(
            new EndSiteCommissionAgreementInput(AgreementId, new DateTime(2026, 6, 30)),
            CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(Actor.DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(Actor.ObjectId, entry.ActorObjectId);
        Assert.Equal(BusinessId, entry.BusinessId);
        Assert.Equal(AgreementId, entry.AgreementId);
        Assert.Equal(SiteId, entry.SiteId);
        Assert.Equal(new DateTime(2026, 12, 31), entry.PreviousEndDate);
        Assert.Equal(new DateTime(2026, 6, 30), entry.NewEndDate);
        Assert.Equal(Now, entry.OccurredAtUtc);
        Assert.Equal(Now, store.WrittenOccurredAtUtc);
    }

    /// <summary>An agreement that was open-ended is audited as having had no previous end date.</summary>
    [Fact]
    public async Task An_open_ended_agreement_is_audited_with_no_previous_end_date()
    {
        var audit = new FakeSiteCommissionAgreementAudit();

        await UseCase(new FakeSiteCommissionStore(Agreement(null)), audit).Handle(
            new EndSiteCommissionAgreementInput(AgreementId, new DateTime(2026, 6, 30)),
            CancellationToken.None);

        Assert.Null(Assert.Single(audit.Entries).PreviousEndDate);
    }

    /// <summary>
    /// A request with no identified actor writes nothing: an end date nobody can be named for is not
    /// auditable, and this change is only allowed because it is audited.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_identified_actor_writes_nothing()
    {
        var store = new FakeSiteCommissionStore(Agreement(null));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            new EndSiteCommissionAgreement(
                    store,
                    new FakeCurrentBusinessProvider(BusinessId),
                    FakeAuthenticatedActorAccessor.NotAuthenticated(),
                    new FakeSiteCommissionAgreementAudit(),
                    new FakeClock(Now))
                .Handle(
                    new EndSiteCommissionAgreementInput(AgreementId, new DateTime(2026, 6, 30)),
                    CancellationToken.None));

        Assert.Empty(store.Trace);
    }

    private static ActorIdentity Actor
    {
        get
        {
            ActorIdentity.TryCreate(
                "11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222",
                out var actor);
            return actor!;
        }
    }

    private static CommissionAgreement Agreement(DateTime? effectiveTo) =>
        new(AgreementId, SiteId, Start, effectiveTo, 0.1m, CommissionFrequency.Monthly,
            CommissionBasis.GrossSales, 14, Start, Start);

    private static EndSiteCommissionAgreement UseCase(
        FakeSiteCommissionStore store,
        FakeSiteCommissionAgreementAudit audit) =>
        new(
            store,
            new FakeCurrentBusinessProvider(BusinessId),
            FakeAuthenticatedActorAccessor.Identified(Actor),
            audit,
            new FakeClock(Now));

    /// <summary>
    /// Records the order the use case drives the port in, which is what the concurrency criterion is
    /// about: the agreement an end date is measured against must be read after the write lock is
    /// taken, and the commit must come after the write.
    /// </summary>
    private sealed class FakeSiteCommissionStore : ISiteCommissionStore
    {
        private readonly CommissionAgreement? _agreement;

        public FakeSiteCommissionStore(CommissionAgreement? agreement) => _agreement = agreement;

        public List<string> Trace { get; } = [];

        public int Commits { get; private set; }

        public DateTime? WrittenEndDate { get; private set; }

        public DateTime? WrittenOccurredAtUtc { get; private set; }

        public Task<ISiteCommissionWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken)
        {
            Trace.Add("begin");
            return Task.FromResult<ISiteCommissionWriteTransaction>(new Transaction(this));
        }

        public Task<CommissionAgreement?> FindAgreementAsync(int agreementId, CancellationToken cancellationToken)
        {
            Trace.Add("find");
            return Task.FromResult(agreementId == AgreementId ? _agreement : null);
        }

        public Task<CommissionAgreement> SetAgreementEndDateAsync(
            int agreementId,
            DateTime endDate,
            DateTime occurredAtUtc,
            CancellationToken cancellationToken)
        {
            Trace.Add("write");
            WrittenEndDate = endDate;
            WrittenOccurredAtUtc = occurredAtUtc;
            return Task.FromResult(_agreement! with { EffectiveTo = endDate, UpdatedAt = occurredAtUtc });
        }

        public Task<IReadOnlyList<SiteCommissionSaleFact>> GetCompletedSalesAsync(
            IReadOnlyCollection<long> machineIds,
            DateTime from,
            DateTime endExclusive,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiteCommissionSaleFact>>([]);

        public Task<IReadOnlyList<CommissionAgreement>> GetAgreementsAsync(
            long? siteId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CommissionAgreement>>(
                _agreement is null ? [] : [_agreement]);

        public Task<bool> HasOverlappingAgreementAsync(
            long siteId,
            DateTime effectiveFrom,
            DateTime? effectiveTo,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<CommissionAgreement> AddAgreementAsync(
            CommissionAgreement agreement,
            CancellationToken cancellationToken) =>
            Task.FromResult(agreement);

        private sealed class Transaction : ISiteCommissionWriteTransaction
        {
            private readonly FakeSiteCommissionStore _store;

            public Transaction(FakeSiteCommissionStore store) => _store = store;

            public Task CommitAsync(CancellationToken cancellationToken)
            {
                _store.Trace.Add("commit");
                _store.Commits++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSiteCommissionAgreementAudit : ISiteCommissionAgreementAudit
    {
        public List<SiteCommissionAgreementEndedAuditEntry> Entries { get; } = [];

        public void Record(SiteCommissionAgreementEndedAuditEntry entry) => Entries.Add(entry);
    }
}
