using Inventory.Application.Costing;
using Inventory.Application.Time;
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
/// The queries are the former <c>SaleCostingService</c>'s: the product catalogue, the product's
/// transition-baseline cutoff and the sales that consumed a unit, ordered by authorization time, all
/// read through <see cref="AppDbContext"/>'s business query filter. Each loaded sale keeps a
/// reference to its row, so <see cref="StageCost"/> writes the decision of a sale-costing use case
/// back onto exactly that tracked row. It applies no costing rule of its own.
///
/// <para>Which sales those are is <c>EfNayaxSalesQueries.StockConsumingSalePredicate</c> since issue
/// #1107 - status 12 <em>or</em> status 62 - rather than completed sales alone: a refunded sale
/// dispensed its unit and keeps its historical cost, because what a refund reverses is money.</para>
///
/// <para><strong>A staged cost records the rebuild it makes necessary (issue #1261).</strong> A sale's
/// historical cost is exactly the history the product's costing position is derived from, so every
/// product a staged cost touched gets a <see cref="PendingInventoryCostRebuild"/> row from its
/// earliest changed sale's instant, through the shared
/// <see cref="PendingInventoryCostRebuildRecorder"/> - written in the same write-locked transaction
/// and the same <c>SaveChanges</c> as the costs themselves, so a failure leaves neither. Before this,
/// a backfill rewrote sale costs and enqueued nothing: the product's <c>CostingQuantity</c> and
/// <c>InventoryValue</c> stayed as the costs it replaced had left them until its next sale.</para>
/// </summary>
public sealed class EfSaleCostingStore : ISaleCostingStore
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly Dictionary<long, DateTime> _rebuilds = [];

    public EfSaleCostingStore(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<DateTime?> GetTransitionCutoffAsync(long productId, CancellationToken cancellationToken) =>
        _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => x.ProductId == productId)
            .Select(x => (DateTime?)x.CutoffAt)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CostableSale>> LoadStockConsumingSalesAsync(
        StockConsumingSaleSelection selection, bool forUpdate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var query = (forUpdate ? _db.NayaxSales.AsQueryable() : _db.NayaxSales.AsNoTracking())
            .Where(EfNayaxSalesQueries.StockConsumingSalePredicate);
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

        // The product whose cost history this changed, from this sale's instant (issue #1261). An
        // unresolved sale belongs to no product, consumes no costing inventory and has no position to
        // replay, so it records no work.
        PendingInventoryCostRebuildRecorder.Add(
            _rebuilds, efSale.Entity.ProductId, efSale.Entity.MachineAuthorizationTime);
    }

    /// <summary>
    /// Saves the staged costs and the rebuild each of them made necessary, in one transaction. The
    /// write lock is taken before the pending rows are read, as every other writer of them does, so
    /// the revision this increments from is the stored one.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        await PendingInventoryCostRebuildRecorder
            .StageAsync(_db, _rebuilds, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _rebuilds.Clear();
    }
}
