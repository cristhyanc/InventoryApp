namespace Inventory.Application.Reporting.Transactions;

/// <summary>
/// Narrow Application-owned port for the report facts the transaction sales report use case needs.
/// Not a generic repository: it returns one purpose-built fact bundle for one resolved date range
/// and optional machine filter. The bundle's per-transaction rows are an asynchronous stream so an
/// implementation can consume its underlying query one row at a time instead of completing it before
/// returning.
/// </summary>
public interface ITransactionSalesReportFactsProvider
{
    Task<TransactionSalesReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken);
}
