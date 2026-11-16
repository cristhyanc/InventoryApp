using Inventory.Application.MachineStockSync;
using Inventory.Infrastructure.MachineStockSync;
using InventoryApi.Tests.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.MachineStockSync;

/// <summary>
/// The one place <see cref="IMachineStockSyncLog"/>'s events become log lines (issue #1240).
///
/// <para>What matters here is that a failed Sync Restock apply is findable afterwards: it is a
/// <see cref="LogLevel.Warning"/> - an expected outcome an operator may ask about, not a server
/// error - and it names the event id and the failure code as structured properties, so Azure
/// Monitor exports them as queryable dimensions rather than only as a sentence.</para>
///
/// <para>What <em>cannot</em> be logged is structural rather than a matter of care: the port has no
/// method with a parameter that could carry an exception, a Nayax credential, a machine number or a
/// raw alert (AGENTS.md § Nayax integration). The one free-text value is the cost replay's own
/// refusal, which is the sentence the operator is already being shown.</para>
/// </summary>
public class MachineStockSyncLoggingTests
{
    [Fact]
    public void A_failed_apply_is_one_warning_naming_the_event_the_failure_and_the_reason()
    {
        const string Reason = "Restock adjustment 12 for product Coke 375mL (7) has no valid unit cost.";
        var logger = new CapturingLogger<LoggingMachineStockSyncLog>();

        new LoggingMachineStockSyncLog(logger)
            .ApplyFailed(44, nameof(MachineRefillFailure), Reason);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("44", entry.Properties["StockEventId"]);
        Assert.Equal(nameof(MachineRefillFailure), entry.Properties["FailureType"]);
        Assert.Equal(Reason, entry.Properties["FailureReason"]);
        Assert.Contains(Reason, entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_failure_with_no_caller_safe_reason_still_leaves_a_line_that_says_so(string? reason)
    {
        var logger = new CapturingLogger<LoggingMachineStockSyncLog>();

        new LoggingMachineStockSyncLog(logger).ApplyFailed(44, "SqliteException", reason);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("44", entry.Properties["StockEventId"]);
        Assert.Equal("SqliteException", entry.Properties["FailureType"]);
        Assert.Equal(LoggingMachineStockSyncLog.NoReason, entry.Properties["FailureReason"]);
    }
}
