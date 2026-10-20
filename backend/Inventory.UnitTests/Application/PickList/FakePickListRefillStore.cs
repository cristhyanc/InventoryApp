#nullable enable

using Inventory.Application.PickList;

namespace InventoryApi.Tests.Application.PickList;

/// <summary>
/// In-memory fake of the batch refill persistence port (issue #743), so the use case's
/// orchestration - the order of the claim, the reads, the rules and the write, and whether the
/// transaction was committed - is tested without EF Core or SQLite.
///
/// <para>Everything the real adapter decides against the relational schema is proved elsewhere:
/// <c>InventoryApi.Tests.Adapters.Persistence.EfPickListRefillStoreTests</c> for the movements and
/// the tenant boundary, and <c>PickListRefillConcurrencyTests</c> for the <c>BEGIN IMMEDIATE</c>
/// write lock, which no in-memory double can stand in for. What this fake does model faithfully is
/// the one thing the use case depends on: a key that has already recorded a refill is found, and a
/// write that is not committed is not visible afterwards.</para>
/// </summary>
public sealed class FakePickListRefillStore : IPickListRefillStore
{
    private readonly Dictionary<string, PickListRefillSnapshot> _committed = new(StringComparer.Ordinal);

    private PickListRefillSnapshot? _pending;

    /// <summary>How many times a write transaction was opened.</summary>
    public int TransactionsOpened { get; private set; }

    /// <summary>How many of them were committed.</summary>
    public int TransactionsCommitted { get; private set; }

    /// <summary>Whether a transaction was open when the key was last looked up.</summary>
    public bool KeyLookedUpInsideTransaction { get; private set; }

    /// <summary>Whether a transaction was open when <see cref="ApplyAsync"/> was last called.</summary>
    public bool AppliedInsideTransaction { get; private set; }

    public int ApplyCalls { get; private set; }

    public PickListRefillClaim? LastClaim { get; private set; }

    /// <summary>When set, <see cref="ApplyAsync"/> throws it - the index refusing a duplicate key.</summary>
    public Exception? ApplyThrows { get; set; }

    /// <summary>What <see cref="IsDuplicateIdempotencyKey"/> answers for <see cref="ApplyThrows"/>.</summary>
    public bool ApplyFailureIsADuplicateKey { get; set; }

    private bool _inTransaction;

    /// <summary>Seeds a refill as already recorded and committed under its key.</summary>
    public void Seed(string idempotencyKey, string fingerprint, PickListRefillRecord record) =>
        _committed[idempotencyKey] = new PickListRefillSnapshot(fingerprint, record);

    /// <summary>Every refill that survived a commit, by key.</summary>
    public IReadOnlyDictionary<string, PickListRefillSnapshot> Committed => _committed;

    public Task<IPickListRefillWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken)
    {
        TransactionsOpened++;
        _inTransaction = true;
        return Task.FromResult<IPickListRefillWriteTransaction>(new Transaction(this));
    }

    public Task<PickListRefillSnapshot?> FindByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken cancellationToken)
    {
        KeyLookedUpInsideTransaction = _inTransaction;
        return Task.FromResult(_committed.GetValueOrDefault(idempotencyKey));
    }

    public Task<PickListRefillRecord> ApplyAsync(PickListRefillClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        ApplyCalls++;
        AppliedInsideTransaction = _inTransaction;
        LastClaim = claim;

        if (ApplyThrows is { } failure) throw failure;

        var record = new PickListRefillRecord(
            claim.IdempotencyKey,
            claim.MachineId,
            claim.Notes,
            claim.RecordedAtUtc,
            [.. claim.Rows.Select((row, index) => new PickListRefillRecordedRow(
                row.ProductId, row.QuantityTaken, StockAdjustmentId: index + 1))]);

        // Staged, not committed: only CommitAsync makes it findable, which is what lets a test
        // assert that a refused refill left nothing behind.
        _pending = new PickListRefillSnapshot(claim.RequestFingerprint, record);
        return Task.FromResult(record);
    }

    public bool IsDuplicateIdempotencyKey(Exception exception) =>
        ApplyFailureIsADuplicateKey && ReferenceEquals(exception, ApplyThrows);

    private sealed class Transaction(FakePickListRefillStore store) : IPickListRefillWriteTransaction
    {
        /// <summary>The commit is the only thing that makes a staged refill findable afterwards.</summary>
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (store._pending is { } pending)
            {
                store._committed[pending.Record.IdempotencyKey] = pending;
            }

            store.TransactionsCommitted++;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Clears whatever was staged. An uncommitted refill is thereby discarded - exactly as
        /// disposing the provider transaction without committing discards it in the real adapter -
        /// and a committed one has already been moved across.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            store._pending = null;
            store._inTransaction = false;
            return ValueTask.CompletedTask;
        }
    }
}
