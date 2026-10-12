using Inventory.Application.Nayax;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// A save transaction that records what the use case did with it (issue #537), so a use-case test
/// can state that a refused save never committed.
///
/// It deliberately does <strong>not</strong> simulate a rollback. Undoing writes is the database's
/// job, and a fake that pretended to do it would let a use case that forgot to commit still look
/// correct. That a refused save leaves the draft in place, and that a failed credential write leaves
/// no claimed draft behind, are proved relationally in
/// <c>Adapters/Persistence/EfNayaxConnectionSaveTransactionTests</c> against real SQLite.
/// </summary>
public sealed class FakeNayaxConnectionSaveTransactions : INayaxConnectionSaveTransactions
{
    /// <summary>How many transactions were begun.</summary>
    public int Begun { get; private set; }

    /// <summary>How many were committed.</summary>
    public int Committed { get; private set; }

    /// <summary>How many were disposed without a commit, which is a rollback in production.</summary>
    public int RolledBack { get; private set; }

    public Task<INayaxConnectionSaveTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        Begun++;
        return Task.FromResult<INayaxConnectionSaveTransaction>(new Transaction(this));
    }

    private sealed class Transaction : INayaxConnectionSaveTransaction
    {
        private readonly FakeNayaxConnectionSaveTransactions _owner;
        private bool _committed;

        public Transaction(FakeNayaxConnectionSaveTransactions owner)
        {
            _owner = owner;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            _committed = true;
            _owner.Committed++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                _owner.RolledBack++;
            }

            return ValueTask.CompletedTask;
        }
    }
}
