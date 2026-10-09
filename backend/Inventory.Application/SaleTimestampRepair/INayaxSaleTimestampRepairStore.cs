using Inventory.Domain.Nayax;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.SaleTimestampRepair;

/// <summary>
/// The database transaction a sale timestamp repair apply runs in. Disposing it without
/// <see cref="CommitAsync"/> rolls back every change saved through the store since it began -
/// the repaired instants, the audit rows, the applied marker on the draft and whatever the costing
/// replay staged - which is what makes the maintenance action all-or-nothing.
/// </summary>
public interface INayaxSaleTimestampRepairTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>A preview plan to store, serialized by the use case.</summary>
public sealed record NewNayaxSaleTimestampRepairDraft(
    Guid Id,
    string PlanJson,
    DateTime CreatedAt,
    DateTime ExpiresAt);

/// <summary>A stored preview plan, as loaded for apply.</summary>
public sealed record StoredNayaxSaleTimestampRepairDraft(
    Guid Id,
    string PlanJson,
    DateTime ExpiresAt,
    DateTime? AppliedAt);

/// <summary>
/// Narrow persistence port for the Nayax sale timestamp repair use cases (issue #472), owned by the
/// Application layer.
///
/// It reads and writes through the caller's business scope - the <c>AppDbContext</c> tenant query
/// filters and the ownership stamp on save - so it carries no business predicate of its own and
/// cannot reach, examine or repair another business's sale, and it shares its unit of work with the
/// <see cref="Costing.IRebuildProductCost"/> replay so one transaction covers the repair and the
/// recosting. It decides nothing: <see cref="NayaxSaleTimestampRepairPolicy"/> classifies every
/// stored sale and <see cref="ProductMatcher"/> matches the products, and the adapter only projects
/// stored rows and persists the instants and audit rows it is handed.
///
/// The port is deliberately append-only for the audit, and has no method to move a sale to a
/// caller-supplied instant, to delete a sale, to add one, or to change an amount, a status or a
/// product mapping: a repair has authority over exactly one column.
/// </summary>
public interface INayaxSaleTimestampRepairStore
{
    /// <summary>Begins the transaction an apply reads and writes in.</summary>
    Task<INayaxSaleTimestampRepairTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The caller's stored sales for the given remote transaction identifiers, as raw persisted
    /// facts. A transaction this business does not hold is simply absent - it is a missing sale, and
    /// the use case reports it as one rather than creating it.
    /// </summary>
    Task<IReadOnlyList<StoredNayaxSale>> ListSalesByTransactionIdAsync(
        IReadOnlyCollection<long> transactionIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The caller's stored sales, of every status, whose stored instant falls in
    /// <c>[fromUtcInclusive, toUtcInclusive]</c>. This is how a sale no source covered is examined
    /// at all: the affected range comes from the evidence the operator obtained, and every stored
    /// sale inside it is reported - the ones a source verified and the ones it did not.
    /// </summary>
    Task<IReadOnlyList<StoredNayaxSale>> ListSalesInRangeAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcInclusive,
        CancellationToken cancellationToken);

    /// <summary>
    /// The caller's completed sales whose stored instant falls in
    /// <c>[fromUtcInclusive, toUtcExclusive)</c>, for the reconciliation's "before" position. The
    /// completed-sale predicate is the central one every sales query uses.
    /// </summary>
    Task<IReadOnlyList<StoredNayaxSale>> ListCompletedSalesAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken);

    /// <summary>The caller's product catalogue as match candidates, projected once per preview.</summary>
    Task<IReadOnlyList<ProductMatchCandidate>> GetProductCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Each named product's inventory-cost transition baseline cutoff. A product without a baseline
    /// is absent, exactly as the sales import and the latest-sales synchronization read it.
    /// </summary>
    Task<IReadOnlyDictionary<long, DateTime>> GetTransitionCutoffsAsync(
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken);

    /// <summary>Stages a new preview plan.</summary>
    void AddDraft(NewNayaxSaleTimestampRepairDraft draft);

    /// <summary>
    /// The caller's stored preview plan, loaded so it can be marked applied, or <c>null</c> when it
    /// does not exist or belongs to another business.
    /// </summary>
    Task<StoredNayaxSaleTimestampRepairDraft?> FindDraftAsync(Guid previewId, CancellationToken cancellationToken);

    /// <summary>Stages the applied time on a plan loaded by <see cref="FindDraftAsync"/>.</summary>
    void MarkDraftApplied(Guid previewId, DateTime appliedAt);

    /// <summary>
    /// Writes each named sale's repaired authorization instant and appends its audit row, inside the
    /// caller's transaction. Nothing else is written: no amount, status, product mapping, costing
    /// value or stock movement, and no sale is created or removed.
    /// </summary>
    /// <returns>The appended audit records, with their keys.</returns>
    Task<IReadOnlyList<NayaxSaleTimestampRepairRecord>> RepairAsync(
        NayaxSaleTimestampRepairApplication application,
        CancellationToken cancellationToken);

    /// <summary>Persists every staged change, including a costing replay's.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
