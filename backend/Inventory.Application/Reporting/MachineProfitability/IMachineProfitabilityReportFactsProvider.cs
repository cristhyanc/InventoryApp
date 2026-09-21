namespace Inventory.Application.Reporting.MachineProfitability;

/// <summary>
/// Narrow Application-owned port for the report facts the machine profitability use case needs.
/// Not a generic repository: it returns one purpose-built fact bundle for one resolved date range
/// and optional machine filter.
/// </summary>
public interface IMachineProfitabilityReportFactsProvider
{
    Task<MachineProfitabilityReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken);
}
