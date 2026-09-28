using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>One Nayax machine-stock event as shown in the Sync Restock reconciliation preview (issue #183).</summary>
public record NayaxStockEventPreviewDto(
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
    NayaxStockEventMatchStatus MatchStatus,
    string? NeedsReviewReason,
    NayaxStockEventProcessingStatus ProcessingStatus,
    int? AvailableStorageQuantity,
    bool IsInsufficientStock,
    int? UnaccountedDifference,
    bool IsDiscrepancy,
    bool IsPossibleDuplicate,
    string? PossibleDuplicateNotes,
    NayaxDuplicateResolution DuplicateResolution
);

/// <summary>
/// The combined storage impact of every validated (Matched, sufficiently stocked) positive event
/// for one product across however many MDBs it occupies on the machine (issue #183).
/// </summary>
public record NayaxProductImpactPreviewDto(
    long ProductId,
    string ProductName,
    int AvailableStorageQuantity,
    int PendingRefillQuantity
);

public record NayaxMachineStockSyncPreviewDto(
    long MachineId,
    int NewEventCount,
    IReadOnlyList<NayaxStockEventPreviewDto> Events,
    IReadOnlyList<NayaxProductImpactPreviewDto> ProductImpacts,
    string? Message
);

public record NayaxStockEventApplyRequestDto(IReadOnlyList<int> EventIds);

public enum NayaxStockEventApplyOutcome
{
    Applied,
    InsufficientStock,
    NotMatched,
    NotApplicable,
    Error,

    /// <summary>Reconciled as already recorded manually (issue #196): no movement was applied.</summary>
    Reconciled,

    /// <summary>
    /// Flagged as a possible duplicate of a manual refill and not yet explicitly resolved
    /// (issue #196); the ordinary Apply action refused it.
    /// </summary>
    DuplicateRequiresResolution
}

public record NayaxStockEventApplyResultDto(
    int EventId,
    NayaxStockEventApplyOutcome Outcome,
    string Message,
    int? StockAdjustmentId
);

public record NayaxMachineStockApplyResponseDto(IReadOnlyList<NayaxStockEventApplyResultDto> Results);

/// <summary>
/// An operator's explicit resolution of one Nayax event flagged as a possible duplicate of a manual
/// refill (issue #196): <c>AlreadyRecordedManually</c> reconciles it without any movement,
/// <c>ApplyAsSeparateRestock</c> explicitly overrides the warning and applies it once.
/// </summary>
public record NayaxResolveDuplicateRequestDto(int EventId, NayaxDuplicateResolutionChoice Resolution);
