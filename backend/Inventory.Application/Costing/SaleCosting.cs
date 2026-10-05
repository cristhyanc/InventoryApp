using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Costing;

/// <summary>
/// A sale's persisted costing status (issue #297), mirroring the persisted
/// <c>Inventory.Infrastructure.Models.SaleCostingStatus</c> member-for-member and ordinal-for-ordinal so the
/// temporary API-owned adapter converts between them by a plain cast.
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
    public long? NayaxProductId { get; init; }
    public string? ProductName { get; init; }
    public required DateTime AuthorizationTime { get; init; }

    /// <summary>The raw transaction-level Nayax <c>Product Cost Price</c>, as imported.</summary>
    public decimal? NayaxProductCostPrice { get; init; }

    public decimal? UnitCostAtSale { get; init; }
    public decimal? CostOfGoodsSold { get; init; }
    public SaleCostStatus CostingStatus { get; init; }
}

/// <summary>
/// The historical cost a use case decided for one sale. A sale is one unit, so the cost of goods
/// sold equals <see cref="UnitCost"/>; a <c>null</c> cost leaves the sale uncosted.
/// </summary>
public sealed record SaleCostAssignment(decimal? UnitCost, SaleCostStatus Status, SaleCostOrigin Source);

/// <summary>Which completed sales a sale-costing use case loads.</summary>
/// <param name="PendingOnly">Only sales whose costing status is still <see cref="SaleCostStatus.Pending"/>.</param>
/// <param name="From">Inclusive lower bound on the authorization time, when set.</param>
/// <param name="To">Inclusive upper bound on the authorization time, when set.</param>
public sealed record CompletedSaleSelection(bool PendingOnly = false, DateTime? From = null, DateTime? To = null);

/// <summary>
/// Narrow persistence port for historical sale costing (issue #297), owned by the Application
/// layer. Its implementation reads through the caller's business scope and writes back only what
/// a use case decided; it applies no costing rule of its own.
/// </summary>
public interface ISaleCostingStore
{
    /// <summary>The caller's product catalogue, as the Domain <see cref="ProductMatcher"/> consumes it.</summary>
    Task<IReadOnlyList<ProductMatchCandidate>> GetProductCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>The product's inventory-cost transition baseline cutoff, or <c>null</c> when it has none.</summary>
    Task<DateTime?> GetTransitionCutoffAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// The caller's completed sales (status ID 12 only) matching <paramref name="selection"/>,
    /// ordered by authorization time. With <paramref name="forUpdate"/> the rows are change-tracked
    /// so <see cref="StageCost"/> can update them; without it (a dry run) nothing is tracked.
    /// </summary>
    Task<IReadOnlyList<CostableSale>> LoadCompletedSalesAsync(
        CompletedSaleSelection selection, bool forUpdate, CancellationToken cancellationToken);

    /// <summary>Stages <paramref name="cost"/> onto a sale loaded for update.</summary>
    void StageCost(CostableSale sale, SaleCostAssignment cost);

    /// <summary>Persists every staged cost.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
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
