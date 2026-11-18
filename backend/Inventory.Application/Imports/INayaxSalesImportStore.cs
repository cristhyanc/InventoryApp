using Inventory.Application.Costing;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Imports;

/// <summary>
/// One importable Nayax sale: a parsed row whose required identity facts are all present, so the
/// store never has to re-decide whether a row is importable (issue #301). Every field is a raw
/// imported fact; the historical cost and its provenance are decided separately by
/// <see cref="ICostSale"/> and written through <see cref="INayaxSalesImportStore.StageCost"/>.
///
/// <para>The one derived field is <c>ProductId</c>: the local product the import resolved the row
/// to, or <c>null</c> when it resolved none (issue #758). It is persisted as the sale's one
/// association and no reader re-derives it afterwards. <c>NayaxProductId</c> keeps the provider's
/// own product identifier exactly as the export carried it - a remote identity, never a local
/// key.</para>
/// </summary>
public sealed record ImportedNayaxSale(
    long TransactionId,
    long MachineId,
    DateTime MachineAuthorizationTime,
    int? TransactionStatusId,
    long? ProductId,
    long? NayaxProductId,
    string? MachineName,
    decimal SettlementValue,
    string? PaymentMethod,
    string? ProductName,
    decimal? NayaxProductCostPrice);

/// <summary>
/// One sale this business already holds, as the import reads it before deciding what the uploaded
/// row may overwrite (issue #1298). It is the <see cref="Costing.CostableSale"/> view the costing
/// and replay path already used, plus the three imported facts that view does not carry, because a
/// blank cell or a missing column in the file must leave each of them exactly as it is.
///
/// <para>The costing contract stays what it says it is - the facts historical sale costing reads -
/// so a settlement value, a payment method and a machine name, which costing never reads, are
/// carried here instead of being added to it.</para>
///
/// <para>It is also how <see cref="ImportNayaxSales"/> carries a sale one of the file's own rows has
/// just applied (issue #1300), so a second row naming that transaction merges against it rather
/// than against the state the batched read found before either row ran. The store answers these from
/// the rows it loaded; the import builds one from the row it just wrote.</para>
/// </summary>
/// <param name="Sale">The stored sale's costing view, carrying its status, product and instant.</param>
/// <param name="SettlementValue">The stored settled amount; the column is not nullable.</param>
/// <param name="PaymentMethod">The stored raw payment method.</param>
/// <param name="MachineName">The stored machine name as a previous import or sync reported it.</param>
public sealed record StoredImportedSale(
    CostableSale Sale,
    decimal SettlementValue,
    string? PaymentMethod,
    string? MachineName);

/// <summary>
/// Narrow persistence port for the uploaded Nayax sales import (issue #301), owned by the
/// Application layer and implemented by
/// <c>Inventory.Infrastructure.Persistence.EfNayaxSalesImportStore</c>.
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
    /// The remote transaction identifiers of the caller's sales that issue #758's migration marked
    /// conflicting: their two candidate products disagreed, so only an audited decision may name
    /// one. The import leaves such a sale's product exactly as it is while updating every other
    /// imported fact. Expected to be empty.
    /// </summary>
    Task<IReadOnlyCollection<long>> GetConflictingTransactionIdsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The caller's stored sales carrying any of these remote transaction identifiers, keyed by the
    /// identifier. A transaction the business does not hold is simply absent. The rows are loaded
    /// for update, so <see cref="Update"/> and <see cref="StageCost"/> write onto exactly them.
    ///
    /// <para><strong>One read for the whole file</strong> (issue #1300), in one query or in
    /// fixed-size batches of the adapter's choosing - never one query per row, which is what made a
    /// 4,000-row upload take 30 seconds and an 8,000-row one 124. It is called <em>inside</em> the
    /// import's write-locked transaction, so what it answers is the state the save is then decided
    /// on: a transaction the Nayax SQS queue reader stored while the file was being read is found
    /// here and updated, instead of being inserted a second time and refused by the unique index on
    /// <c>(BusinessId, TransactionID)</c>. What the file's own rows then make of those sales is the
    /// import's business: it tracks a sale a row of the file applied itself rather than reading it
    /// again, so this stays one read.</para>
    /// </summary>
    Task<IReadOnlyDictionary<long, StoredImportedSale>> FindByTransactionIdsAsync(
        IReadOnlyCollection<long> transactionIds, CancellationToken cancellationToken);

    /// <summary>
    /// Stages <paramref name="sale"/> as a new sale of the caller's business and answers it as a
    /// costable sale. Nothing is persisted until <see cref="SaveChangesAsync"/>.
    /// </summary>
    CostableSale Add(ImportedNayaxSale sale);

    /// <summary>
    /// Overwrites every imported fact of <paramref name="stored"/> with <paramref name="sale"/>'s,
    /// leaving the row's identity and its costing columns alone, and answers the updated row as a
    /// costable sale. It applies no rule of its own: deciding what an update may overwrite -
    /// notably that a row carrying no cost price, no status, no amount, no payment method, no
    /// product name and no machine name must not erase the stored ones (issue #1298) - belongs to
    /// <see cref="ImportNayaxSales"/>, which hands this the values it decided.
    /// </summary>
    /// <param name="stored">A sale <see cref="FindByTransactionIdsAsync"/> returned.</param>
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
