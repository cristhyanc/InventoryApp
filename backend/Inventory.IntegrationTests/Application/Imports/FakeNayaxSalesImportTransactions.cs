using Inventory.Application.Imports;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// An <see cref="INayaxSalesImportTransactions"/> that begins nothing, for the cases whose store is
/// a mock and whose subject is therefore the use case's own counting and ordering rather than any
/// database (issue #1300).
///
/// <para>Every case that has a real store uses the real
/// <c>EfNayaxSalesImportTransactions</c> instead, so that what is exercised is the transaction
/// production opens - which on a relational database is <c>BEGIN IMMEDIATE</c> and on the EF
/// InMemory provider is already nothing.</para>
/// </summary>
internal sealed class FakeNayaxSalesImportTransactions : INayaxSalesImportTransactions
{
    /// <summary>How many transactions the use case began; one per import that reached its rows.</summary>
    public int Begun { get; private set; }

    /// <summary>How many of them it committed.</summary>
    public int Committed { get; private set; }

    public Task<INayaxSalesImportTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        Begun++;
        return Task.FromResult<INayaxSalesImportTransaction>(new Transaction(this));
    }

    private sealed class Transaction : INayaxSalesImportTransaction
    {
        private readonly FakeNayaxSalesImportTransactions _owner;

        public Transaction(FakeNayaxSalesImportTransactions owner) => _owner = owner;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            _owner.Committed++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
