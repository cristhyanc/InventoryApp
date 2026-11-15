using Inventory.Application.MachineStockSync;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// The Sync Restock log double (issue #1240): it keeps every line the apply emitted so a test can
/// assert that a failure was recorded at all, with the event id that identifies it and the reason
/// the batch failed. A test that does not care about the log simply uses one and ignores it.
/// </summary>
public sealed class RecordingMachineStockSyncLog : IMachineStockSyncLog
{
    public List<(int EventId, string FailureType, string? Reason)> Failures { get; } = [];

    public void ApplyFailed(int eventId, string failureType, string? reason) =>
        Failures.Add((eventId, failureType, reason));
}
