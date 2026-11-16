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
///
/// <para><strong>And the precedence decision is protected at write time (issue #1261).</strong> A
/// caller decides on sales it read before this adapter takes the write lock, so another writer can
/// commit a cost on one of them in that gap - and the staged decision would then be saved over a cost
/// whose comparison it had already lost, with a rebuild enqueued for it. Every staged cost is
/// therefore re-asked of <see cref="SaleCostPrecedence.StillReplaces"/> <em>inside</em> the
/// transaction, against the latest persisted row read by the database rather than from the tracked
/// instance the cost was staged onto. A refused cost is taken back off its row, records no rebuild,
/// and is reported to its caller so the run does not count it as changed.</para>
/// </summary>
public sealed class EfSaleCostingStore : ISaleCostingStore
{
    /// <summary>
    /// The columns <see cref="NayaxSaleCosting.ApplySaleCost"/> writes, which are therefore the ones a
    /// refused cost is taken back off.
    /// </summary>
    private static readonly string[] CostingColumns =
    [
        nameof(NayaxSales.UnitCostAtSale),
        nameof(NayaxSales.CostOfGoodsSold),
        nameof(NayaxSales.CostingStatus),
        nameof(NayaxSales.CostSource),
    ];

    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly List<StagedSaleCost> _staged = [];

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
        _staged.Add(new StagedSaleCost(sale, efSale.Entity, cost));
    }

    /// <summary>
    /// Saves the staged costs the write lock still allows, and the rebuild each of them made
    /// necessary, in one transaction. The lock is taken before anything is read, as every other writer
    /// of a pending row does, so the precedence decisions are checked against the stored sales and the
    /// revision the recorder increments from is the stored one.
    /// </summary>
    public async Task<SaleCostSaveOutcome> SaveChangesAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var (refused, rebuilds) = await ReviewStagedCostsAsync(cancellationToken).ConfigureAwait(false);

        await PendingInventoryCostRebuildRecorder
            .StageAsync(_db, rebuilds, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _staged.Clear();
        return refused.Count == 0 ? SaleCostSaveOutcome.NothingRefused : new SaleCostSaveOutcome(refused);
    }

    /// <summary>
    /// Asks the precedence rule about every staged cost again, now that the write lock is held: the
    /// ones it refuses are taken back off their rows, and the ones it allows are the work that is
    /// recorded - one product's rebuild from its earliest changed sale (issue #1261). An unresolved
    /// sale belongs to no product, consumes no costing inventory and has no position to replay, so it
    /// records no work either.
    /// </summary>
    private async Task<(List<RefusedSaleCost> Refused, Dictionary<long, DateTime> Rebuilds)>
        ReviewStagedCostsAsync(CancellationToken cancellationToken)
    {
        var refused = new List<RefusedSaleCost>();
        var rebuilds = new Dictionary<long, DateTime>();
        if (_staged.Count == 0)
            return (refused, rebuilds);

        var persisted = await PersistedCostsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var staged in _staged)
        {
            // A sale this run read is always found: no path deletes one. A row that is not found is
            // left to EF Core's own concurrency check on the update, which refuses the whole save
            // rather than letting this adapter decide anything about a sale that is no longer there.
            if (persisted.TryGetValue(staged.Entity.TransactionID, out var stored) &&
                !SaleCostPrecedence.StillReplaces(StoredSaleCost.Of(staged.Sale), stored, staged.Cost))
            {
                Discard(staged.Entity);
                refused.Add(new RefusedSaleCost(staged.Sale, staged.Cost, stored));
                continue;
            }

            PendingInventoryCostRebuildRecorder.Add(
                rebuilds, staged.Entity.ProductId, staged.Entity.MachineAuthorizationTime);
        }

        return (refused, rebuilds);
    }

    /// <summary>
    /// The costing columns the staged sales' rows hold now, projected rather than materialised as
    /// entities: a tracking query would be answered with the very instances the staged costs were
    /// written onto, so the answer has to come from the database. The business query filter is what
    /// keeps it to the caller's own sales, as it does for every other read here.
    /// </summary>
    private async Task<Dictionary<long, StoredSaleCost>> PersistedCostsAsync(
        CancellationToken cancellationToken)
    {
        var transactionIds = _staged.Select(staged => staged.Entity.TransactionID).Distinct().ToList();

        var rows = await _db.NayaxSales.AsNoTracking()
            .Where(sale => transactionIds.Contains(sale.TransactionID))
            .Select(sale => new
            {
                sale.TransactionID,
                sale.UnitCostAtSale,
                sale.CostOfGoodsSold,
                sale.CostingStatus,
                sale.CostSource,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(
            row => row.TransactionID,
            row => new StoredSaleCost(
                row.UnitCostAtSale,
                row.CostOfGoodsSold,
                (SaleCostStatus)row.CostingStatus,
                (SaleCostOrigin)row.CostSource));
    }

    /// <summary>
    /// Takes a staged cost back off its row: each costing column returns to the value the row was
    /// loaded with, so the save writes nothing for this sale at all.
    /// </summary>
    private void Discard(NayaxSales sale)
    {
        var entry = _db.Entry(sale);
        foreach (var column in CostingColumns)
            entry.Property(column).IsModified = false;
    }

    /// <summary>One cost a caller staged, and the row and the sale it was staged for.</summary>
    private sealed record StagedSaleCost(CostableSale Sale, NayaxSales Entity, SaleCostAssignment Cost);
}
