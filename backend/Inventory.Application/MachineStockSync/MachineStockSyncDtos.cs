using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// One Nayax machine-stock event as shown in the Sync Restock reconciliation preview (issue #183).
///
/// <para>When <see cref="IsPossibleDuplicate"/> is set, the three duplicate-comparison values
/// describe the manual refill the rule chose (issue #1244): <see cref="PossibleDuplicateQuantity"/>
/// is how many units that movement put into the machine - the opposite sign of its own storage
/// change, so a manual refill of twenty reads as <c>20</c> - <see cref="PossibleDuplicateEffectiveAt"/>
/// is when it was effective, and <see cref="OtherPossibleDuplicateCount"/> is how many other manual
/// refills also qualified. They let the operator compare the event's own
/// <see cref="ParsedQuantity"/> against what was recorded by hand instead of being told only that
/// the two look alike.</para>
/// </summary>
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
    NayaxDuplicateResolution DuplicateResolution,
    int? PossibleDuplicateQuantity,
    DateTime? PossibleDuplicateEffectiveAt,
    int OtherPossibleDuplicateCount,
    int EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
    NayaxProductReplacementPreviewDto? ProductReplacement = null,
    bool IsPossibleReplacementDoubleCount = false,
    string? ReplacementDoubleCountNotes = null
);

/// <summary>
/// What a Nayax product replacement (Event 510, issue #1325) reported and what the app suggests for
/// it. <see cref="NayaxStockEventPreviewDto.ProductReplacement"/> carries it only for a 510 event
/// whose EventData parsed; an unparsed one is a Needs Review row with its reason instead.
///
/// <para>Every suggestion here is advice only, recomputed from the live machine read on every load,
/// and never stored: a suggestion that has no answer carries its reason instead.
/// <see cref="SuggestedIncomingQuantity"/> is the units Nayax currently shows in the MDB
/// (<c>PAR - MissingStockByMDB</c>); it is not the slot's capacity and can read low by the sales
/// since the replacement.</para>
/// </summary>
public record NayaxProductReplacementPreviewDto(
    string OutgoingProductName,
    string IncomingProductName,
    long? SuggestedOutgoingProductId,
    string? SuggestedOutgoingProductName,
    NayaxReplacementSuggestionSource OutgoingSuggestionSource,
    string? OutgoingSuggestionReason,
    long? SuggestedIncomingProductId,
    string? SuggestedIncomingProductName,
    NayaxReplacementSuggestionSource IncomingSuggestionSource,
    string? IncomingSuggestionReason,
    int? SuggestedIncomingQuantity,
    string? SuggestedIncomingQuantityReason
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

/// <summary>
/// The Sync Restock working list for one machine. <see cref="HiddenReconciledCount"/> is how many
/// reconciled-manually events within the current From date window are not in <see cref="Events"/>
/// because Show reconciled is off (issue #206); it is zero when Show reconciled is on.
/// </summary>
public record NayaxMachineStockSyncPreviewDto(
    long MachineId,
    int NewEventCount,
    IReadOnlyList<NayaxStockEventPreviewDto> Events,
    IReadOnlyList<NayaxProductImpactPreviewDto> ProductImpacts,
    int HiddenReconciledCount,
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
    DuplicateRequiresResolution,

    /// <summary>
    /// The event was already settled the <em>other</em> way, so this resolution changed nothing
    /// (issue #1248): an event that has been applied cannot also be recorded as never applied, and
    /// one reconciled as already recorded manually cannot also be applied as a separate restock.
    /// <c>POST /api/machines/{id}/sync-restock/resolve-duplicate</c> answers it with <c>409</c>.
    /// Repeating the <em>same</em> resolution is not this outcome: that is the established
    /// idempotent replay and still answers <see cref="Reconciled"/> or <see cref="Applied"/>.
    /// </summary>
    ResolutionConflict
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

/// <summary>
/// An operator's confirmed product replacement, for one 510 event (issue #1326): the product loaded
/// into the slot and how many units of it, and what happened to the units taken out.
///
/// <para><see cref="IncomingQuantity"/> is whole and may be <c>0</c> - the replacement happened but
/// nothing was loaded - while <see cref="IncomingProductId"/> is always required, because the
/// replacement records which product the slot now holds. <see cref="OutgoingProductId"/> and
/// <see cref="OutgoingQuantity"/> (whole, at least <c>1</c>) are required for every
/// <see cref="OutgoingDisposition"/> but <c>None</c>, and must be absent for that one;
/// <see cref="Inventory.Domain.Nayax.NayaxProductReplacementWritePolicy"/> is the one rule and
/// refuses anything else before a lock is taken or a row is read.</para>
/// </summary>
public record NayaxApplyProductReplacementRequestDto(
    int EventId,
    long IncomingProductId,
    int IncomingQuantity,
    NayaxReplacementOutgoingDisposition OutgoingDisposition,
    long? OutgoingProductId = null,
    int? OutgoingQuantity = null
);

/// <summary>
/// An operator's explicit bulk request to resolve every listed Sync Restock event as "already
/// recorded manually" in one action (issue #242). Unlike <see cref="NayaxResolveDuplicateRequestDto"/>,
/// this is never limited to events the app flagged as a possible duplicate - that flag is only a
/// suggestion for human resolution, not a precondition.
/// </summary>
public record NayaxResolveManyAsAlreadyRecordedRequestDto(IReadOnlyList<int> EventIds);
