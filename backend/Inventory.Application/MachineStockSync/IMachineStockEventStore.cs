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
/// The outcome of the store's single-event refill application. A failed application has recorded
/// nothing at all: neither the inventory movement nor the event's processed state.
/// </summary>
public sealed record MachineRefillApplication(bool Succeeded, int? StockAdjustmentId);

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

    Task<NayaxStockSyncProduct?> FindStorageProductAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// Records the refill movement and the event's processed state together, in one transaction, so
    /// an event can never be left marked processed without its inventory movement. The refill is a
    /// machine transfer out of storage: it reduces storage quantity and must not change costing
    /// quantity/value or create COGS. <paramref name="duplicateResolution"/> is
    /// <see cref="NayaxDuplicateResolution.None"/> for an ordinary (non-duplicate) apply, or
    /// <see cref="NayaxDuplicateResolution.AppliedAsSeparateRestock"/> when the operator explicitly
    /// overrode a possible-duplicate warning (issue #196); in that case
    /// <paramref name="matchedManualStockAdjustmentId"/> is the manual refill the override was
    /// resolved against, persisted alongside the movement for auditability. The event is found by
    /// the machine's Nayax number and the movement records its local id (issue #741).
    /// </summary>
    Task<MachineRefillApplication> ApplyRefillAsync(
        int eventId,
        Machines.MachineIdentity machine,
        long nayaxEventLogId,
        long productId,
        int quantity,
        NayaxDuplicateResolution duplicateResolution,
        int? matchedManualStockAdjustmentId,
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
