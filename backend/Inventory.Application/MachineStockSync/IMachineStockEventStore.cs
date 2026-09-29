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
public interface IMachineStockEventStore
{
    /// <summary>The Nayax <c>EventLogID</c>s already imported for this machine; the basis of idempotency.</summary>
    Task<IReadOnlyList<long>> GetImportedNayaxEventLogIdsAsync(long machineId, CancellationToken cancellationToken);

    /// <summary>The local products for the given Nayax product identifiers, keyed by that identifier.</summary>
    Task<IReadOnlyDictionary<long, NayaxStockSyncProduct>> GetStorageProductsAsync(
        IReadOnlyCollection<long> nayaxProductIds, CancellationToken cancellationToken);

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
        long machineId,
        CancellationToken cancellationToken,
        DateTime? fromDateGmt = null,
        bool includeReconciled = false);

    /// <summary>Manual (operator-entered) machine refills for this machine, as duplicate evidence.</summary>
    Task<IReadOnlyList<ManualRefillEvidence>> GetManualMachineRefillsAsync(
        long machineId, CancellationToken cancellationToken);

    Task<MachineStockEventState?> FindEventAsync(long machineId, int eventId, CancellationToken cancellationToken);

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
    /// resolved against, persisted alongside the movement for auditability.
    /// </summary>
    Task<MachineRefillApplication> ApplyRefillAsync(
        int eventId,
        long machineId,
        long nayaxEventLogId,
        long productId,
        int quantity,
        NayaxDuplicateResolution duplicateResolution,
        int? matchedManualStockAdjustmentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a flagged possible duplicate as "already recorded manually" (issue #196): marks it
    /// reconciled against <paramref name="matchedManualStockAdjustmentId"/> without creating a
    /// Nayax-sourced movement or changing storage. Idempotent: resolving an event already reconciled
    /// this way changes nothing. Returns <c>false</c> only when the event does not exist for this
    /// machine.
    /// </summary>
    Task<bool> ReconcileAsManualDuplicateAsync(
        int eventId, long machineId, int matchedManualStockAdjustmentId, CancellationToken cancellationToken);
}
