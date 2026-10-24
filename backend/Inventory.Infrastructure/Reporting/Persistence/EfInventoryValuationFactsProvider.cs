using Inventory.Application.Reporting.Dashboard;
using Inventory.Domain.Reporting.Dashboard;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IInventoryValuationFactsProvider"/>. It lives in
/// Inventory.Infrastructure for the same reason the other <c>Ef&lt;Feature&gt;ReportFactsProvider</c>
/// adapters do: it depends on <see cref="AppDbContext"/>, which moved here in issue #307 ahead of
/// this adapter family (issue #308, Persistence 7/8 of #153). The context's global business query
/// filter already scopes this projection to the caller's business, so no per-call filter is needed
/// here.
///
/// <para>Since issue #861 it also resolves whether a product has any recorded inventory history,
/// through the shared <see cref="EfProductInventoryHistoryQueries"/> the catalogue adapter behind the
/// home Dashboard summary uses - one query, so the two surfaces cannot disagree about whether a
/// product is empty. The history is asked about only for the products whose own stored columns could
/// still be empty (<see cref="ProductInventoryValuationFact.CouldNeverHaveHeldInventory"/>, the
/// Domain rule, read rather than restated), so a fully costed catalogue costs exactly the one
/// projection it always did.</para>
/// </summary>
public sealed class EfInventoryValuationFactsProvider : IInventoryValuationFactsProvider
{
    private readonly AppDbContext _db;

    public EfInventoryValuationFactsProvider(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProductInventoryValuationFact>> GetProductValuationFactsAsync(
        CancellationToken cancellationToken)
    {
        var positions = await _db.Products
            .AsNoTracking()
            .Select(p => new
            {
                p.Id,
                p.InventoryValue,
                p.CostingQuantity,
                p.QuantityInStock,
            })
            .ToListAsync(cancellationToken);

        var candidates = positions
            .Where(position => ProductInventoryValuationFact.CouldNeverHaveHeldInventory(
                position.InventoryValue, position.CostingQuantity, position.QuantityInStock))
            .Select(position => position.Id)
            .ToHashSet();
        var withHistory = await EfProductInventoryHistoryQueries.WithRecordedInventoryHistoryAsync(
            _db, candidates, cancellationToken);

        return positions
            .Select(position => new ProductInventoryValuationFact(
                position.InventoryValue,
                position.CostingQuantity,
                position.QuantityInStock,
                // A product with a costing position or storage stock is decided by that position
                // alone, so its history is neither read nor needed.
                !candidates.Contains(position.Id) || withHistory.Contains(position.Id)))
            .ToList();
    }
}
