namespace Inventory.Application.Reorder;

/// <summary>
/// The narrow Application-facing port for the outstanding (not yet fully received, not cancelled)
/// supplier-order quantity per product that <see cref="CalculateReorderNeeds"/> needs to compute
/// <c>OnOrderQuantity</c> (issue #47). Its EF Core implementation still lives in InventoryApi,
/// alongside the other <c>Ef&lt;Feature&gt;</c> adapters; <c>AppDbContext</c> moved to
/// <c>Inventory.Infrastructure</c> in issue #307 and that adapter family follows it in
/// Persistence 7/8 and 8/8 of #153.
/// </summary>
public interface IOutstandingSupplierOrderQuantityStore
{
    Task<IReadOnlyDictionary<long, decimal>> GetOutstandingQuantitiesByProductAsync(CancellationToken cancellationToken);
}
