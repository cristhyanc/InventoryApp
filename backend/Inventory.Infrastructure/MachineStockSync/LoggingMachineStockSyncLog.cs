using Inventory.Application.MachineStockSync;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.MachineStockSync;

/// <summary>
/// <see cref="IMachineStockSyncLog"/> over <c>ILogger</c> (issue #1240) - the arrangement
/// <c>LoggingNayaxSqsReaderLog</c> and <c>LoggingNayaxSalesReconciliationLog</c> have: the
/// Application layer declares the fixed set of events, and this is the one place they become log
/// lines.
///
/// <para><strong>What cannot be logged is not a parameter above.</strong> There is no overload here
/// that accepts an exception, a Nayax credential, a machine number, a raw alert or a quantity, so
/// none of them can reach a sink by anybody forgetting to withhold it (AGENTS.md § Nayax
/// integration). The line carries the event's own id, a short failure code and - only for the cost
/// data-quality refusal, whose text is the replay's own product-named sentence - the reason the
/// operator is already being shown.</para>
///
/// <para>Structured names rather than interpolation, so the fields stay queryable in Azure
/// Monitor.</para>
/// </summary>
public sealed class LoggingMachineStockSyncLog : IMachineStockSyncLog
{
    /// <summary>What the line says when the failure published no caller-safe reason.</summary>
    public const string NoReason = "No caller-safe reason is published for this failure.";

    private readonly ILogger<LoggingMachineStockSyncLog> _logger;

    public LoggingMachineStockSyncLog(ILogger<LoggingMachineStockSyncLog> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void ApplyFailed(int eventId, string failureType, string? reason) =>
        _logger.LogWarning(
            "Sync Restock did not record machine stock event {StockEventId}: the write was rolled "
                + "back on {FailureType} and nothing was committed. {FailureReason}",
            eventId,
            failureType,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason);
}
