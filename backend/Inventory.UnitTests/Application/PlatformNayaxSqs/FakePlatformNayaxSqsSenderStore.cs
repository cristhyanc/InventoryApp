using Inventory.Application.PlatformNayaxSqs;
using Inventory.Domain.Exceptions;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.PlatformNayaxSqs;

/// <summary>
/// In-memory fake of the Nayax SQS sender port (issue #1101), so the use case's frame - the two
/// reads, the lifecycle gate, the revision comparison, the write and the commit - is exercised
/// without EF Core or a database.
///
/// <para>It records what it was asked and in which order, because that order <em>is</em> part of the
/// contract: the authoritative read happens inside the transaction, the write happens after it, and
/// a refusal commits nothing. The relational behaviour underneath - the <c>BEGIN IMMEDIATE</c> lock,
/// the ownership lease, the global unique index and the sender and audit rows committing together -
/// is asserted against real SQLite in <c>EfPlatformNayaxSqsSenderStoreTests</c>.</para>
/// </summary>
public sealed class FakePlatformNayaxSqsSenderStore : IPlatformNayaxSqsSenderStore
{
    private readonly Dictionary<int, BusinessStatus> _businesses = [];
    private readonly Dictionary<int, PlatformNayaxSqsSenderState> _senders = [];
    private readonly HashSet<string> _takenElsewhere = new(StringComparer.Ordinal);

    /// <summary>Every write this store received, in order.</summary>
    public List<PlatformNayaxSqsSenderWrite> Writes { get; } = [];

    /// <summary>Every transaction it opened, so a commit can be told from a rollback.</summary>
    public List<FakeTransaction> Transactions { get; } = [];

    /// <summary>How many times the store was read, so "re-read inside the lock" is observable.</summary>
    public int Reads { get; private set; }

    /// <summary>
    /// What the next in-transaction read should find, replacing whatever the store holds - the state
    /// another administrator committed while this caller was deciding.
    /// </summary>
    public Action? BeforeInTransactionRead { get; set; }

    public static FakePlatformNayaxSqsSenderStore WithBusiness(
        int businessId,
        BusinessStatus status = BusinessStatus.Active)
    {
        var store = new FakePlatformNayaxSqsSenderStore();
        store._businesses[businessId] = status;
        return store;
    }

    public FakePlatformNayaxSqsSenderStore AndBusiness(int businessId, BusinessStatus status)
    {
        _businesses[businessId] = status;
        return this;
    }

    /// <summary>Puts a recorded sender on a business, as a previous decision would have.</summary>
    public FakePlatformNayaxSqsSenderStore Holding(int businessId, PlatformNayaxSqsSenderState sender)
    {
        _senders[businessId] = sender;
        return this;
    }

    /// <summary>
    /// Declares a sender id that some <em>other</em> business already holds, which the real adapter
    /// learns from the global unique index rather than from a read. The fake refuses it the same way:
    /// with the documented conflict, from inside <see cref="SaveAsync"/>, after the transaction was
    /// opened.
    /// </summary>
    public FakePlatformNayaxSqsSenderStore TakenElsewhere(string senderId)
    {
        _takenElsewhere.Add(senderId);
        return this;
    }

    /// <summary>The sender a business holds now, so a test can read what a write left behind.</summary>
    public PlatformNayaxSqsSenderState? SenderOf(int businessId) =>
        _senders.TryGetValue(businessId, out var sender) ? sender : null;

    public Task<PlatformNayaxSqsSenderTarget?> FindAsync(int businessId, CancellationToken cancellationToken)
    {
        Reads++;

        if (Transactions.Count > 0 && Reads > 1)
        {
            BeforeInTransactionRead?.Invoke();
            BeforeInTransactionRead = null;
        }

        return Task.FromResult(_businesses.TryGetValue(businessId, out var status)
            ? new PlatformNayaxSqsSenderTarget(businessId, status, SenderOf(businessId))
            : null);
    }

    public Task<IPlatformNayaxSqsSenderWriteTransaction> BeginWriteTransactionAsync(
        CancellationToken cancellationToken)
    {
        var transaction = new FakeTransaction();
        Transactions.Add(transaction);
        return Task.FromResult<IPlatformNayaxSqsSenderWriteTransaction>(transaction);
    }

    public Task SaveAsync(PlatformNayaxSqsSenderWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (write.AwsSenderId is { } senderId && _takenElsewhere.Contains(senderId))
        {
            throw new DomainConflictException(NayaxSqsSenderPolicy.DuplicateSenderIdMessage);
        }

        Writes.Add(write);

        var existing = SenderOf(write.BusinessId);
        _senders[write.BusinessId] = new PlatformNayaxSqsSenderState(
            write.AwsSenderId,
            write.IsEnabled,
            existing?.LastMessageReceivedAtUtc,
            write.Revision,
            existing?.CreatedAtUtc ?? write.OccurredAtUtc,
            write.OccurredAtUtc);

        return Task.CompletedTask;
    }

    /// <summary>Replaces the business's sender, as another administrator's committed write would.</summary>
    public void Replace(int businessId, PlatformNayaxSqsSenderState? sender)
    {
        if (sender is null)
        {
            _senders.Remove(businessId);
            return;
        }

        _senders[businessId] = sender;
    }

    public sealed class FakeTransaction : IPlatformNayaxSqsSenderWriteTransaction
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

/// <summary>Collects the Nayax SQS sender log entries a decision emitted (issue #1101).</summary>
public sealed class FakePlatformNayaxSqsSenderAudit : IPlatformNayaxSqsSenderAudit
{
    public List<PlatformNayaxSqsSenderAuditEntry> Entries { get; } = [];

    public void Record(PlatformNayaxSqsSenderAuditEntry entry) => Entries.Add(entry);
}
