namespace Inventory.Application.Costing;

// The costing-repair request and response contracts (issue #359), owned by the Application layer.
// HTTP endpoints and UI are separate tasks; these are the shapes those tasks bind to.

/// <summary>What the operator proposes: a costing-only acquisition for one product.</summary>
public sealed record InventoryCostRepairRequest(
    long ProductId,
    DateTime EffectiveAt,
    int Quantity,
    decimal UnitCost,
    string Reason);

/// <summary>
/// The same proposal, confirmed against the ledger it was previewed on.
/// <paramref name="LedgerFingerprint"/> is the value the preview reported; the apply recomputes it
/// from its own authoritative read and refuses to write when it differs.
/// </summary>
public sealed record ApplyInventoryCostRepairRequest(
    long ProductId,
    DateTime EffectiveAt,
    int Quantity,
    decimal UnitCost,
    string Reason,
    string LedgerFingerprint);

/// <summary>A completed sale the replay cannot cost, as the repair preview reports it.</summary>
public sealed record UncostableSale(long TransactionId, DateTime AuthorizationTime);

/// <summary>
/// What applying the proposed repair would do, computed without persisting anything.
///
/// The three positions it reports answer three different questions: the costing quantity/value
/// <em>before</em> the repair is what the ledger holds at that instant; the <em>after</em> values
/// are that position plus the repair, with the resulting weighted-average unit cost; and the
/// <em>projected</em> values are where the whole history ends up once the rest of it replays on top,
/// together with the fatal issues that would still be left. A repair is worth applying only when
/// <see cref="RemainingFatalIssues"/> is empty - a rebuild refuses a history that still has one.
/// </summary>
public sealed record InventoryCostRepairPreview(
    long ProductId,
    string ProductName,
    DateTime EffectiveAt,
    int Quantity,
    decimal UnitCost,
    decimal TotalValue,
    string Reason,
    int CostingQuantityBefore,
    decimal InventoryValueBefore,
    int CostingQuantityAfter,
    decimal InventoryValueAfter,
    decimal? AverageUnitCostAfter,
    UncostableSale? FirstUncostableSale,
    bool ReplaysBeforeFirstUncostableSale,
    int ProjectedCostingQuantity,
    decimal ProjectedInventoryValue,
    decimal? ProjectedAverageUnitCost,
    IReadOnlyList<InventoryCostDataQualityIssue> RemainingFatalIssues,
    string LedgerFingerprint);

/// <summary>One stored costing repair, as the history query and the apply result report it.</summary>
public sealed record InventoryCostRepairRecord(
    int Id,
    long ProductId,
    DateTime EffectiveAt,
    int Quantity,
    decimal UnitCost,
    decimal TotalValue,
    string Reason,
    DateTime CreatedAt,
    string CreatedByDirectoryTenantId,
    string CreatedByObjectId);

/// <summary>The persisted repair and the product's rebuilt costing position after it.</summary>
public sealed record InventoryCostRepairApplied(
    InventoryCostRepairRecord Repair,
    int CostingQuantity,
    decimal InventoryValue,
    decimal? AverageUnitCost,
    int RecostedSaleCount);

/// <summary>A repair to append. The store never receives an identifier or a business.</summary>
public sealed record NewInventoryCostRepair(
    long ProductId,
    DateTime EffectiveAt,
    int Quantity,
    decimal UnitCost,
    decimal TotalValue,
    string Reason,
    DateTime CreatedAt,
    string CreatedByDirectoryTenantId,
    string CreatedByObjectId);

/// <summary>The product facts a costing repair needs: enough to name it in the preview.</summary>
public sealed record InventoryCostRepairProduct(long Id, string Name);
