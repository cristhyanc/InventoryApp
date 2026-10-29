namespace Inventory.Application.Reorder;

/// <summary>
/// The narrow Application-facing port for the outstanding supplier-order quantity per product that
/// <see cref="CalculateReorderNeeds"/> needs to compute <c>OnOrderQuantity</c> (issue #47).
///
/// <para>Outstanding means "still on its way". An order that is cancelled, fully received, or
/// explicitly completed with part of it undelivered (issue #974) contributes nothing, because nobody
/// is sending the remainder - counting a closed balance as supply would suppress a reorder alert for
/// goods that are never coming.</para>
///
/// Its EF Core implementation lives in
/// <c>Inventory.Infrastructure.Persistence</c>, alongside the other <c>Ef&lt;Feature&gt;</c>
/// adapters: <c>AppDbContext</c> moved to <c>Inventory.Infrastructure</c> in issue #307 and that
/// adapter family followed it in issue #309 (Persistence 8/8 of #153).
/// </summary>
public interface IOutstandingSupplierOrderQuantityStore
{
    Task<IReadOnlyDictionary<long, decimal>> GetOutstandingQuantitiesByProductAsync(CancellationToken cancellationToken);
}
