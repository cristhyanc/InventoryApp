namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// Narrow Application-owned port for the imported-summary data-quality facts the GST accounting-aid
/// report needs. Not a generic repository: it returns one purpose-built fact bundle for one resolved
/// date range and optional machine filter.
/// </summary>
public interface IGstReportFactsProvider
{
    Task<GstReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken);
}
