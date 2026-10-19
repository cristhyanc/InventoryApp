using Inventory.Domain.Providers;

namespace Inventory.Application.CatalogueReview;

/// <summary>
/// The database transaction one catalogue review operation runs its authoritative read and its write
/// in (issue #738). Disposing it without <see cref="CommitAsync"/> rolls back everything the
/// operation staged, which is what makes an apply atomic: a refused link leaves no linked row, no
/// created row, no renamed category and no field decision behind.
/// </summary>
public interface ICatalogueReviewTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>One of the business's products, as the review reads it.</summary>
public sealed record LocalCatalogueProduct(
    long ProductId,
    string Name,
    string? ProviderId,
    decimal UnitPrice,
    string? Unit,
    long? CategoryId);

/// <summary>One of the business's categories, as the review reads it.</summary>
public sealed record LocalCatalogueCategory(long CategoryId, string Name, string? ProviderId);

/// <summary>
/// The last provider value one product's negotiable field was decided about - the row that stops a
/// skipped value being proposed again until the provider changes it.
/// </summary>
public sealed record LastDecidedProviderField(
    long ProductId,
    NegotiableCatalogueField Field,
    string ProviderValue);

/// <summary>
/// Everything local the review compares the provider's catalogue against, read in one go.
///
/// <para><see cref="UnresolvedSaleCountByProviderProductId"/> counts the business's stored sales that
/// are still unresolved, carry that provider product id, and have known raw provenance (issue #758).
/// A provenance-unknown row is deliberately not counted: its stored number may be either a provider
/// id or the local product id the old synchronization wrote, so it is not evidence about anything.</para>
/// </summary>
public sealed record LocalCatalogueSnapshot(
    IReadOnlyList<LocalCatalogueProduct> Products,
    IReadOnlyList<LocalCatalogueCategory> Categories,
    IReadOnlyList<LastDecidedProviderField> LastDecided,
    IReadOnlyDictionary<string, int> UnresolvedSaleCountByProviderProductId);

/// <summary>
/// One provider entry as the review records it: the provider's side exactly as it was read, plus the
/// local row its id already resolves to, if any.
/// </summary>
public sealed record CatalogueReviewEntry(
    ProviderCatalogueEntryKind Kind,
    string ExternalId,
    string ProviderName,
    string? ProviderGroupExternalId,
    string? ProviderUnitPrice,
    string? ProviderUnitVolume,
    long? LinkedProductId,
    long? LinkedCategoryId,
    int UnresolvedSaleCount);

/// <summary>One stored review entry, with the key the apply's decisions name it by.</summary>
public sealed record PersistedCatalogueReviewItem(long ItemId, CatalogueReviewEntry Entry);

/// <summary>A stored review: when it was read, whether it has been applied, and its entries.</summary>
public sealed record PersistedCatalogueReview(
    long ReviewId,
    DateTime StartedAtUtc,
    DateTime? AppliedAtUtc,
    IReadOnlyList<PersistedCatalogueReviewItem> Items);

/// <summary>
/// One decision the apply carries out, already checked for shape by
/// <see cref="CatalogueReviewPolicy.DecisionRefusal"/>. The two field answers are <c>null</c> when
/// the operator said nothing about that field, which is not the same as keeping the local value: a
/// field nobody decided is left alone and is proposed again next time.
/// </summary>
public sealed record CatalogueReviewApplyDecision(
    long ItemId,
    CatalogueReviewAction Action,
    long? LocalRowId,
    ProviderFieldDecision? UnitPrice,
    ProviderFieldDecision? UnitVolume);

/// <summary>
/// Everything one apply writes, with every provider value already taken from the stored review rather
/// than from the request.
/// </summary>
public sealed record CatalogueReviewApplyPlan(
    PersistedCatalogueReview Review,
    DateTime AppliedAtUtc,
    string AppliedBy,
    IReadOnlyList<CatalogueReviewApplyDecision> Decisions);

/// <summary>
/// Narrow persistence port for the provider catalogue review (issue #738, parent #329 decision C1),
/// owned by the Application layer and implemented by
/// <c>Inventory.Infrastructure.Persistence.EfCatalogueReviewStore</c>.
///
/// <para>Reads and writes are scoped to the caller's business by the central <c>AppDbContext</c>
/// query filters and <c>BusinessOwnershipEnforcer</c> alone, never by a predicate of its own, and
/// every one of them refuses outright unless the business's sales provider is Nayax - the external id
/// columns hold <em>that</em> provider's ids and nothing in them says who issued them (issue #759).</para>
///
/// <para>It deliberately does not reuse the catalogue import's port. That one applies a whole
/// snapshot to the rows the provider already names; this one links rows the provider does not name
/// yet, on an operator's decision, and has to refuse a link the stored rows conflict with. Neither
/// is a special case of the other, and the direct import keeps working unchanged.</para>
/// </summary>
public interface ICatalogueReviewStore
{
    /// <summary>
    /// Begins the transaction a review's read and write share. For an apply it is opened after the
    /// provider has been read, so no provider request is ever made while a database transaction is
    /// held open.
    /// </summary>
    Task<ICatalogueReviewTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>The business's own catalogue, its last field decisions and its unresolved sale counts.</summary>
    Task<LocalCatalogueSnapshot> ReadLocalCatalogueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="entries"/> as the business's pending review, replacing an unapplied one
    /// if it has one, and answers it with the keys the database assigned. An <em>applied</em> review
    /// is never touched: it is the record of what was decided.
    /// </summary>
    Task<PersistedCatalogueReview> ReplacePendingReviewAsync(
        IReadOnlyList<CatalogueReviewEntry> entries,
        DateTime startedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// The business's review with that key, or <see langword="null"/> when it holds none - which is
    /// also the answer for another business's review id, because the read is tenant-filtered.
    /// </summary>
    Task<PersistedCatalogueReview?> FindReviewAsync(long reviewId, CancellationToken cancellationToken);

    /// <summary>
    /// Carries out <paramref name="plan"/> against the rows as they are <em>now</em>, inside the
    /// caller's transaction: it re-reads every local row it is about to write, applies
    /// <see cref="CatalogueReviewPolicy"/> to each link, writes the links, the provider-owned fields
    /// and the decided ones, records what was decided about, and marks the review applied.
    ///
    /// <para>The read, the comparison and the write are one operation, deliberately: a product that
    /// gained a provider id, or a category that took the one this plan names, between the review and
    /// the apply must refuse the apply rather than have the plan overwrite it.</para>
    ///
    /// <para>It touches no sale. A link makes previously unresolved sales <em>resolvable</em> and
    /// resolves none of them, and it never recosts anything (AGENTS.md § Inventory and historical
    /// costing invariants).</para>
    /// </summary>
    /// <exception cref="Inventory.Domain.Exceptions.DomainValidationException">
    /// A link the stored rows refuse. Nothing is staged, so the caller's transaction rolls back clean.
    /// </exception>
    Task<CatalogueReviewApplied> ApplyAsync(CatalogueReviewApplyPlan plan, CancellationToken cancellationToken);
}
