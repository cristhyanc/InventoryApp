namespace Inventory.Application.Reporting.Bookkeeping;

/// <summary>
/// Narrow Application-owned port for the report facts the bookkeeping use case needs. Not a
/// generic repository: it returns one purpose-built fact bundle for one resolved date range and
/// optional machine filter.
/// </summary>
public interface IBookkeepingReportFactsProvider
{
    Task<BookkeepingReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken);
}
