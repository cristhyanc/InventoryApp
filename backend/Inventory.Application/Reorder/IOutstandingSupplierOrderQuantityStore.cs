namespace Inventory.Application.Reorder;

/// <summary>
/// The narrow Application-facing port for the outstanding (not yet fully received, not cancelled)
/// supplier-order quantity per product that <see cref="CalculateReorderNeeds"/> needs to compute
/// <c>OnOrderQuantity</c> (issue #47). Its EF Core implementation lives in InventoryApi, alongside the
/// other <c>Ef&lt;Feature&gt;</c> adapters, because it depends on <c>AppDbContext</c>, which still
/// lives there.
/// </summary>
public interface IOutstandingSupplierOrderQuantityStore
{
    Task<IReadOnlyDictionary<long, decimal>> GetOutstandingQuantitiesByProductAsync(CancellationToken cancellationToken);
}
