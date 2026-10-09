using Inventory.Domain.Gst;

namespace Inventory.Application.Gst;

/// <summary>
/// The database transaction a historical GST classification Apply runs in. Disposing it without
/// <see cref="CommitAsync"/> rolls back every change the apply saved through the store, which is
/// what makes the maintenance action all-or-nothing.
/// </summary>
public interface IHistoricalGstClassificationTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Narrow persistence port for the historical GST classification maintenance workflow (issue #433),
/// owned by the Application layer.
///
/// It reads and writes through the caller's business scope - the <c>AppDbContext</c> tenant query
/// filters and the ownership stamp on save - so it carries no business predicate of its own and
/// cannot reach another business's purchase. It applies no classification rule either:
/// <see cref="HistoricalGstClassificationPolicy"/> decides what each component becomes, and the
/// adapter only projects stored rows and persists the states it is handed.
///
/// <see cref="LoadPurchasesAsync"/> deliberately returns the whole purchase history rather than the
/// unclassified components alone. Eligibility is a Domain decision, and the already-classified
/// components are what let <see cref="HistoricalGstClassificationFingerprint"/> notice relevant
/// purchase data changing between a preview and its apply.
/// </summary>
public interface IHistoricalGstClassificationStore
{
    /// <summary>Begins the transaction an apply reads and writes in.</summary>
    Task<IHistoricalGstClassificationTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every purchase of the caller's business with each component's stored GST state and the
    /// configured product rule and supplier defaults that could classify it.
    /// </summary>
    Task<IReadOnlyList<HistoricalGstPurchase>> LoadPurchasesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Persists exactly <paramref name="changes"/> inside the caller's transaction, setting each
    /// named component's classification and provenance and touching nothing else - no amount, no
    /// unit cost, no costing quantity, no inventory value and no stock movement.
    /// </summary>
    /// <returns>How many stored components were written.</returns>
    Task<int> ApplyAsync(
        IReadOnlyCollection<HistoricalGstClassificationChange> changes,
        CancellationToken cancellationToken);
}
