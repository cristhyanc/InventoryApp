using Inventory.Application.Machines;
using Inventory.Domain.MachineStock;

namespace Inventory.Application.MachineStock;

/// <summary>
/// Everything one machine's ledger is projected from, read in one go.
///
/// <see cref="ProductNames"/> covers every product the result may mention - the products the
/// movements and baselines name, plus the observed products that exist in the caller's own
/// catalogue - so a row can be labelled without a per-product fan-out, and so the use case can drop
/// an observed slot whose product this business does not have: "not in the catalogue" is the tenant
/// boundary and an unmapped slot at once, never a row to invent a name for.
///
/// <see cref="PendingNayaxStockEventCount"/> and <see cref="UnattributedCompletedSaleCount"/> are the
/// two machine-level reasons the ledger may be incomplete: Nayax refill events not yet applied in
/// Sync Restock (issue #183), and completed sales that match no catalogue product.
/// </summary>
public sealed record MachineStockLedgerFacts(
    IReadOnlyList<MachineRefillFact> Refills,
    IReadOnlyList<MachineSaleFact> Sales,
    IReadOnlyList<MachineStockCountFact> Counts,
    IReadOnlyList<MachineStockOpeningBaselineFact> OpeningBaselines,
    IReadOnlyDictionary<long, string> ProductNames,
    int PendingNayaxStockEventCount,
    int UnattributedCompletedSaleCount);

/// <summary>One recorded machine-stock count, as persisted.</summary>
public sealed record MachineStockCountRecord(
    int Id, long MachineId, long ProductId, string ProductName, int CountedQuantity, DateTime CountedAtUtc, string? Notes);

/// <summary>
/// Narrow persistence port for the machine-stock ledger (issue #56), owned by the Application layer.
///
/// It reads the facts the ledger is derived from and appends the one fact the application did not
/// have before - an operator's physical count. It deliberately owns no second copy of a refill or a
/// sale: those movements are already persisted as <c>StockAdjustment</c> rows and <c>NayaxSales</c>
/// rows, and reading them is what makes a machine's expected stock and its COGS impossible to count
/// twice.
/// </summary>
public interface IMachineStockLedgerStore
{
    /// <summary>
    /// The machine's ledger facts. <paramref name="observedProductIds"/> are the products the stock
    /// source reported for this machine, so their names resolve in the same read; pass an empty
    /// collection when no observation is available.
    /// </summary>
    /// <remarks>
    /// The refills and counts are this application's rows and are read by the machine's local id;
    /// the sales, the pending Nayax stock events and the inventory-cost transition's opening machine
    /// stock are the provider's numbers and are read by its Nayax number, and are empty for a machine
    /// that has none (issue #741).
    /// </remarks>
    Task<MachineStockLedgerFacts> GetLedgerFactsAsync(
        MachineIdentity machine, IReadOnlyCollection<long> observedProductIds, CancellationToken cancellationToken);

    /// <summary>
    /// Appends one counted reconciliation. Returns <c>null</c> when the product does not exist (or is
    /// not owned by the caller's business). It writes nothing but the count: no storage quantity, no
    /// costing quantity, no inventory value, no stock movement and no sale cost.
    /// </summary>
    Task<MachineStockCountRecord?> RecordCountAsync(
        long localMachineId,
        long productId,
        int countedQuantity,
        DateTime countedAtUtc,
        string? notes,
        CancellationToken cancellationToken);
}
