namespace Inventory.Application.Reporting.Reconciliation;

/// <summary>
/// Narrow Application-owned port for the report facts the reconciliation use case needs. Not a
/// generic repository: it returns one purpose-built fact bundle for one resolved date range and
/// optional machine filter.
/// </summary>
public interface IReconciliationReportFactsProvider
{
    Task<ReconciliationReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken);
}
