using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Application.PlatformDiagnostics;
using Inventory.Infrastructure.Data;
using InventoryApi.Auth.E2ETesting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The access boundary of the platform diagnostics API, through the real HTTP pipeline against a
/// real relational database (issue #336).
///
/// The claims being tested are the issue's acceptance criteria in order: an unauthorised caller is
/// denied; a business member without the platform-admin configuration is denied; the configured
/// platform administrator reaches <em>only</em> diagnostics; every other route keeps its membership
/// behaviour; the capability endpoint exposes no tenant data; the data path cannot write or exceed
/// its limits; and every query - including a refused one - is audited without the statement, the
/// rows or any credential appearing in the log.
/// </summary>
public sealed class PlatformAdminAuthorizationTests : IClassFixture<PlatformDiagnosticsHostFactory>
{
    private const string AccessEndpoint = "/api/admin/diagnostics/access";
    private const string QueryEndpoint = "/api/admin/diagnostics/query";

    private readonly PlatformDiagnosticsHostFactory _factory;

    public PlatformAdminAuthorizationTests(PlatformDiagnosticsHostFactory factory) => _factory = factory;

    #region Who may reach it

    [Fact]
    public async Task An_unauthenticated_request_to_the_capability_endpoint_is_401()
    {
        var response = await _factory.CreateClient().GetAsync(AccessEndpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_request_to_the_data_path_is_401()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync(QueryEndpoint, new { sql = "SELECT Id FROM Products" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// An authenticated business member is not a platform administrator. Being a legitimate,
    /// fully authorised user of every other endpoint buys nothing here.
    /// </summary>
    [Fact]
    public async Task An_authenticated_business_member_is_forbidden_from_the_capability_endpoint()
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.BusinessMember).GetAsync(AccessEndpoint);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_authenticated_business_member_is_forbidden_from_the_data_path()
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.BusinessMember)
            .PostAsJsonAsync(QueryEndpoint, new { sql = "SELECT Id, BusinessId FROM Products" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_configured_platform_administrator_reaches_the_capability_endpoint()
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator)
            .GetAsync(AccessEndpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Everything else keeps the behaviour it had

    /// <summary>
    /// The membership bypass is endpoint-specific. The platform administrator has no business
    /// membership, and on a business endpoint that still means 403 - the same answer the same actor
    /// got before this issue existed.
    /// </summary>
    [Theory]
    [InlineData("/api/products")]
    [InlineData("/api/categories")]
    [InlineData("/api/suppliers")]
    [InlineData("/api/reports/dashboard")]
    public async Task The_platform_administrator_is_still_refused_by_every_business_endpoint(string route)
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator).GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The other half of the same claim: an ordinary member's access is untouched, so the policy
    /// and the middleware change narrowed nothing for everybody else.
    /// </summary>
    [Fact]
    public async Task A_business_member_still_reads_its_own_businesss_data_normally()
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.BusinessMember).GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var products = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEmpty(products.RootElement.EnumerateArray());
    }

    #endregion

    #region The capability endpoint exposes no tenant data

    [Fact]
    public async Task The_capability_response_carries_only_the_signal_and_the_limits()
    {
        using var body = await ReadJsonAsync(
            await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator).GetAsync(AccessEndpoint));

        Assert.Equal(
            ["authorized", "crossBusinessScope", "limits"],
            body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.True(body.RootElement.GetProperty("authorized").GetBoolean());
        Assert.True(body.RootElement.GetProperty("crossBusinessScope").GetBoolean());

        var limits = body.RootElement.GetProperty("limits");
        Assert.Equal(PlatformDiagnosticsQueryLimits.MaxSqlBytes, limits.GetProperty("maxSqlBytes").GetInt32());
        Assert.Equal(PlatformDiagnosticsQueryLimits.HardMaxRows, limits.GetProperty("maxRows").GetInt32());
        Assert.Equal(
            PlatformDiagnosticsQueryLimits.HardMaxResponseBytes,
            limits.GetProperty("maxResponseBytes").GetInt32());
        Assert.Equal(
            PlatformDiagnosticsQueryLimits.HardMaxDuration.TotalSeconds,
            limits.GetProperty("maxDurationSeconds").GetDouble());
    }

    /// <summary>
    /// Asserted on the raw body rather than the parsed object, because the risk is a field nobody
    /// meant to add. None of the seeded businesses' or products' names, and no actor identifier,
    /// may appear anywhere in it.
    /// </summary>
    [Fact]
    public async Task The_capability_response_contains_no_business_or_actor_data_at_all()
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator)
            .GetAsync(AccessEndpoint);
        var raw = await response.Content.ReadAsStringAsync();

        foreach (var forbidden in new[]
        {
            E2ETestFixture.BusinessAName,
            E2ETestFixture.BusinessBName,
            E2ETestFixture.BusinessBProductName,
            E2ETestFixture.ReorderProductName,
            PlatformDiagnosticsHostFactory.PlatformAdministrator.ObjectId,
            PlatformDiagnosticsHostFactory.PlatformAdministrator.DirectoryTenantId,
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region The data path

    /// <summary>
    /// What the endpoint is for: the cross-business ownership read the tenant boundary deliberately
    /// makes impossible everywhere else. Both synthetic businesses' rows come back, from the
    /// seeded data of two different owners.
    /// </summary>
    [Fact]
    public async Task The_platform_administrator_reads_identity_columns_from_every_business()
    {
        using var body = await QueryAsync(
            "SELECT BusinessId, Id FROM Products ORDER BY BusinessId, Id",
            HttpStatusCode.OK);

        Assert.Equal("Succeeded", body.RootElement.GetProperty("outcome").GetString());
        Assert.True(body.RootElement.GetProperty("crossBusinessScope").GetBoolean());
        Assert.Equal(["BusinessId", "Id"], Columns(body));

        var ownerIds = body.RootElement.GetProperty("rows")
            .EnumerateArray()
            .Select(row => row[0].GetString())
            .Distinct()
            .ToArray();

        Assert.Equal(2, ownerIds.Length);
    }

    [Theory]
    [InlineData("SELECT Name FROM Products", "ForbiddenSchemaAccess")]
    [InlineData("SELECT ObjectId FROM BusinessMemberships", "ForbiddenSchemaAccess")]
    [InlineData("SELECT p.Id FROM Products p JOIN NayaxSales s ON s.BusinessId = p.BusinessId", "ForbiddenSchemaAccess")]
    [InlineData("DELETE FROM Products", "NotAReadOnlyStatement")]
    [InlineData("SELECT Id FROM Products; DROP TABLE Products", "MultipleStatements")]
    [InlineData("PRAGMA table_info('Products')", "NotAReadOnlyStatement")]
    public async Task A_statement_outside_the_permitted_surface_is_refused_with_400(string sql, string denialReason)
    {
        using var body = await QueryAsync(sql, HttpStatusCode.BadRequest);

        Assert.Equal("Rejected", body.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(denialReason, body.RootElement.GetProperty("denialReason").GetString());
        Assert.Empty(body.RootElement.GetProperty("rows").EnumerateArray());
    }

    /// <summary>
    /// The refusals above must be refusals, not quiet successes. Row counts and the product
    /// catalogue are read back through the ordinary unrestricted test context afterwards.
    /// </summary>
    [Fact]
    public async Task A_refused_write_through_the_endpoint_mutates_nothing()
    {
        var before = await CountsAsync();

        foreach (var sql in new[]
        {
            "DELETE FROM Products",
            "UPDATE Products SET BusinessId = 99",
            "INSERT INTO Categories (BusinessId, Name) VALUES (1, 'injected')",
            "DROP TABLE StockAdjustments",
            "SELECT Id FROM Products; DELETE FROM Categories",
        })
        {
            await QueryAsync(sql, HttpStatusCode.BadRequest);
        }

        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task An_oversized_statement_is_refused_with_400_before_it_is_prepared()
    {
        var oversized = "SELECT Id FROM Products WHERE Id = 1 AND '"
            + new string('x', PlatformDiagnosticsQueryLimits.MaxSqlBytes)
            + "' = ''";

        using var body = await QueryAsync(oversized, HttpStatusCode.BadRequest);

        Assert.Equal("SqlTooLarge", body.RootElement.GetProperty("denialReason").GetString());
    }

    [Fact]
    public async Task A_missing_statement_is_refused_with_400()
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator)
            .PostAsJsonAsync(QueryEndpoint, new { sql = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("SqlMissing", body.RootElement.GetProperty("denialReason").GetString());
    }

    #endregion

    #region Audit

    /// <summary>
    /// The audit event, asserted on both halves: what it must say, and what it must never say. The
    /// statement carries a distinctive literal precisely so its absence from the log is provable.
    /// </summary>
    [Fact]
    public async Task A_successful_query_emits_one_audit_event_with_the_actor_the_fingerprint_and_no_statement()
    {
        const string sql = "SELECT Id, BusinessId FROM Products WHERE Id > 0 AND 'auditprobe' <> ''";
        var fingerprint = DiagnosticsSqlShape.Fingerprint(sql);

        using var body = await QueryAsync(sql, HttpStatusCode.OK);
        Assert.Equal(fingerprint, body.RootElement.GetProperty("queryFingerprint").GetString());

        var audit = AuditEventFor(fingerprint);

        Assert.Contains(PlatformDiagnosticsHostFactory.PlatformAdministrator.DirectoryTenantId, audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PlatformDiagnosticsHostFactory.PlatformAdministrator.ObjectId, audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cross-business scope True", audit, StringComparison.Ordinal);
        Assert.Contains("outcome Succeeded", audit, StringComparison.Ordinal);

        // Never the statement, and never a value the caller typed into it.
        Assert.DoesNotContain("auditprobe", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FROM Products", audit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A refused attempt is exactly what an investigation into misuse would look for, so it cannot
    /// be the one outcome that leaves no trace.
    /// </summary>
    [Fact]
    public async Task A_refused_query_is_audited_too()
    {
        const string sql = "SELECT ObjectId FROM BusinessMemberships WHERE ObjectId <> 'refusalprobe'";

        await QueryAsync(sql, HttpStatusCode.BadRequest);

        var audit = AuditEventFor(DiagnosticsSqlShape.Fingerprint(sql));

        Assert.Contains("outcome Rejected", audit, StringComparison.Ordinal);
        Assert.Contains("denial ForbiddenSchemaAccess", audit, StringComparison.Ordinal);
        Assert.Contains("rows 0", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("refusalprobe", audit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A caller the policy refuses never reaches the use case, so there is no query to audit - and
    /// the result values of one must not appear in the log either way.
    /// </summary>
    [Fact]
    public async Task A_forbidden_caller_produces_no_query_audit_event()
    {
        const string sql = "SELECT Id FROM Products WHERE Id > 0 AND 'memberprobe' <> ''";

        var response = await _factory.As(PlatformDiagnosticsHostFactory.BusinessMember)
            .PostAsJsonAsync(QueryEndpoint, new { sql });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(
            DiagnosticsSqlShape.Fingerprint(sql),
            string.Join("\n", _factory.LogMessages),
            StringComparison.Ordinal);
    }

    private string AuditEventFor(string fingerprint)
    {
        var audit = _factory.LogMessages
            .Where(message => message.Contains("Platform diagnostics query audit", StringComparison.Ordinal))
            .Where(message => message.Contains(fingerprint, StringComparison.Ordinal))
            .ToArray();

        return Assert.Single(audit);
    }

    #endregion

    #region Harness

    private async Task<JsonDocument> QueryAsync(string sql, HttpStatusCode expected)
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator)
            .PostAsJsonAsync(QueryEndpoint, new { sql });

        var body = await ReadJsonAsync(response);

        Assert.Equal(expected, response.StatusCode);

        return body;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static string[] Columns(JsonDocument body) =>
        body.RootElement.GetProperty("columns").EnumerateArray().Select(column => column.GetString()!).ToArray();

    /// <summary>
    /// Row counts read from outside the diagnostics path, through the deliberate unrestricted test
    /// context, so "nothing was mutated" is checked against the database rather than against the
    /// endpoint's own answer.
    /// </summary>
    private async Task<string> CountsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
        await using var db = TestAppDbContext.Unrestricted(options);

        return string.Join(
            ", ",
            $"businesses={await db.Businesses.CountAsync()}",
            $"categories={await db.Categories.CountAsync()}",
            $"products={await db.Products.CountAsync()}",
            $"stockAdjustments={await db.StockAdjustments.CountAsync()}",
            $"owners={string.Join("/", await db.Products.Select(product => product.BusinessId).Distinct().OrderBy(id => id).ToListAsync())}");
    }

    #endregion
}
