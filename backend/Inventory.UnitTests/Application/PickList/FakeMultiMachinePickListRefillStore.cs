#nullable enable

using Inventory.Application.PickList;

namespace InventoryApi.Tests.Application.PickList;

/// <summary>
/// In-memory fake of the multi-machine batch persistence port (issue #897), so the use case's
/// orchestration - the order of the claim, the reads, the rules and the write, and whether the
/// transaction was committed - is tested without EF Core or SQLite.
///
/// <para>Everything the real adapter decides against the relational schema is proved elsewhere:
/// <c>InventoryApi.Tests.Adapters.Persistence.EfMultiMachinePickListRefillStoreTests</c> for the
/// movements, the per-allocation machine and the tenant boundary, and
/// <c>MultiMachinePickListRefillConcurrencyTests</c> for the <c>BEGIN IMMEDIATE</c> write lock, which
/// no in-memory double can stand in for. What this fake does model faithfully is what the use case
/// depends on: a key that has already recorded a refill is found - including one that recorded a
/// refill this contract cannot answer with - and a write that is not committed is not visible
/// afterwards.</para>
/// </summary>
public sealed class FakeMultiMachinePickListRefillStore : IMultiMachinePickListRefillStore
{
    private readonly Dictionary<string, MultiMachinePickListRefillSnapshot> _committed =
        new(StringComparer.Ordinal);

    private MultiMachinePickListRefillSnapshot? _pending;

    /// <summary>How many times a write transaction was opened.</summary>
    public int TransactionsOpened { get; private set; }

    /// <summary>How many of them were committed.</summary>
    public int TransactionsCommitted { get; private set; }

    /// <summary>Whether a transaction was open when the key was last looked up.</summary>
    public bool KeyLookedUpInsideTransaction { get; private set; }

    /// <summary>Whether a transaction was open when <see cref="ApplyAsync"/> was last called.</summary>
    public bool AppliedInsideTransaction { get; private set; }

    public int ApplyCalls { get; private set; }

    public MultiMachinePickListRefillClaim? LastClaim { get; private set; }

    /// <summary>When set, <see cref="ApplyAsync"/> throws it - the index refusing a duplicate key.</summary>
    public Exception? ApplyThrows { get; set; }

    /// <summary>What <see cref="IsDuplicateIdempotencyKey"/> answers for <see cref="ApplyThrows"/>.</summary>
    public bool ApplyFailureIsADuplicateKey { get; set; }

    private bool _inTransaction;

    /// <summary>Seeds a batch as already recorded and committed under its key.</summary>
    public void Seed(string idempotencyKey, string fingerprint, MultiMachinePickListRefillRecord record) =>
        _committed[idempotencyKey] = new MultiMachinePickListRefillSnapshot(fingerprint, record);

    /// <summary>
    /// Seeds a key whose stored refill this contract cannot answer with - what the real adapter
    /// reports for a key the single-machine endpoint spent.
    /// </summary>
    public void SeedUnanswerable(string idempotencyKey, string fingerprint) =>
        _committed[idempotencyKey] = new MultiMachinePickListRefillSnapshot(fingerprint, null);

    /// <summary>Every batch that survived a commit, by key.</summary>
    public IReadOnlyDictionary<string, MultiMachinePickListRefillSnapshot> Committed => _committed;

    public Task<IPickListRefillWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken)
    {
        TransactionsOpened++;
        _inTransaction = true;
        return Task.FromResult<IPickListRefillWriteTransaction>(new Transaction(this));
    }

    public Task<MultiMachinePickListRefillSnapshot?> FindByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken cancellationToken)
    {
        KeyLookedUpInsideTransaction = _inTransaction;
        return Task.FromResult(_committed.GetValueOrDefault(idempotencyKey));
    }

    public Task<MultiMachinePickListRefillRecord> ApplyAsync(
        MultiMachinePickListRefillClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        ApplyCalls++;
        AppliedInsideTransaction = _inTransaction;
        LastClaim = claim;

        if (ApplyThrows is { } failure) throw failure;

        var record = new MultiMachinePickListRefillRecord(
            claim.IdempotencyKey,
            claim.Notes,
            claim.RecordedAtUtc,
            [.. claim.Allocations.Select((allocation, index) =>
                new MultiMachinePickListRefillRecordedAllocation(
                    allocation.MachineId,
                    allocation.ProductId,
                    allocation.QuantityTaken,
                    StockAdjustmentId: index + 1))]);

        // Staged, not committed: only CommitAsync makes it findable, which is what lets a test assert
        // that a refused batch left nothing behind.
        _pending = new MultiMachinePickListRefillSnapshot(claim.RequestFingerprint, record);
        return Task.FromResult(record);
    }

    public bool IsDuplicateIdempotencyKey(Exception exception) =>
        ApplyFailureIsADuplicateKey && ReferenceEquals(exception, ApplyThrows);

    private sealed class Transaction(FakeMultiMachinePickListRefillStore store) : IPickListRefillWriteTransaction
    {
        /// <summary>The commit is the only thing that makes a staged batch findable afterwards.</summary>
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (store._pending is { Record: { } record } pending)
            {
                store._committed[record.IdempotencyKey] = pending;
            }

            store.TransactionsCommitted++;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Clears whatever was staged. An uncommitted batch is thereby discarded - exactly as
        /// disposing the provider transaction without committing discards it in the real adapter - and
        /// a committed one has already been moved across.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            store._pending = null;
            store._inTransaction = false;
            return ValueTask.CompletedTask;
        }
    }
}
