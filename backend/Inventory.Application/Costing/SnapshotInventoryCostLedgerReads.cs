using Inventory.Domain.Costing;

namespace Inventory.Application.Costing;

/// <summary>
/// An <see cref="IInventoryCostLedgerStore"/> that answers <see cref="LoadAsOfAsync"/> from one
/// read of each product's ledger, for a caller that knows the database does not change while it is
/// in use (issue #1304).
///
/// <para><strong>Why it exists.</strong> Costing a sale replays its product's ledger as of the sale
/// (<see cref="RebuildProductCost.GetAverageUnitCostAtAsync"/>), and the as-of read loads every
/// movement and stored sale of the product up to that instant. A batch of the background sales
/// history import costs hundreds of rows of the same few products before it saves anything, so
/// reading the ledger once per row made each batch's write-lock hold grow with the sales already
/// stored per product - past the 10-second budget once a product held about three thousand. Read
/// once per product per batch instead, the hold no longer pays one ledger read per row.</para>
///
/// <para><strong>It changes no cost.</strong> The first as-of read of a product loads the product's
/// whole read-only ledger through the inner store's <c>LoadAsync(forUpdate: false)</c>; every
/// as-of read is then that ledger filtered in memory with exactly the inner as-of read's
/// predicates - movements and repairs effective, completed sales authorised, at or before the
/// instant. The replay orders its events by its own total key (timestamp, priority, source id), so
/// the order the rows were read in cannot change its answer. The one input the full read cannot
/// reproduce for every instant is the transition baseline: when the product's baseline is cut off
/// after the instant asked about, the read is passed to the inner store unchanged rather than
/// decided here.</para>
///
/// <para><strong>Its lifetime is the caller's promise.</strong> Nothing here notices a write. An
/// instance must be used only while the rows it has read cannot change - for the history import,
/// one batch's row pass, which saves nothing until it ends - and then thrown away. Every other
/// member is the inner store's, unchanged and uncached.</para>
/// </summary>
public sealed class SnapshotInventoryCostLedgerReads : IInventoryCostLedgerStore
{
    private readonly IInventoryCostLedgerStore _inner;
    private readonly Dictionary<long, InventoryCostLedger?> _ledgers = [];

    public SnapshotInventoryCostLedgerReads(IInventoryCostLedgerStore inner) => _inner = inner;

    public Task<InventoryCostLedger?> LoadAsync(
        long productId, bool forUpdate, CancellationToken cancellationToken) =>
        _inner.LoadAsync(productId, forUpdate, cancellationToken);

    public async Task<InventoryCostLedger?> LoadAsOfAsync(
        long productId, DateTime asOf, CancellationToken cancellationToken)
    {
        if (!_ledgers.TryGetValue(productId, out var ledger))
        {
            ledger = await _inner.LoadAsync(productId, forUpdate: false, cancellationToken);
            _ledgers[productId] = ledger;
        }

        if (ledger is null)
            return null;

        // A baseline cut off after the instant is not in range of the as-of read. Rather than decide
        // here what is, the inner as-of read answers that instant itself.
        if (ledger.Baseline is not null && ledger.Baseline.CutoffAt > asOf)
            return await _inner.LoadAsOfAsync(productId, asOf, cancellationToken);

        return new InventoryCostLedger(
            ledger.Product,
            ledger.Adjustments.Where(x => x.EffectiveAt <= asOf).ToList(),
            ledger.Sales.Where(x => x.AuthorizationTime <= asOf).ToList(),
            ledger.Repairs.Where(x => x.EffectiveAt <= asOf).ToList(),
            ledger.Baseline);
    }

    public void StageReplay(
        InventoryCostLedger ledger,
        IReadOnlyCollection<CostReplayAdjustmentOutcome> adjustments,
        IReadOnlyCollection<CostReplaySaleCost> recostedSales) =>
        _inner.StageReplay(ledger, adjustments, recostedSales);

    public void StageProductPosition(InventoryCostLedger ledger, ProductCostPosition position) =>
        _inner.StageProductPosition(ledger, position);
}
