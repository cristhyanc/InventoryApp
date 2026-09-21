namespace Inventory.Application.Reporting.Daily;

/// <summary>
/// Narrow Application-owned port for the report facts the daily use case needs. Not a generic
/// repository: it returns one purpose-built fact bundle for one resolved date range and optional
/// machine filter.
/// </summary>
public interface IDailyReportFactsProvider
{
    Task<DailyReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken);
}
