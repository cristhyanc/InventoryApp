namespace Inventory.Application.Reporting.ProductProfitability;

/// <summary>
/// Narrow Application-owned port for the report facts the product profitability use case needs.
/// Not a generic repository: it returns one purpose-built fact bundle for one resolved date range
/// and optional machine filter.
/// </summary>
public interface IProductProfitabilityReportFactsProvider
{
    Task<ProductProfitabilityReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken);
}
