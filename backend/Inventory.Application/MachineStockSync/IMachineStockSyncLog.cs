namespace Inventory.Application.MachineStockSync;

/// <summary>
/// What the Sync Restock apply says about itself (issue #1240), as a narrow Application port
/// implemented over <c>ILogger</c> in <c>Inventory.Infrastructure.MachineStockSync</c> - the same
/// arrangement <c>INayaxSqsReaderLog</c> and <c>INayaxSalesReconciliationLog</c> have, and for the
/// same reason: the Application layer declares the fixed set of events and takes no logging
/// dependency.
///
/// <para>It exists because a failed apply used to be silent. The operator was told
/// "Failed to apply this event; no inventory movement was recorded." and nothing was written
/// anywhere, so a support question about a machine whose restock never lands had no record to start
/// from. Every failed apply now leaves one line.</para>
/// </summary>
public interface IMachineStockSyncLog
{
    /// <summary>
    /// One accepted event whose write was staged and then rolled back, with the event id that
    /// identifies it and the reason the batch failed. <paramref name="failureType"/> is the short
    /// code of <see cref="MachineRefillFailure.FailureType"/>; <paramref name="reason"/> is the
    /// caller-safe refusal when there is one (today, only a cost data-quality refusal) and
    /// <see langword="null"/> otherwise, so no provider exception's own text can reach a sink
    /// through this port.
    /// </summary>
    void ApplyFailed(int eventId, string failureType, string? reason);
}
