using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Inventory.Application.Access;
using Inventory.Application.SupportSessions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// A Super Admin's role preview, end to end (issue #720): over real HTTP, through the whole
/// pipeline - authentication, <c>RolePreviewMiddleware</c>, <c>[Authorize]</c>, the capability
/// policies, the support-session accessor, <c>BusinessScopeMiddleware</c>, the tenant query filters
/// and <c>BusinessOwnershipEnforcer</c> - against relational SQLite with the real migration history.
///
/// <para>The administrator here is deliberately an active <strong>Owner of their own business</strong>,
/// which is the case the feature is for: the preview has a real base to narrow, so "an Operator is
/// shown less" is a claim about the intersection rather than about a caller who could reach nothing
/// anyway. The trading business of the Super Admin fixture is somebody else's and is what the
/// support-session rows point at.</para>
///
/// <para>The financial redaction half of the criteria - the dashboard, machine, site and
/// stock-history figures an Operator preview must withhold - is
/// <c>RolePreviewFinancialRedactionApiTests</c>, which seeds the sales, fleet and movements those
/// figures are computed from.</para>
/// </summary>
public class RolePreviewApiTests : IClassFixture<RolePreviewApiTests.ApiFactory>, IAsyncLifetime
{
    private const string MeAccess = "/api/me/access";
    private const string Products = "/api/products";

    /// <summary>A <c>Reports.View</c> read: the issue's own example of a capability an Operator lacks.</summary>
    private const string DailyReport = "/api/reports/daily";

    /// <summary>A <c>Business.Manage</c> read, which answers <c>200</c> for an Owner over this host.</summary>
    private const string BusinessSettings = "/api/business/settings";

    private readonly ApiFactory _factory;

    public RolePreviewApiTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region What GET /api/me/access reports

    /// <summary>
    /// The criterion: under each preview role the reported capabilities are exactly that role's
    /// published set, in the published order, and <c>role</c> still reports the caller's own stored
    /// role - the preview changes what they may do, not who they are.
    /// </summary>
    [Theory]
    [InlineData("Operator")]
    [InlineData("Manager")]
    [InlineData("Owner")]
    public async Task Me_access_reports_exactly_the_previewed_roles_capabilities(string preview)
    {
        var response = await _factory.Previewing(preview).GetAsync(MeAccess);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        Assert.Equal(
            Names(RoleCapabilities.For(Enum.Parse<BusinessRole>(preview))),
            CapabilityNames(json.RootElement));
        Assert.Equal("Owner", json.RootElement.GetProperty("role").GetString());
        Assert.Equal(ApiFactory.AdminBusinessName, json.RootElement.GetProperty("businessName").GetString());
        // Not a support session: this administrator is reading their own business.
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("supportSession").ValueKind);
    }

    /// <summary>
    /// The control that makes the rows above a claim about the preview: the same caller without the
    /// header is reported their own Owner capabilities, in full.
    /// </summary>
    [Fact]
    public async Task Without_the_header_the_same_caller_is_reported_their_own_capabilities()
    {
        var response = await _factory.AsAdministrator().GetAsync(MeAccess);

        using var json = await ReadJsonAsync(response);
        Assert.Equal(
            Names(RoleCapabilities.For(BusinessRole.Owner)),
            CapabilityNames(json.RootElement));
        Assert.Equal("Owner", json.RootElement.GetProperty("role").GetString());
    }

    #endregion

    #region What a previewed read may reach

    /// <summary>
    /// The criterion: an endpoint whose capability the previewed role lacks answers the same bare
    /// <c>403</c> that role gets - no code, no capability, no hint about the role matrix.
    /// </summary>
    [Theory]
    [InlineData("Operator", DailyReport)]
    [InlineData("Operator", BusinessSettings)]
    [InlineData("Manager", BusinessSettings)]
    public async Task A_capability_the_previewed_role_lacks_is_a_bare_403(string preview, string route)
    {
        var response = await _factory.Previewing(preview).GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("role_preview", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Reports.View", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Business.Manage", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same criterion, and what keeps it a statement about the preview: the
    /// same caller, the same endpoints, no header - and no refusal. The report answers the
    /// <c>409</c> of a business with no Nayax connection, which is its own contract and not an
    /// access decision.
    /// </summary>
    [Theory]
    [InlineData(DailyReport)]
    [InlineData(BusinessSettings)]
    public async Task Without_the_header_the_same_endpoints_are_not_refused(string route)
    {
        var response = await _factory.AsAdministrator().GetAsync(route);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A capability the previewed role does hold is answered normally, so the preview narrows
    /// rather than simply refusing: an Operator preview still reads the catalogue, which is what
    /// <c>Inventory.Operate</c> is for.
    /// </summary>
    [Fact]
    public async Task A_capability_the_previewed_role_holds_is_answered_normally()
    {
        var response = await _factory.Previewing("Operator").GetAsync(Products);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            ApiFactory.AdminProductName,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>HEAD</c> is a read, so the read-only rule does not refuse it: the method allowlist is
    /// two methods rather than "GET", and a client that probes with <c>HEAD</c> is not writing.
    /// Which status the endpoint itself answers a <c>HEAD</c> with is its own routing contract and
    /// not this feature's.
    /// </summary>
    [Fact]
    public async Task A_head_request_is_not_refused_as_read_only()
    {
        var response = await _factory.Previewing("Operator")
            .SendAsync(new HttpRequestMessage(HttpMethod.Head, Products));

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region A previewed request is read-only

    /// <summary>
    /// The criterion: every method that is not a read is refused with <c>403</c> and
    /// <c>read_only_role_preview</c>, on a capability endpoint, on the two membership-not-required
    /// writes and on a Super Admin write alike - and the endpoint's own code never runs.
    ///
    /// <para>The preview is <c>Owner</c> throughout, which is the caller's own role, so nothing was
    /// narrowed away: the refusal is the method rule and could not be a missing capability. The
    /// onboarding application is behind a disabled feature flag on this host and would answer
    /// <c>404</c>; it answers <c>403</c> instead, which is itself the proof that the refusal happens
    /// before the endpoint.</para>
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/operating-expenses")]
    [InlineData("PUT", "/api/operating-expenses/999999")]
    [InlineData("DELETE", "/api/operating-expenses/999999")]
    [InlineData("PUT", BusinessSettings)]
    // The "Available to add" supplier add of issue #935, which declares no rule of its own: the
    // method rule is what refuses it, so a previewed role cannot give the business a supplier.
    [InlineData("POST", "/api/suppliers/available-presets/999999")]
    [InlineData("POST", "/api/invites/redeem")]
    [InlineData("POST", "/api/onboarding/business-applications")]
    [InlineData("POST", "/api/nayax-sales-sync")]
    public async Task Every_method_that_is_not_a_read_is_refused_as_read_only(string method, string route)
    {
        var response = await _factory.Previewing("Owner").SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RolePreviewErrorCodes.ReadOnly, await CodeAsync(response));
    }

    /// <summary>
    /// The same criterion on the Super Admin's own write surface, with the assertion the issue asks
    /// for: the pending application is still pending afterwards, so nothing was written.
    /// </summary>
    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("deactivate")]
    public async Task A_super_admin_write_is_refused_as_read_only_and_writes_nothing(string action)
    {
        var route = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/super-admin/businesses/{_factory.PendingBusinessId}/{action}");

        var response = await _factory.Previewing("Owner").SendAsync(Request("POST", route));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RolePreviewErrorCodes.ReadOnly, await CodeAsync(response));
        Assert.Equal(BusinessStatus.Pending, (await _factory.BusinessAsync(_factory.PendingBusinessId)).Status);
    }

    /// <summary>
    /// A write refused as read-only wrote nothing of its own either: the expense the <c>POST</c>
    /// would have recorded does not exist.
    /// </summary>
    [Fact]
    public async Task A_refused_write_records_nothing()
    {
        var response = await _factory.Previewing("Owner")
            .SendAsync(Request("POST", "/api/operating-expenses", Expense));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RolePreviewErrorCodes.ReadOnly, await CodeAsync(response));
        Assert.Empty(await _factory.OperatingExpensesAsync());
    }

    /// <summary>
    /// The control: the same request without the header is not refused, so this administrator
    /// really could have written it. A preview is what makes them read-only.
    /// </summary>
    [Fact]
    public async Task Without_the_header_the_same_write_is_allowed()
    {
        var response = await _factory.AsAdministrator()
            .SendAsync(Request("POST", "/api/operating-expenses", Expense));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(await _factory.OperatingExpensesAsync());
    }

    /// <summary>
    /// A previewed <c>GET</c> on the Super Admin's own surface is unaffected: that surface declares
    /// no capability at all - it is answered by the platform-admin policy - so there is nothing for
    /// a preview to narrow, and the read-only rule is the only part of this feature that reaches it.
    /// </summary>
    [Fact]
    public async Task A_previewed_read_of_the_super_admin_surface_is_unaffected()
    {
        var response = await _factory.Previewing("Operator").GetAsync("/api/super-admin/businesses");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Who may ask, and with what value

    /// <summary>
    /// The criterion: a caller the platform-admin policy refuses is refused with <c>403</c> and
    /// <c>role_preview_not_permitted</c>. They are never served at their own capabilities instead -
    /// the body carries none of their business's data - and the refusal happens before any business
    /// is resolved, so no membership, profile or anything else is written.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task A_caller_who_may_not_preview_is_refused(string objectId)
    {
        var before = await _factory.MembershipsOfAsync(_factory.TradingBusinessId);

        var response = await _factory.PreviewingAs(objectId, "Operator").GetAsync(Products);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RolePreviewErrorCodes.NotPermitted, await CodeAsync(response));
        Assert.DoesNotContain(ApiFactory.TradingProductName, body, StringComparison.Ordinal);
        var after = await _factory.MembershipsOfAsync(_factory.TradingBusinessId);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(
            before.Select(membership => (membership.Id, membership.Role, membership.IsActive)),
            after.Select(membership => (membership.Id, membership.Role, membership.IsActive)));
    }

    /// <summary>
    /// And the control: that same Owner without the header reads their own business exactly as
    /// before. Setting the header costs them their request; it never costs them their access.
    /// </summary>
    [Fact]
    public async Task Without_the_header_an_ordinary_owner_is_unaffected()
    {
        var response = await _factory
            .Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)
            .GetAsync(Products);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            ApiFactory.TradingProductName,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The criterion: a value that is not exactly one of the three published role names is
    /// <c>400</c> with <c>invalid_role_preview</c>, and is never served at full capabilities. A
    /// different case, a stored role value, an undeclared role, a blank value and surrounding
    /// whitespace are all refused rather than interpreted.
    ///
    /// <para>A header present with an <em>empty</em> value belongs to the same criterion and is in
    /// <c>RequestRolePreviewAccessorTests</c> instead: <c>HttpClient</c> drops an empty header value
    /// before it is sent, so over real HTTP it cannot be told from no header at all.</para>
    /// </summary>
    [Theory]
    [InlineData("operator")]
    [InlineData("OWNER")]
    [InlineData("MaNaGeR")]
    [InlineData("20")]
    [InlineData("40")]
    [InlineData("Viewer")]
    [InlineData(" ")]
    [InlineData(" Owner")]
    [InlineData("Owner ")]
    public async Task An_undeclared_preview_value_is_refused(string value)
    {
        var response = await _factory.Previewing(value).GetAsync(MeAccess);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RolePreviewErrorCodes.Invalid, await CodeAsync(response));
        // Never the Owner's own answer: a preview that cannot be read must not widen the request.
        Assert.DoesNotContain("\"capabilities\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The header sent twice names no single role, so it is refused in the same way rather than
    /// resolved from whichever of the two values a reader happens to pick first.
    /// </summary>
    [Fact]
    public async Task The_header_sent_twice_is_refused()
    {
        var client = _factory.AsAdministrator();
        client.DefaultRequestHeaders.TryAddWithoutValidation(RolePreviewHeader.Name, "Owner");
        client.DefaultRequestHeaders.TryAddWithoutValidation(RolePreviewHeader.Name, "Operator");

        var response = await client.GetAsync(MeAccess);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RolePreviewErrorCodes.Invalid, await CodeAsync(response));
    }

    /// <summary>
    /// An invalid value is refused before the method rule, which is the documented precedence: a
    /// preview nobody can read is not refused as a write, because the caller has to fix the value
    /// either way.
    /// </summary>
    [Fact]
    public async Task An_undeclared_value_outranks_the_read_only_refusal()
    {
        var response = await _factory.Previewing("owner").SendAsync(Request("POST", "/api/operating-expenses"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RolePreviewErrorCodes.Invalid, await CodeAsync(response));
    }

    /// <summary>
    /// And a caller who may not preview at all is refused before the value is even looked at, which
    /// is the first step of the same precedence: the header's value is not theirs to have read.
    /// </summary>
    [Fact]
    public async Task Not_being_permitted_outranks_an_undeclared_value()
    {
        var response = await _factory
            .PreviewingAs(SuperAdminBusinessesApiFactory.TradingOwnerObjectId, "nonsense")
            .GetAsync(Products);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RolePreviewErrorCodes.NotPermitted, await CodeAsync(response));
    }

    /// <summary>
    /// An unauthenticated request keeps its ordinary <c>401</c>: the header is not read for a caller
    /// nobody has identified, exactly as <c>BusinessScopeMiddleware</c> leaves such a request alone,
    /// so "you are not signed in" is not reported as a refused preview.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_previewed_request_keeps_its_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(RolePreviewHeader.Name, "Operator");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Products)).StatusCode);
    }

    #endregion

    #region A preview resolves no business

    /// <summary>
    /// The criterion: an administrator with no usable membership and no session gets the same
    /// refusal with a valid preview as without one. A preview narrows capabilities and never
    /// resolves a business, so there is nothing for it to be the caller's way in.
    /// </summary>
    [Fact]
    public async Task A_preview_never_resolves_a_business_for_an_administrator_with_no_membership()
    {
        await _factory.SetMembershipActiveAsync(_factory.AdminMembershipId, isActive: false);

        var withHeader = await _factory.Previewing("Owner").GetAsync(Products);
        var without = await _factory.AsAdministrator().GetAsync(Products);

        Assert.Equal(HttpStatusCode.Forbidden, withHeader.StatusCode);
        Assert.Equal(without.StatusCode, withHeader.StatusCode);
        // The same refusal, not a role-preview one: there is no preview-shaped answer to a caller
        // whose membership did not resolve, and no data in either body.
        Assert.Null(await CodeAsync(withHeader));
        Assert.DoesNotContain(
            ApiFactory.AdminProductName,
            await withHeader.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And on <c>/api/me/access</c>, which has no capability requirement of its own: the preview
    /// does not describe access to a business nobody resolved.
    /// </summary>
    [Fact]
    public async Task A_preview_describes_no_access_for_an_administrator_with_no_membership()
    {
        await _factory.SetMembershipActiveAsync(_factory.AdminMembershipId, isActive: false);

        var response = await _factory.Previewing("Operator").GetAsync(MeAccess);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

    #region Composed with a support session

    /// <summary>
    /// The criterion: inside a support session the base is the session's own fixed set, so the
    /// preview answers that set intersected with the previewed role. With <c>Manager</c>,
    /// <c>Suppliers.ManageGstDefaults</c> stays absent - the session never held it, and an
    /// intersection cannot restore what neither side grants.
    /// </summary>
    [Fact]
    public async Task A_manager_preview_in_a_support_session_is_the_intersection()
    {
        var client = await _factory.InSupportSessionPreviewing("Manager");

        using var json = await ReadJsonAsync(await client.GetAsync(MeAccess));

        Assert.Equal(
            Names(SupportSessionAccessPolicy.Capabilities
                .Where(capability => RoleCapabilities.Allows(BusinessRole.Manager, capability))),
            CapabilityNames(json.RootElement));
        Assert.DoesNotContain("Suppliers.ManageGstDefaults", CapabilityNames(json.RootElement));
        Assert.Contains("Reports.View", CapabilityNames(json.RootElement));
        // Still a support session: the preview narrows what it may do and changes nothing else.
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("role").ValueKind);
        Assert.Equal(
            SuperAdminBusinessesApiFactory.TradingBusinessName,
            json.RootElement.GetProperty("supportSession").GetProperty("businessName").GetString());
    }

    /// <summary>
    /// The same intersection for an Operator preview: the session's financial and reporting
    /// capabilities go, and the four operational ones remain.
    /// </summary>
    [Fact]
    public async Task An_operator_preview_in_a_support_session_loses_the_financial_capabilities()
    {
        var client = await _factory.InSupportSessionPreviewing("Operator");

        using var json = await ReadJsonAsync(await client.GetAsync(MeAccess));

        Assert.Equal(
            ["Dashboard.View", "PickList.View", "Inventory.Operate", "Purchasing.Operate"],
            CapabilityNames(json.RootElement));
    }

    /// <summary>
    /// And the session still reads the session's business: a preview narrows capabilities and never
    /// chooses whose data is read.
    /// </summary>
    [Fact]
    public async Task A_previewed_support_session_still_reads_the_sessions_business()
    {
        var client = await _factory.InSupportSessionPreviewing("Operator");

        var body = await (await client.GetAsync(Products)).Content.ReadAsStringAsync();

        Assert.Contains(ApiFactory.TradingProductName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.AdminProductName, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The documented precedence where both rules would refuse: a write inside a session is
    /// answered as the session's own <c>read_only_support_session</c>, not as the preview's
    /// read-only refusal. A dead session has to be told apart from a read-only view, and that
    /// distinction is the session's to make.
    /// </summary>
    [Fact]
    public async Task A_write_in_a_previewed_support_session_is_refused_as_the_session()
    {
        var client = await _factory.InSupportSessionPreviewing("Owner");

        var response = await client.SendAsync(Request("POST", "/api/operating-expenses"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SupportSessionErrorCodes.ReadOnly, await CodeAsync(response));
    }

    /// <summary>
    /// And a session that is over is reported as over, whatever the preview asked for: the preview
    /// is not what makes a presented session usable or unusable.
    /// </summary>
    [Fact]
    public async Task A_dead_session_with_a_preview_is_still_refused_as_ended()
    {
        const string sessionId = "a-preview-will-not-revive-this-session";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            sessionId,
            startedAtUtc: DateTime.UtcNow.AddHours(-3),
            expiresAtUtc: DateTime.UtcNow.AddHours(-2));
        var client = _factory.Previewing("Operator");
        client.DefaultRequestHeaders.Add(SupportSessionHeader.Name, sessionId);

        var response = await client.GetAsync(Products);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SupportSessionErrorCodes.SessionEnded, await CodeAsync(response));
    }

    #endregion

    private static string[] Names(IEnumerable<Capability> capabilities) =>
        [.. capabilities.Select(Capabilities.Name)];

    private static string?[] CapabilityNames(JsonElement access) =>
        [.. access.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString())];

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>
    /// The stable error code a refusal carries, or <see langword="null"/> when it carries none -
    /// which includes the bare, bodyless <c>403</c> a capability refusal answers with.
    /// </summary>
    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>A body the operating-expense endpoint really would record.</summary>
    private const string Expense =
        """
        {"expenseDate":"2026-03-12T00:00:00Z","category":8,"description":"Cleaning",
         "amountExGst":10.00,"gstAmount":1.00,"totalAmount":11.00}
        """;

    /// <summary>
    /// A request whose body, where it needs one, is deliberately the empty object unless a test
    /// supplies one: every row that uses the default is about the access decision, which is taken
    /// before an action binds a body, so nothing there may be a request the endpoint would act on.
    /// </summary>
    private static HttpRequestMessage Request(string method, string route, string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);

        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    /// <summary>
    /// The Super Admin host of issue #550, with the one thing this feature needs: the configured
    /// platform administrator is also an active Owner of a business of their own, so a preview has
    /// a real base to narrow. Both businesses hold a product, so "whose data was read" stays
    /// assertable.
    /// </summary>
    public sealed class ApiFactory : SuperAdminBusinessesApiFactory
    {
        public const string AdminBusinessName = "Administrator's Own Vending";
        public const string AdminProductName = "Administrator Business Cola";
        public const string TradingProductName = "Trading Business Lemonade";
        public const string SessionId = "the-support-session-id-these-tests-present";

        /// <summary>The business the administrator owns in their own right.</summary>
        public int AdminBusinessId { get; private set; }

        /// <summary>The administrator's own Owner membership of it.</summary>
        public int AdminMembershipId { get; private set; }

        /// <summary>A client previewing <paramref name="role"/> as the administrator.</summary>
        public HttpClient Previewing(string role) => PreviewingAs(AdministratorObjectId, role);

        /// <summary>A client previewing <paramref name="role"/> as <paramref name="objectId"/>.</summary>
        public HttpClient PreviewingAs(string objectId, string role)
        {
            var client = Authenticated(objectId);
            // Added without validation so a blank or padded value reaches the API exactly as a
            // client could send it, rather than being refused by HttpClient first.
            client.DefaultRequestHeaders.TryAddWithoutValidation(RolePreviewHeader.Name, role);
            return client;
        }

        /// <summary>
        /// A client inside a freshly started, usable support session for the <em>trading</em>
        /// business - somebody else's - while previewing <paramref name="role"/>.
        /// </summary>
        public async Task<HttpClient> InSupportSessionPreviewing(string role)
        {
            await SeedSupportSessionAsync(TradingBusinessId, AdministratorObjectId, SessionId);
            var client = Previewing(role);
            client.DefaultRequestHeaders.Add(SupportSessionHeader.Name, SessionId);
            return client;
        }

        public async Task<IReadOnlyList<OperatingExpense>> OperatingExpensesAsync()
        {
            await using var db = Database();
            return await db.OperatingExpenses.AsNoTracking().ToListAsync();
        }

        /// <summary>
        /// Puts the world back, and adds this file's own fixtures: the administrator's own business
        /// and Owner membership, and one product in each business.
        /// </summary>
        public new async Task ResetAsync()
        {
            // Before the base reset, which removes the businesses themselves: an expense recorded
            // by one of these tests belongs to a business, so it has to go first.
            await using (var expenses = Database())
            {
                expenses.OperatingExpenses.RemoveRange(await expenses.OperatingExpenses.ToListAsync());
                await expenses.SaveChangesAsync();
            }

            await base.ResetAsync();

            AdminBusinessId = await SeedBusinessAsync(AdminBusinessName, BusinessStatus.Active);
            AdminMembershipId = await SeedMembershipAsync(
                AdminBusinessId, AdministratorObjectId, BusinessRole.Owner, isActive: true);

            await using var db = Database();
            db.Products.AddRange(
                NewProduct(AdminBusinessId, AdminProductName),
                NewProduct(TradingBusinessId, TradingProductName));
            await db.SaveChangesAsync();

            ClearLog();
        }

        private static Product NewProduct(int businessId, string name) => new()
        {
            BusinessId = businessId,
            Name = name,
            Sku = $"{name}-SKU",
            QuantityInStock = 5,
        };
    }
}
