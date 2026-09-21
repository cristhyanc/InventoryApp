namespace Inventory.Application.Reporting.Dashboard;

/// <summary>
/// Narrow Application-owned port for the report facts unique to the dashboard summary. Not a
/// generic repository: it returns one purpose-built fact bundle for one resolved date range and
/// optional machine filter.
/// </summary>
public interface IDashboardReportFactsProvider
{
    Task<DashboardReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken);
}
