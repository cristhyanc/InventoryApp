using Inventory.Application.PlatformDiagnostics;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformDiagnostics;

/// <summary>
/// The hard maxima, and the one property that makes them hard: nothing can raise them (issue #336).
/// </summary>
public class PlatformDiagnosticsQueryLimitsTests
{
    [Fact]
    public void The_shipped_limits_are_the_values_the_issue_specifies()
    {
        Assert.Equal(16 * 1024, PlatformDiagnosticsQueryLimits.MaxSqlBytes);
        Assert.Equal(500, PlatformDiagnosticsQueryLimits.HardMaxRows);
        Assert.Equal(1_048_576, PlatformDiagnosticsQueryLimits.HardMaxResponseBytes);
        Assert.Equal(TimeSpan.FromSeconds(5), PlatformDiagnosticsQueryLimits.HardMaxDuration);
    }

    [Fact]
    public void The_default_limits_are_the_hard_maxima()
    {
        var limits = PlatformDiagnosticsQueryLimits.Default;

        Assert.Equal(PlatformDiagnosticsQueryLimits.HardMaxRows, limits.MaxRows);
        Assert.Equal(PlatformDiagnosticsQueryLimits.HardMaxResponseBytes, limits.MaxResponseBytes);
        Assert.Equal(PlatformDiagnosticsQueryLimits.HardMaxDuration, limits.MaxDuration);
    }

    [Fact]
    public void Asking_for_more_than_the_hard_maximum_gets_the_hard_maximum()
    {
        var limits = PlatformDiagnosticsQueryLimits.Create(
            maxRows: 1_000_000,
            maxResponseBytes: 512 * 1024 * 1024,
            maxDuration: TimeSpan.FromHours(1));

        Assert.Equal(PlatformDiagnosticsQueryLimits.HardMaxRows, limits.MaxRows);
        Assert.Equal(PlatformDiagnosticsQueryLimits.HardMaxResponseBytes, limits.MaxResponseBytes);
        Assert.Equal(PlatformDiagnosticsQueryLimits.HardMaxDuration, limits.MaxDuration);
    }

    [Fact]
    public void Tighter_limits_are_honoured_because_the_test_suite_and_an_operator_may_only_tighten()
    {
        var limits = PlatformDiagnosticsQueryLimits.Create(
            maxRows: 10,
            maxResponseBytes: 8192,
            maxDuration: TimeSpan.FromMilliseconds(250));

        Assert.Equal(10, limits.MaxRows);
        Assert.Equal(8192, limits.MaxResponseBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(250), limits.MaxDuration);
    }

    /// <summary>
    /// A zero or negative limit would be a query that can never return anything, which is a
    /// configuration mistake rather than a tighter policy, so it is floored instead of honoured.
    /// </summary>
    [Fact]
    public void Zero_and_negative_limits_are_floored_rather_than_producing_an_unusable_query()
    {
        var limits = PlatformDiagnosticsQueryLimits.Create(
            maxRows: 0,
            maxResponseBytes: -1,
            maxDuration: TimeSpan.Zero);

        Assert.Equal(1, limits.MaxRows);
        Assert.Equal(1, limits.MaxResponseBytes);
        Assert.True(limits.MaxDuration > TimeSpan.Zero);
    }

    [Fact]
    public void The_row_byte_budget_reserves_room_for_the_response_envelope()
    {
        var limits = PlatformDiagnosticsQueryLimits.Default;

        Assert.Equal(
            PlatformDiagnosticsQueryLimits.HardMaxResponseBytes
                - PlatformDiagnosticsQueryLimits.ResponseEnvelopeReserveBytes,
            limits.RowByteBudget);
    }

    [Fact]
    public void A_response_cap_smaller_than_the_envelope_reserve_leaves_no_negative_budget()
    {
        var limits = PlatformDiagnosticsQueryLimits.Create(maxResponseBytes: 16);

        Assert.Equal(0, limits.RowByteBudget);
    }
}
