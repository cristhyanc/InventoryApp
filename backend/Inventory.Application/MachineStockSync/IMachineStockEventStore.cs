using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// One Nayax alert to persist as an imported fact, already parsed and resolved by the use case.
/// The raw EventData and source metadata are carried verbatim so the import stays auditable.
/// </summary>
public sealed record MachineStockEventImport(
    long NayaxEventId,
    long MachineId,
    int EventCode,
    DateTime EventTimestamp,
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
    long NayaxEventId,
    long MachineId,
    DateTime EventTimestamp,
    string RawEventData,
    int? ParsedMdb,
    string? ParsedProductName,
    int? ParsedQuantity,
    long? MatchedProductId,
    string? MatchedProductName,
    int? MatchedProductQuantityInStock,
    NayaxStockEventMatchStatus MatchStatus,
    string? NeedsReviewReason,
    NayaxStockEventProcessingStatus ProcessingStatus);

/// <summary>The stored state one apply attempt needs in order to decide what to do.</summary>
public sealed record MachineStockEventState(
    int Id,
    long NayaxEventId,
    NayaxStockEventMatchStatus MatchStatus,
    string? NeedsReviewReason,
    NayaxStockEventProcessingStatus ProcessingStatus,
    long? MatchedProductId,
    int? ParsedQuantity,
    int? StockAdjustmentId);

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
    /// <summary>The Nayax event ids already imported for this machine; the basis of idempotency.</summary>
    Task<IReadOnlyList<long>> GetImportedNayaxEventIdsAsync(long machineId, CancellationToken cancellationToken);

    /// <summary>The local products for the given Nayax product identifiers, keyed by that identifier.</summary>
    Task<IReadOnlyDictionary<long, NayaxStockSyncProduct>> GetStorageProductsAsync(
        IReadOnlyCollection<long> nayaxProductIds, CancellationToken cancellationToken);

    Task ImportAsync(IReadOnlyList<MachineStockEventImport> events, CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingMachineStockEvent>> GetUnprocessedEventsAsync(
        long machineId, CancellationToken cancellationToken);

    /// <summary>Manual (operator-entered) machine refills for this machine, as duplicate evidence.</summary>
    Task<IReadOnlyList<ManualRefillEvidence>> GetManualMachineRefillsAsync(
        long machineId, CancellationToken cancellationToken);

    Task<MachineStockEventState?> FindEventAsync(long machineId, int eventId, CancellationToken cancellationToken);

    Task<NayaxStockSyncProduct?> FindStorageProductAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// Records the refill movement and the event's processed state together, in one transaction, so
    /// an event can never be left marked processed without its inventory movement. The refill is a
    /// machine transfer out of storage: it reduces storage quantity and must not change costing
    /// quantity/value or create COGS.
    /// </summary>
    Task<MachineRefillApplication> ApplyRefillAsync(
        int eventId,
        long machineId,
        long nayaxEventId,
        long productId,
        int quantity,
        CancellationToken cancellationToken);
}
