using Inventory.Domain.MachineStock;

namespace Inventory.Application.MachineStock;

/// <summary>
/// One product's row in a machine's stock reconciliation (issue #56). Every nullable quantity here
/// is nullable on purpose: an unknown expected or observed stock is reported as unknown, with the
/// <see cref="DataQuality"/> state that says why, and is never presented as zero.
/// </summary>
public record MachineStockProductReconciliationDto(
    long ProductId,
    string ProductName,
    int? ExpectedQuantity,
    int? ObservedQuantity,
    int? DiscrepancyQuantity,
    int? ShrinkageQuantity,
    int? OverageQuantity,
    MachineStockReconciliationStatus Status,
    MachineStockBaselineSource BaselineSource,
    DateTime? BaselineAtUtc,
    int? BaselineQuantity,
    int RefilledQuantity,
    int ReturnedToStorageQuantity,
    int SoldQuantity,
    int? UnexplainedChangeAtLastCount,
    IReadOnlyList<MachineStockDataQualityIssue> DataQuality);

/// <summary>
/// One machine's complete stock reconciliation. <see cref="ObservedAtUtc"/> is the instant the
/// machine's own stock report was read, and is <c>null</c> when it could not be read at all - in
/// which case <see cref="DataQuality"/> carries
/// <see cref="MachineStockDataQualityIssueCodes.ObservationSourceUnavailable"/> and every row's
/// observed quantity is unknown while its expected quantity is still reported.
/// </summary>
public record MachineStockReconciliationDto(
    long MachineId,
    DateTime AsOfUtc,
    DateTime? ObservedAtUtc,
    IReadOnlyList<MachineStockProductReconciliationDto> Products,
    IReadOnlyList<MachineStockDataQualityIssue> DataQuality);

/// <summary>
/// An operator's physical count of one product in one machine. The instant is the server's, not the
/// request's: a count is recorded when it is submitted, and this contract deliberately carries no
/// back-dated instant, no expected quantity and no discrepancy - the server derives every one of
/// those from the ledger.
/// </summary>
public record MachineStockCountRequestDto(long ProductId, int CountedQuantity, string? Notes);

/// <summary>The appended count, exactly as it was persisted.</summary>
public record MachineStockCountResultDto(
    int CountId,
    long MachineId,
    long ProductId,
    string ProductName,
    int CountedQuantity,
    DateTime CountedAtUtc,
    string? Notes);
