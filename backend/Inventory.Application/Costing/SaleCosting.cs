namespace Inventory.Application.Costing;

/// <summary>
/// A sale's persisted costing status (issue #297), mirroring the persisted
/// <c>Inventory.Infrastructure.Models.SaleCostingStatus</c> member-for-member and ordinal-for-ordinal so the
/// EF adapter converts between them by a plain cast.
/// </summary>
public enum SaleCostStatus
{
    Pending = 0,
    Costed = 1,
    LegacyEstimated = 2,
    Error = 3
}

/// <summary>
/// The provenance of a sale's historical cost (issue #297), mirroring the persisted
/// <c>Inventory.Infrastructure.Models.SaleCostSource</c> member-for-member and ordinal-for-ordinal.
/// </summary>
public enum SaleCostOrigin
{
    Unknown = 0,
    InventoryLedger = 1,
    NayaxTransactionExport = 2,
    Estimated = 3
}

/// <summary>
/// The facts of one Nayax sale that historical sale costing reads (issue #297). It is not sealed
/// so the store implementation can carry the persistence entity it came from and write the use
/// case's decision back to exactly that row.
/// </summary>
public class CostableSale
{
    public required long TransactionId { get; init; }
    public int? TransactionStatusId { get; init; }

    /// <summary>
    /// The sale's queue-delivery verification state, or <c>null</c> for a sale the queue did not
    /// deliver. Read only through <see cref="Domain.NayaxSqs.NayaxSaleCountingPolicy"/>: a sale the
    /// business's own <c>lastSales</c> contradicted, or that its Owner excluded, consumes no costing
    /// inventory and is neither costed nor counted (issue #1105).
    /// </summary>
    public Domain.NayaxSqs.NayaxSqsVerification? SqsVerification { get; init; }

    /// <summary>
    /// The local product the sale is attributed to, as persisted on the row (issue #758), or
    /// <c>null</c> when the sale is unresolved. Costing reads exactly this and never re-derives it
    /// from <see cref="NayaxProductId"/> or <see cref="ProductName"/>: an unresolved sale is left
    /// uncosted rather than attached to a product by a number or a name.
    /// </summary>
    public long? ProductId { get; init; }

    /// <summary>
    /// The provider's own product identifier, kept for the cost-price provenance and the reports
    /// that show what the provider reported. It is never read as a local product id (issue #758).
    /// </summary>
    public long? NayaxProductId { get; init; }
    public string? ProductName { get; init; }
    public required DateTime AuthorizationTime { get; init; }

    /// <summary>The raw transaction-level Nayax <c>Product Cost Price</c>, as imported.</summary>
    public decimal? NayaxProductCostPrice { get; init; }

    public decimal? UnitCostAtSale { get; init; }
    public decimal? CostOfGoodsSold { get; init; }
    public SaleCostStatus CostingStatus { get; init; }

    /// <summary>
    /// The provenance of the cost currently stored on the sale, which is what
    /// <see cref="SaleCostPrecedence"/> compares a newly decided cost against (issue #1261):
    /// a forced backfill may not replace a better-sourced cost with a worse-sourced one.
    /// <see cref="SaleCostOrigin.Unknown"/> on a costed sale is a legacy cost with no recorded
    /// provenance.
    /// </summary>
    public SaleCostOrigin CostSource { get; init; }
}

/// <summary>
/// The historical cost a use case decided for one sale. A sale is one unit, so the cost of goods
/// sold equals <see cref="UnitCost"/>; a <c>null</c> cost leaves the sale uncosted.
/// </summary>
public sealed record SaleCostAssignment(decimal? UnitCost, SaleCostStatus Status, SaleCostOrigin Source);

/// <summary>
/// The four costing columns of one sale, as a row holds them (issue #1261). It is what
/// <see cref="SaleCostPrecedence"/> compares a decision against, and it exists as its own value so
/// the question can also be asked of the <strong>latest persisted</strong> row inside the write-locked
/// transaction - which a <see cref="CostableSale"/> read before that lock cannot answer for.
/// </summary>
public sealed record StoredSaleCost(
    decimal? UnitCost, decimal? CostOfGoodsSold, SaleCostStatus Status, SaleCostOrigin Source)
{
    /// <summary>The cost <paramref name="sale"/> carried when it was read.</summary>
    public static StoredSaleCost Of(CostableSale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        return new(sale.UnitCostAtSale, sale.CostOfGoodsSold, sale.CostingStatus, sale.CostSource);
    }
}

/// <summary>
/// A staged cost the write lock refused (issue #1261): another writer had committed a cost on that
/// sale between the read the decision was made from and the lock, and the decision no longer holds
/// against the row. Nothing was written for the sale and no cost rebuild was recorded for it, so a
/// run that reports what it changed must not count it as changed either.
/// </summary>
/// <param name="Sale">The sale, as the run read it.</param>
/// <param name="Decided">The cost the run had decided and staged, which was discarded.</param>
/// <param name="Stored">The cost the latest persisted row holds instead.</param>
public sealed record RefusedSaleCost(
    CostableSale Sale, SaleCostAssignment Decided, StoredSaleCost Stored);

/// <summary>
/// What one <see cref="ISaleCostingStore.SaveChangesAsync"/> committed (issue #1261). A caller that
/// reports how many sales it changed reclassifies each <see cref="Refused"/> cost; one that reports
/// only the state the sales are now in may ignore it.
/// </summary>
public sealed record SaleCostSaveOutcome(IReadOnlyList<RefusedSaleCost> Refused)
{
    /// <summary>Every staged cost was written, which is the ordinary outcome.</summary>
    public static SaleCostSaveOutcome NothingRefused { get; } = new([]);
}

/// <summary>
/// Which of the caller's stock-consuming sales a sale-costing use case loads. Named for the rule it
/// selects on (<see cref="Domain.NayaxSqs.NayaxSaleCountingPolicy.ConsumesStock"/>) rather than for
/// completed sales, because since issue #1107 a refunded sale has a historical cost too: it
/// dispensed its unit, the business no longer owns it, and only the money was reversed.
/// </summary>
/// <param name="PendingOnly">Only sales whose costing status is still <see cref="SaleCostStatus.Pending"/>.</param>
/// <param name="From">Inclusive lower bound on the authorization time, when set.</param>
/// <param name="To">Inclusive upper bound on the authorization time, when set.</param>
public sealed record StockConsumingSaleSelection(bool PendingOnly = false, DateTime? From = null, DateTime? To = null);

/// <summary>
/// Narrow persistence port for historical sale costing (issue #297), owned by the Application
/// layer. Its implementation reads through the caller's business scope and writes back only what
/// a use case decided; it applies no costing rule of its own.
/// </summary>
public interface ISaleCostingStore
{
    /// <summary>The product's inventory-cost transition baseline cutoff, or <c>null</c> when it has none.</summary>
    Task<DateTime?> GetTransitionCutoffAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// The caller's stock-consuming sales (status ID 12 or 62, neither excluded by review) matching
    /// <paramref name="selection"/>, ordered by authorization time. With
    /// <paramref name="forUpdate"/> the rows are change-tracked so <see cref="StageCost"/> can
    /// update them; without it (a dry run) nothing is tracked.
    /// </summary>
    Task<IReadOnlyList<CostableSale>> LoadStockConsumingSalesAsync(
        StockConsumingSaleSelection selection, bool forUpdate, CancellationToken cancellationToken);

    /// <summary>
    /// Stages <paramref name="cost"/> onto a sale loaded for update. A staged cost also records the
    /// inventory-cost rebuild it makes necessary for the sale's product (issue #1261): rewriting a
    /// sale's historical cost changes the history the product's costing position is derived from.
    /// </summary>
    void StageCost(CostableSale sale, SaleCostAssignment cost);

    /// <summary>
    /// Persists every staged cost, and the pending inventory-cost rebuild each of them made
    /// necessary, in one transaction - so a failure leaves neither (issue #1261).
    ///
    /// <para>Each staged cost is re-asked of <see cref="SaleCostPrecedence.StillReplaces"/> against
    /// the latest persisted row <strong>inside</strong> that transaction, because the decision was
    /// made on a sale read before the write lock existed. A staged cost another writer has committed
    /// over in that gap is discarded and reported in <see cref="SaleCostSaveOutcome.Refused"/>: it
    /// writes no cost and records no rebuild.</para>
    /// </summary>
    Task<SaleCostSaveOutcome> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Result of <see cref="BackfillSaleCosts"/>; the <c>POST api/sale-costing/backfill</c> response.</summary>
public record SaleCostingBackfillResult(
    int CostedCount,
    int LegacyEstimatedCount,
    int PendingCount,
    int ErrorCount,
    int AlreadyFinalizedCount,
    bool DryRun);

/// <summary>
/// Result of <see cref="BackfillNayaxHistoricalSaleCosts"/>; the
/// <c>POST api/sale-costing/nayax-cost-backfill/dry-run</c> and <c>/apply</c> response.
/// </summary>
public record NayaxCostBackfillResult(
    int SalesReviewed,
    int SalesWithNayaxCost,
    int SalesWouldBeCosted,
    int SalesAlreadyCosted,
    int SalesStillPending,
    int InvalidCostRows,
    int ErrorRows,
    bool DryRun);
