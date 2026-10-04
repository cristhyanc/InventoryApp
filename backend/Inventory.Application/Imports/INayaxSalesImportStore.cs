using Inventory.Application.Costing;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Imports;

/// <summary>
/// One importable Nayax sale: a parsed row whose required identity facts are all present, so the
/// store never has to re-decide whether a row is importable (issue #301). Every field is a raw
/// imported fact; the historical cost and its provenance are decided separately by
/// <see cref="ICostSale"/> and written through <see cref="INayaxSalesImportStore.StageCost"/>.
/// </summary>
public sealed record ImportedNayaxSale(
    long TransactionId,
    long MachineId,
    DateTime MachineAuthorizationTime,
    int? TransactionStatusId,
    long? NayaxProductId,
    string? MachineName,
    decimal SettlementValue,
    string? PaymentMethod,
    string? ProductName,
    decimal? NayaxProductCostPrice);

/// <summary>
/// Narrow persistence port for the uploaded Nayax sales import (issue #301), owned by the
/// Application layer and implemented by the temporary API-owned
/// <c>InventoryApi.Adapters.Persistence.EfNayaxSalesImportStore</c>.
///
/// It decides nothing: the import's own rules - which row is importable, which stored row a row
/// updates, which product a completed sale affected, and when the rebuilt costs are saved - all
/// stay in <see cref="ImportNayaxSales"/>. Reads and writes are scoped to the caller's business by
/// the central query filter and ownership enforcer alone, never by a predicate of this port's own,
/// which is what keeps a remote <c>TransactionID</c> two businesses both hold from letting one
/// import overwrite the other's sale.
/// </summary>
public interface INayaxSalesImportStore
{
    /// <summary>The caller's product catalogue, as the Domain <see cref="ProductMatcher"/> consumes it.</summary>
    Task<IReadOnlyList<ProductMatchCandidate>> GetProductCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The caller's stored sale carrying this remote transaction identifier, or <c>null</c> when
    /// the business has none. The row is loaded for update, so <see cref="Update"/> and
    /// <see cref="StageCost"/> write onto exactly it.
    /// </summary>
    Task<CostableSale?> FindByTransactionIdAsync(long transactionId, CancellationToken cancellationToken);

    /// <summary>
    /// Stages <paramref name="sale"/> as a new sale of the caller's business and answers it as a
    /// costable sale. Nothing is persisted until <see cref="SaveChangesAsync"/>.
    /// </summary>
    CostableSale Add(ImportedNayaxSale sale);

    /// <summary>
    /// Overwrites every imported fact of <paramref name="stored"/> with <paramref name="sale"/>'s,
    /// leaving the row's identity and its costing columns alone, and answers the updated row as a
    /// costable sale. It applies no rule of its own: deciding what an update may overwrite -
    /// notably that a row carrying no cost price must not erase the stored one - belongs to
    /// <see cref="ImportNayaxSales"/>.
    /// </summary>
    /// <param name="stored">A sale <see cref="FindByTransactionIdAsync"/> returned.</param>
    /// <param name="sale">The facts to write onto it.</param>
    CostableSale Update(CostableSale stored, ImportedNayaxSale sale);

    /// <summary>Stages the decided historical cost and its provenance onto a staged or stored sale.</summary>
    void StageCost(CostableSale sale, SaleCostAssignment cost);

    /// <summary>
    /// The inventory-cost transition baseline cutoff of each of <paramref name="productIds"/> that
    /// has one. A product with no entry has no baseline, so a sale of it is not post-transition and
    /// its cost is not replayed.
    /// </summary>
    Task<IReadOnlyDictionary<long, DateTime>> GetTransitionCutoffsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    /// <summary>Persists everything staged so far.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
