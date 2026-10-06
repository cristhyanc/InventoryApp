#nullable enable

using Inventory.Application.Gst;
using Inventory.Domain.Gst;

namespace InventoryApi.Tests.Application.Gst;

/// <summary>
/// In-memory fake of the historical GST classification persistence port, so the Application use-case
/// tests exercise the Preview/Apply orchestration - the read order, the stale-preview comparison,
/// the transaction and the write - without EF Core or SQLite.
///
/// It records what the use cases did to it, which is how "the preview writes nothing" and "a
/// refused apply writes nothing" are asserted rather than assumed: a write that never happened
/// leaves <see cref="Applied"/> null, and a rolled-back one leaves
/// <see cref="Committed"/> false.
///
/// <see cref="ChangeHistoryBeforeRead"/> is the concurrency hook. It runs inside
/// <see cref="LoadPurchasesAsync"/>, which is where a real competing write would land: after the
/// preview was taken and before (or inside) the apply's own authoritative read.
/// </summary>
public sealed class FakeHistoricalGstClassificationStore : IHistoricalGstClassificationStore
{
    private List<HistoricalGstPurchase> _purchases;

    public FakeHistoricalGstClassificationStore(params HistoricalGstPurchase[] purchases) =>
        _purchases = [.. purchases];

    /// <summary>Runs once at the start of the next load, simulating a competing change.</summary>
    public Action<FakeHistoricalGstClassificationStore>? ChangeHistoryBeforeRead { get; set; }

    public int Loads { get; private set; }

    public int TransactionsBegun { get; private set; }

    /// <summary>The changes the apply persisted, or <c>null</c> when it never wrote.</summary>
    public IReadOnlyList<HistoricalGstClassificationChange>? Applied { get; private set; }

    public bool Committed { get; private set; }

    public bool Disposed { get; private set; }

    public void Replace(params HistoricalGstPurchase[] purchases) => _purchases = [.. purchases];

    public Task<IHistoricalGstClassificationTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        TransactionsBegun++;
        return Task.FromResult<IHistoricalGstClassificationTransaction>(new Transaction(this));
    }

    public Task<IReadOnlyList<HistoricalGstPurchase>> LoadPurchasesAsync(CancellationToken cancellationToken)
    {
        var change = ChangeHistoryBeforeRead;
        ChangeHistoryBeforeRead = null;
        change?.Invoke(this);

        Loads++;
        return Task.FromResult<IReadOnlyList<HistoricalGstPurchase>>([.. _purchases]);
    }

    public Task<int> ApplyAsync(
        IReadOnlyCollection<HistoricalGstClassificationChange> changes,
        CancellationToken cancellationToken)
    {
        Applied = [.. changes];
        return Task.FromResult(changes.Count);
    }

    private sealed class Transaction(FakeHistoricalGstClassificationStore store)
        : IHistoricalGstClassificationTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            store.Committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            store.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
