namespace Inventory.Application.Reporting.Transactions;

/// <summary>
/// Narrow Application-owned port for the report facts the transaction sales report use case needs.
/// Not a generic repository: it returns one purpose-built fact bundle for one resolved date range
/// and optional machine filter.
/// </summary>
public interface ITransactionSalesReportFactsProvider
{
    Task<TransactionSalesReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken);
}
