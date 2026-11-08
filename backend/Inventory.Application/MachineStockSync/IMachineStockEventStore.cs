using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// One Nayax alert to persist as an imported fact, already parsed and resolved by the use case.
/// <see cref="NayaxEventLogId"/> is the alert's documented <c>EventLogID</c>,
/// <see cref="EventDateTimeGmt"/> its <c>EventDateTimeGMT</c> (UTC) and
/// <see cref="EventDateTimeVmc"/> its machine-clock <c>EventDateTimeVMC</c>. The raw EventData is
/// carried verbatim and <see cref="RawSourceMetadata"/> holds the complete source alert as JSON in
/// Nayax's documented field names, so the import stays auditable.
/// </summary>
public sealed record MachineStockEventImport(
    long NayaxEventLogId,
    long MachineId,
    int EventCode,
    DateTime EventDateTimeGmt,
    DateTime EventDateTimeVmc,
    string RawEventData,
    string? RawSourceMetadata,
    int? ParsedMdb,
    string? ParsedProductName,
    int? ParsedQuantity,
    long? MatchedProductId,
    NayaxStockEventMatchStatus MatchStatus,
    string? NeedsReviewReason);

/// <summary>An imported event that has not been applied yet, with its matched product's current storage.</summary>
public sealed record PendingMachineStockEvent(
    int Id,
    long NayaxEventLogId,
    long MachineId,
    DateTime EventDateTimeGmt,
    DateTime? EventDateTimeVmc,
    string RawEventData,
    int? ParsedMdb,
    string? ParsedProductName,
    int? ParsedQuantity,
    long? MatchedProductId,
    string? MatchedProductName,
    int? MatchedProductQuantityInStock,
    NayaxStockEventMatchStatus MatchStatus,
    string? NeedsReviewReason,
    NayaxStockEventProcessingStatus ProcessingStatus,
    NayaxDuplicateResolution DuplicateResolution);

/// <summary>
/// One page of the Sync Restock working list (issue #206): the currently-unprocessed events that
/// satisfy the operator's From date / Show reconciled filters, plus how many reconciled-manually
/// events in that same date window are hidden because Show reconciled is off.
/// </summary>
public sealed record MachineStockEventsPage(
    IReadOnlyList<PendingMachineStockEvent> Events, int HiddenReconciledCount);

/// <summary>The stored state one apply attempt needs in order to decide what to do.</summary>
public sealed record MachineStockEventState(
    int Id,
    long NayaxEventLogId,
    DateTime EventDateTimeGmt,
    NayaxStockEventMatchStatus MatchStatus,
    string? NeedsReviewReason,
    NayaxStockEventProcessingStatus ProcessingStatus,
    long? MatchedProductId,
    int? ParsedQuantity,
    int? StockAdjustmentId,
    NayaxDuplicateResolution DuplicateResolution);

/// <summary>
/// One accepted event's refill, as the use case decided it: the event to mark processed, the
/// movement's quantity, and - for the explicit duplicate override of issue #196 - the resolution to
/// stamp and the manual refill it was resolved against.
/// </summary>
public sealed record MachineStockRefillRequest(
    int EventId,
    long NayaxEventLogId,
    int Quantity,
    NayaxDuplicateResolution DuplicateResolution,
    int? MatchedManualStockAdjustmentId);

/// <summary>One recorded refill: the event it applied and the movement that was written for it.</summary>
public sealed record AppliedMachineRefill(int EventId, int StockAdjustmentId);

/// <summary>
/// The outcome of one product's batch of refills. A failed batch has recorded nothing at all:
/// neither any inventory movement nor any event's processed state, and
/// <see cref="Applied"/> is empty.
/// </summary>
public sealed record MachineRefillApplication(bool Succeeded, IReadOnlyList<AppliedMachineRefill> Applied);

/// <summary>
/// The write transaction one product's batch of refills runs in (issue #1127). Disposing it without
/// <see cref="CommitAsync"/> rolls back everything written through the connection it shares since it
/// began - every movement of that product's batch, every event state it stamped, and the quantities
/// and costs the product's single rebuild touched. Disposal also drops whatever the batch left
/// tracked in the request's unit of work, so the history one product's rebuild loaded does not make
/// the next product's saves slower.
///
/// <para>On SQLite it is a <c>BEGIN IMMEDIATE</c> transaction, so the write lock is taken when the
/// transaction opens rather than at its first write. That is what makes the reads inside it
/// authoritative: the event's own eligibility and the product's storage quantity both decide a
/// write, and a deferred transaction would let two callers read the same answer and both act on it.
/// It is the same shape and the same reason as
/// <c>IPickListRefillStore.BeginWriteTransactionAsync</c> (issue #743).</para>
/// </summary>
public interface IMachineStockRefillWriteTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Narrow persistence port for the machine Sync Restock workflow (issue #183), owned by the
/// Application layer. Everything transactional, relational, or costing-specific lives behind it, so
/// the use cases stay free of EF Core.
/// </summary>
///
/// <para><strong>Two machine ids, never confused</strong> (issue #741). The imported events are the
/// provider's own rows and keep the provider's machine number, so every event read and write takes
/// <c>nayaxMachineId</c>. The stock movements are this application's, and since #741
/// <c>StockAdjustment.MachineId</c> holds the local machine, so the manual-refill evidence takes
/// <c>localMachineId</c> and an applied refill takes the whole
/// <see cref="Inventory.Application.Machines.MachineIdentity"/>: it finds the event by the one and
/// records the movement against the other.</para>
public interface IMachineStockEventStore
{
    /// <summary>The Nayax <c>EventLogID</c>s already imported for this machine; the basis of idempotency.</summary>
    Task<IReadOnlyList<long>> GetImportedNayaxEventLogIdsAsync(long nayaxMachineId, CancellationToken cancellationToken);

    /// <summary>
    /// The local products with the given local product ids, keyed by that id. Since issue #733 the
    /// caller resolves a Nayax product id to a local one through
    /// <see cref="Inventory.Application.Nayax.INayaxProductLinkStore"/> first: a remote identifier
    /// never reaches a persistence port as if it were a local key.
    /// </summary>
    Task<IReadOnlyDictionary<long, NayaxStockSyncProduct>> GetStorageProductsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    Task ImportAsync(IReadOnlyList<MachineStockEventImport> events, CancellationToken cancellationToken);

    /// <summary>
    /// The machine's currently-unprocessed events, oldest first (issue #206). <paramref
    /// name="fromDateGmt"/>, when given, is a lower bound on the canonical
    /// <see cref="PendingMachineStockEvent.EventDateTimeGmt"/> - the same event timestamp the
    /// stock-sync contract already defines, never a second interpretation. <paramref
    /// name="includeReconciled"/> controls whether events already resolved as "reconciled: already
    /// recorded manually" are included; when it is <c>false</c> they are excluded from the returned
    /// events but still counted in <see cref="MachineStockEventsPage.HiddenReconciledCount"/>.
    /// Neither parameter changes any persisted event: both are presentation/workflow filters over
    /// the same imported audit facts.
    /// </summary>
    Task<MachineStockEventsPage> GetUnprocessedEventsAsync(
        long nayaxMachineId,
        CancellationToken cancellationToken,
        DateTime? fromDateGmt = null,
        bool includeReconciled = false);

    /// <summary>
    /// Manual (operator-entered) machine refills for this machine, as duplicate evidence. A stock
    /// movement names the local machine (issue #741), so this takes the local id.
    /// </summary>
    Task<IReadOnlyList<ManualRefillEvidence>> GetManualMachineRefillsAsync(
        long localMachineId, CancellationToken cancellationToken);

    Task<MachineStockEventState?> FindEventAsync(long nayaxMachineId, int eventId, CancellationToken cancellationToken);

    /// <summary>
    /// The stored state of each of <paramref name="eventIds"/> that exists for this machine, in no
    /// particular order and with no entry at all for an id that does not (issue #1127). It is the
    /// same machine- and tenant-scoped read as <see cref="FindEventAsync"/>, asked once for a whole
    /// request instead of once per event.
    /// </summary>
    Task<IReadOnlyList<MachineStockEventState>> FindEventsAsync(
        long nayaxMachineId, IReadOnlyCollection<int> eventIds, CancellationToken cancellationToken);

    Task<NayaxStockSyncProduct?> FindStorageProductAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the write transaction one product's batch of refills runs in (issue #1127). Every read
    /// that decides those writes - the events' own stored state and the product's storage quantity -
    /// must happen inside it.
    /// </summary>
    Task<IMachineStockRefillWriteTransaction> BeginRefillWriteTransactionAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Records every refill of one product and each event's processed state together, so an event
    /// can never be left marked processed without its inventory movement, and rebuilds that
    /// product's cost exactly <strong>once</strong> for the whole batch (issue #1127). Each refill
    /// is still its own <c>MachineRefill</c> movement with its own note and its own
    /// <c>StockAdjustmentId</c> on the event row, so every event stays individually auditable.
    ///
    /// <para>The refill is a machine transfer out of storage: it reduces storage quantity and must
    /// not change costing quantity/value or create COGS.
    /// <see cref="MachineStockRefillRequest.DuplicateResolution"/> is
    /// <see cref="NayaxDuplicateResolution.None"/> for an ordinary (non-duplicate) apply, or
    /// <see cref="NayaxDuplicateResolution.AppliedAsSeparateRestock"/> when the operator explicitly
    /// overrode a possible-duplicate warning (issue #196); in that case
    /// <see cref="MachineStockRefillRequest.MatchedManualStockAdjustmentId"/> is the manual refill
    /// the override was resolved against, persisted alongside the movement for auditability. The
    /// events are found by the machine's Nayax number and the movements record its local id
    /// (issue #741).</para>
    ///
    /// <para>It saves inside the caller's transaction and deliberately does <b>not</b> commit: the
    /// caller opened the transaction and owns the commit, so a failure here - a movement that cannot
    /// be recorded or a fatal costing data-quality issue - is answered with
    /// <c>Succeeded: false</c> and the caller's disposal discards every write of that product's
    /// batch.</para>
    /// </summary>
    Task<MachineRefillApplication> ApplyProductRefillsAsync(
        Machines.MachineIdentity machine,
        long productId,
        IReadOnlyList<MachineStockRefillRequest> refills,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an event as "already recorded manually" (issue #196; extended by issue #242 to any
    /// otherwise-eligible unresolved event, not only one flagged as a possible duplicate): marks it
    /// reconciled against <paramref name="matchedManualStockAdjustmentId"/> - the matching manual
    /// refill's <c>StockAdjustment.Id</c> when the app found one, or <c>null</c> when the operator is
    /// explicitly resolving an event the app did not flag - without creating a Nayax-sourced movement
    /// or changing storage. Idempotent: resolving an event already reconciled this way changes
    /// nothing. Returns <c>false</c> only when the event does not exist for this machine.
    /// </summary>
    Task<bool> ReconcileAsManualDuplicateAsync(
        int eventId, long nayaxMachineId, int? matchedManualStockAdjustmentId, CancellationToken cancellationToken);
}
