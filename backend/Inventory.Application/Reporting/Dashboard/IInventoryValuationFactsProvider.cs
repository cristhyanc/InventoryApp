using Inventory.Domain.Reporting.Dashboard;

namespace Inventory.Application.Reporting.Dashboard;

/// <summary>
/// Narrow Application-owned port for the per-product facts the dashboard's inventory-valuation
/// summary aggregates. Not a generic repository: it returns exactly the one projection this use case
/// needs, already scoped to the caller's business by the persistence adapter's tenant-filtered
/// context.
///
/// <para>Since issue #861 a fact carries the product's whole inventory position rather than its
/// value alone, because the value on its own cannot distinguish a product that has never held
/// inventory from one whose inventory is genuinely unknown. The adapter is what resolves the
/// history evidence; <see cref="ProductInventoryValuationFact"/> states what it must prove.</para>
/// </summary>
public interface IInventoryValuationFactsProvider
{
    Task<IReadOnlyList<ProductInventoryValuationFact>> GetProductValuationFactsAsync(
        CancellationToken cancellationToken);
}
