using Inventory.Application.PlatformPlans;
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
    private readonly Dictionary<int, string> _businesses = [];
    private readonly Dictionary<int, List<PlatformPlanGrantState>> _grants = [];

    /// <summary>Every write this store received, in order.</summary>
    public List<PlatformPlanGrantWrite> Writes { get; } = [];

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

    /// <summary>Puts a complimentary grant on a business, as a previous decision would have.</summary>
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

        Writes.Add(write);
        _grants[write.BusinessId] =
        [
            new PlatformPlanGrantState(
                write.Plan,
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
