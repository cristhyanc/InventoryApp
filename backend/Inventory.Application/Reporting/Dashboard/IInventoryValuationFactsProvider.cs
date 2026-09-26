namespace Inventory.Application.Reporting.Dashboard;

/// <summary>
/// Narrow Application-owned port for the raw per-product inventory values the dashboard's
/// inventory-valuation summary aggregates. Not a generic repository: it returns exactly the one
/// projection this use case needs, already scoped to the caller's business by the persistence
/// adapter's tenant-filtered context.
/// </summary>
public interface IInventoryValuationFactsProvider
{
    Task<IReadOnlyList<decimal?>> GetProductInventoryValuesAsync(CancellationToken cancellationToken);
}
