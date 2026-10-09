using Inventory.Application.Costing;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ISaleCostingStore"/> (issue #297). It lives in
/// Inventory.Infrastructure beside the <see cref="AppDbContext"/> and the
/// <see cref="NayaxSales"/>/<see cref="Product"/> persistence models it depends on, which moved
/// there in issue #307; this adapter followed them in issue #309 (Persistence 8/8 of #153).
///
/// The queries are unchanged from the former <c>SaleCostingService</c>: the product catalogue, the
/// product's transition-baseline cutoff and the completed (status ID 12) sales ordered by
/// authorization time, all read through <see cref="AppDbContext"/>'s business query filter. Each
/// loaded sale keeps a reference to its row, so <see cref="StageCost"/> writes the decision of a
/// sale-costing use case back onto exactly that tracked row. It applies no costing rule of its own.
/// </summary>
public sealed class EfSaleCostingStore : ISaleCostingStore
{
    private readonly AppDbContext _db;

    public EfSaleCostingStore(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProductMatchCandidate>> GetProductCandidatesAsync(CancellationToken cancellationToken) =>
        (await _db.Products.ToListAsync(cancellationToken))
            .Select(product => new ProductMatchCandidate(product.Id, product.Name))
            .ToList();

    public Task<DateTime?> GetTransitionCutoffAsync(long productId, CancellationToken cancellationToken) =>
        _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => x.ProductId == productId)
            .Select(x => (DateTime?)x.CutoffAt)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CostableSale>> LoadCompletedSalesAsync(
        CompletedSaleSelection selection, bool forUpdate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var query = (forUpdate ? _db.NayaxSales.AsQueryable() : _db.NayaxSales.AsNoTracking())
            .Where(EfNayaxSalesQueries.CompletedSalePredicate);
        if (selection.PendingOnly)
            query = query.Where(s => s.CostingStatus == SaleCostingStatus.Pending);
        if (selection.From.HasValue)
            query = query.Where(s => s.MachineAuthorizationTime >= selection.From.Value);
        if (selection.To.HasValue)
            query = query.Where(s => s.MachineAuthorizationTime <= selection.To.Value);

        return (await query.OrderBy(s => s.MachineAuthorizationTime).ToListAsync(cancellationToken))
            .Select(sale => (CostableSale)new NayaxCostableSale(sale))
            .ToList();
    }

    public void StageCost(CostableSale sale, SaleCostAssignment cost)
    {
        var efSale = sale as NayaxCostableSale
            ?? throw new ArgumentException("The sale was not loaded from a NayaxSales row.", nameof(sale));
        efSale.Entity.ApplySaleCost(cost);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}
