namespace Inventory.Application.Reorder;

/// <summary>
/// The narrow Application-facing port for the outstanding (not yet fully received, not cancelled)
/// supplier-order quantity per product that <see cref="CalculateReorderNeeds"/> needs to compute
/// <c>OnOrderQuantity</c> (issue #47). Its EF Core implementation lives in
/// <c>Inventory.Infrastructure.Persistence</c>, alongside the other <c>Ef&lt;Feature&gt;</c>
/// adapters: <c>AppDbContext</c> moved to <c>Inventory.Infrastructure</c> in issue #307 and that
/// adapter family followed it in issue #309 (Persistence 8/8 of #153).
/// </summary>
public interface IOutstandingSupplierOrderQuantityStore
{
    Task<IReadOnlyDictionary<long, decimal>> GetOutstandingQuantitiesByProductAsync(CancellationToken cancellationToken);
}
