using Inventory.Application.PlatformPlans;
using Inventory.Application.Subscriptions;
using Inventory.Domain.Subscriptions;

namespace InventoryApi.Tests.Application.PlatformPlans;

/// <summary>
/// In-memory fake of the manual-grant port (issue #982), so the use case's frame - the two reads,
/// the revision comparison, the write and the commit - is exercised without EF Core or a database.
///
/// <para>It records what it was asked and in which order, because that order <em>is</em> part of the
/// contract: the authoritative read happens inside the transaction, the write happens after it, and
/// a refusal commits nothing. The relational behaviour underneath - the <c>BEGIN IMMEDIATE</c> lock,
/// the ownership lease and the grant and audit rows committing together - is asserted against real
/// SQLite in <c>EfPlatformPlanGrantStoreTests</c>.</para>
/// </summary>
public sealed class FakePlatformPlanGrantStore : IPlatformPlanGrantStore
{
    /// <summary>How a write announces itself in a surrounding order of operations.</summary>
    public const string Save = "save-plan-grant";

    private readonly Dictionary<int, string> _businesses = [];
    private readonly Dictionary<int, List<PlatformPlanGrantState>> _grants = [];

    /// <summary>Every write this store received, in order.</summary>
    public List<PlatformPlanGrantWrite> Writes { get; } = [];

    /// <summary>
    /// Where a write is announced, so a test can see it in the overall order - which is how "the
    /// beta grant was written inside the approval's transaction, before the commit" is asserted
    /// rather than assumed (issue #1258).
    /// </summary>
    public Action<string>? Recorder { get; set; }

    /// <summary>
    /// What a write throws instead of succeeding, or <see langword="null"/> when it succeeds. The
    /// call is still recorded: a test needs to see that the caller tried before it failed.
    /// </summary>
    public Exception? FailWith { get; set; }

    /// <summary>Every transaction it opened, so a commit can be told from a rollback.</summary>
    public List<FakeTransaction> Transactions { get; } = [];

    /// <summary>How many times the store was read, so "re-read inside the lock" is observable.</summary>
    public int Reads { get; private set; }

    /// <summary>
    /// What the next in-transaction read should find, replacing whatever the store holds - the state
    /// another administrator committed while this caller was deciding.
    /// </summary>
    public Action? BeforeInTransactionRead { get; set; }

    public static FakePlatformPlanGrantStore WithBusiness(int businessId, string name = "Vending Co")
    {
        var store = new FakePlatformPlanGrantStore();
        store._businesses[businessId] = name;
        return store;
    }

    /// <summary>
    /// Puts a grant this surface administers on a business, as a previous decision - or an approval's
    /// beta grant (issue #1258) - would have.
    /// </summary>
    public FakePlatformPlanGrantStore Holding(int businessId, PlatformPlanGrantState grant)
    {
        if (!_grants.TryGetValue(businessId, out var grants))
        {
            grants = [];
            _grants[businessId] = grants;
        }

        grants.Add(grant);
        return this;
    }

    /// <summary>The grants a business holds now, so a test can read what a write left behind.</summary>
    public IReadOnlyList<PlatformPlanGrantState> GrantsOf(int businessId) =>
        _grants.TryGetValue(businessId, out var grants) ? grants : [];

    public Task<PlatformPlanGrantTarget?> FindAsync(int businessId, CancellationToken cancellationToken)
    {
        Reads++;

        if (Transactions.Count > 0 && Reads > 1)
        {
            BeforeInTransactionRead?.Invoke();
            BeforeInTransactionRead = null;
        }

        return Task.FromResult(_businesses.TryGetValue(businessId, out var name)
            ? new PlatformPlanGrantTarget(businessId, name, GrantsOf(businessId))
            : null);
    }

    public Task<IPlatformPlanGrantWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = new FakeTransaction();
        Transactions.Add(transaction);
        return Task.FromResult<IPlatformPlanGrantWriteTransaction>(transaction);
    }

    public Task SaveAsync(PlatformPlanGrantWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        Recorder?.Invoke(Save);
        Writes.Add(write);

        if (FailWith is not null)
        {
            return Task.FromException(FailWith);
        }

        _grants[write.BusinessId] =
        [
            new PlatformPlanGrantState(
                write.Plan,
                // The real adapter sets the source only when it creates the row, so a grant that
                // already existed keeps its own (issue #1258).
                GrantsOf(write.BusinessId) is [var existing, ..] ? existing.Source : write.Source,
                new EntitlementPeriod(write.StartsAtUtc, write.EndsAtUtc),
                write.GrantReason,
                write.Revision,
                write.OccurredAtUtc),
        ];

        return Task.CompletedTask;
    }

    /// <summary>Replaces the business's grants, as another administrator's committed write would.</summary>
    public void Replace(int businessId, params PlatformPlanGrantState[] grants) =>
        _grants[businessId] = [.. grants];

    public sealed class FakeTransaction : IPlatformPlanGrantWriteTransaction
    {
        public bool Committed { get; private set; }

        public bool Disposed { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Collects the manual-grant log entries a decision emitted (issue #982).</summary>
public sealed class FakePlatformPlanGrantAudit : IPlatformPlanGrantAudit
{
    public List<PlatformPlanGrantAuditEntry> Entries { get; } = [];

    public void Record(PlatformPlanGrantAuditEntry entry) => Entries.Add(entry);
}

/// <summary>
/// The <c>Subscriptions:BetaProGrantOnApproval</c> setting as a test decides it (issue #1258). The
/// real flag reads configuration once at startup, which is the composition root's business; what a
/// test needs is the answer.
/// </summary>
public sealed class FakeBetaProGrantOnApprovalFlag : IBetaProGrantOnApprovalFlag
{
    private FakeBetaProGrantOnApprovalFlag(bool isEnabled) => IsEnabled = isEnabled;

    public bool IsEnabled { get; }

    public static FakeBetaProGrantOnApprovalFlag On() => new(isEnabled: true);

    /// <summary>The shipped state: the setting absent, blank, unparseable or explicitly false.</summary>
    public static FakeBetaProGrantOnApprovalFlag Off() => new(isEnabled: false);
}
