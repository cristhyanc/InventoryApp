using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Application.PlatformDiagnostics;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The serialised-byte cap, measured on the bytes that actually leave the server (issue #336).
///
/// The host is configured with a tightened response budget rather than the 1 MiB maximum, because
/// the permitted data surface is seven tables of integer identity columns and the 500-row cap sits
/// in front of the byte cap - so a megabyte is unreachable through it by design, and a test that
/// tried to reach it would be testing nothing. The number is the only difference:
/// <c>PlatformDiagnosticsQueryLimits.Create</c> clamps it to the hard maximum, the same accounting
/// runs, and <c>PlatformDiagnosticsQueryLimitsTests</c> pins the hard maximum itself.
/// </summary>
public sealed class PlatformDiagnosticsResponseSizeTests
    : IClassFixture<ByteCappedPlatformDiagnosticsHostFactory>
{
    private readonly ByteCappedPlatformDiagnosticsHostFactory _factory;

    public PlatformDiagnosticsResponseSizeTests(ByteCappedPlatformDiagnosticsHostFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task A_result_that_would_exceed_the_byte_budget_is_truncated_and_says_so()
    {
        var (status, body, _) = await QueryAsync("SELECT Id, BusinessId FROM Products ORDER BY Id");

        Assert.Equal(HttpStatusCode.OK, status);
        using var json = JsonDocument.Parse(body);

        Assert.Equal("Truncated", json.RootElement.GetProperty("outcome").GetString());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("ResponseByteLimit", json.RootElement.GetProperty("truncationReason").GetString());
    }

    /// <summary>
    /// The cap is on the complete serialised body, so that is what is measured - not the row count,
    /// and not an estimate the server made.
    /// </summary>
    [Fact]
    public async Task The_serialised_response_body_stays_within_the_configured_maximum()
    {
        var (_, _, byteCount) = await QueryAsync("SELECT Id, BusinessId FROM Products ORDER BY Id");

        Assert.True(
            byteCount <= ByteCappedPlatformDiagnosticsHostFactory.TightenedLimits.MaxResponseBytes,
            $"The response body was {byteCount} bytes; the configured maximum is "
                + $"{ByteCappedPlatformDiagnosticsHostFactory.TightenedLimits.MaxResponseBytes}.");
    }

    /// <summary>
    /// A truncated answer must never look like the whole answer. The seeded catalogue has more
    /// products than the budget allows, so fewer rows came back than exist - and the response says
    /// that rather than leaving a reader to compare counts.
    /// </summary>
    [Fact]
    public async Task A_truncated_result_returns_fewer_rows_than_exist_and_is_not_presented_as_complete()
    {
        var (_, body, _) = await QueryAsync("SELECT Id, BusinessId FROM Products ORDER BY Id");
        var (_, countBody, _) = await QueryAsync("SELECT count(*) FROM Products");

        using var rows = JsonDocument.Parse(body);
        using var count = JsonDocument.Parse(countBody);

        var returned = rows.RootElement.GetProperty("rowCount").GetInt32();
        var total = int.Parse(
            count.RootElement.GetProperty("rows")[0][0].GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(returned < total, $"Expected a truncated read; {returned} of {total} rows came back.");
        Assert.True(rows.RootElement.GetProperty("truncated").GetBoolean());
    }

    /// <summary>
    /// The capability endpoint reports the limits the server is actually running with, so a caller
    /// never sizes its input against a number that is not in force on this host.
    /// </summary>
    [Fact]
    public async Task The_capability_endpoint_reports_the_limits_actually_in_force()
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator)
            .GetAsync("/api/admin/diagnostics/access");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var limits = json.RootElement.GetProperty("limits");

        Assert.Equal(
            ByteCappedPlatformDiagnosticsHostFactory.TightenedLimits.MaxResponseBytes,
            limits.GetProperty("maxResponseBytes").GetInt32());

        // The SQL size cap is a constant rather than a tightenable limit, so it is unchanged.
        Assert.Equal(PlatformDiagnosticsQueryLimits.MaxSqlBytes, limits.GetProperty("maxSqlBytes").GetInt32());
    }

    private async Task<(HttpStatusCode Status, string Body, int ByteCount)> QueryAsync(string sql)
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator)
            .PostAsJsonAsync("/api/admin/diagnostics/query", new { sql });

        var bytes = await response.Content.ReadAsByteArrayAsync();

        return (response.StatusCode, System.Text.Encoding.UTF8.GetString(bytes), bytes.Length);
    }
}
