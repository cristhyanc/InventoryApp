using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Application.PlatformOptions;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The switchable menu destinations over real HTTP (issue #1148, and the Reports group of issue
/// #1153): who may list and change them, what they default to, what every member is told, what an
/// unknown key does, and what a refused write leaves behind.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>,
/// the platform-admin policy, <c>[RequiredScope]</c>, <c>RolePreviewMiddleware</c>,
/// <c>BusinessScopeMiddleware</c>'s independent re-check of the same policy, the tenant query
/// filters and <c>BusinessOwnershipEnforcer</c> - so the authorization under test is the real one.
/// Only the authentication is synthetic.</para>
///
/// <para>The <em>member</em> half of the contract is asserted through <c>GET /api/me/access</c>, by
/// the trading business's own Owner, because that is where a member learns what the application
/// offers. The settings are shared by the whole application, so there is no business in either
/// request: the administrator names none when writing one, and the member's read reports the one
/// shared answer.</para>
///
/// <para>The legacy endpoint's own compatibility contract is
/// <c>SuperAdminOptionVisibilityApiTests</c>.</para>
/// </summary>
public class SuperAdminMenuVisibilityApiTests
    : IClassFixture<SuperAdminBusinessesApiFactory>, IAsyncLifetime
{
    private const string MenuVisibilityUrl = "/api/super-admin/menu-visibility";
    private const string AccessUrl = "/api/me/access";
    private const string PreviewUrl = "/api/admin/historical-gst-classification/preview";
    private const string BookkeepingReportUrl = "/api/reports/bookkeeping";

    private readonly SuperAdminBusinessesApiFactory _factory;

    public SuperAdminMenuVisibilityApiTests(SuperAdminBusinessesApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetAsync();
        await _factory.ResetMenuVisibilityAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The read is the whole catalogue, grouped and labelled, whether a key's state was stored or
    /// defaulted - which in the shipped state is all fifteen defaulted, with no stored row at all.
    /// </summary>
    [Fact]
    public async Task The_administrator_is_answered_the_whole_catalogue_with_every_key_offered()
    {
        Assert.Empty(await _factory.MenuVisibilityRowsAsync());

        var response = await _factory.AsAdministrator().GetAsync(MenuVisibilityUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = await ReadOptionsAsync(response);

        Assert.Equal(
            MenuVisibilityCatalogue.Options.Select(option => option.Key),
            options.Select(option => option.Key));
        Assert.Equal(
            MenuVisibilityCatalogue.Options.Select(option => option.Label),
            options.Select(option => option.Label));
        Assert.All(options, option => Assert.True(option.Visible));
        // And nothing was written to answer the read.
        Assert.Empty(await _factory.MenuVisibilityRowsAsync());
    }

    /// <summary>
    /// The eight Reports keys of issue #1153 are reported with their group, beside the Admin ones and
    /// before them, which is what the Super Admin page renders its two sections from.
    /// </summary>
    [Fact]
    public async Task The_reports_keys_are_reported_with_their_group_above_the_admin_ones()
    {
        var options = await ReadOptionsAsync(await _factory.AsAdministrator().GetAsync(MenuVisibilityUrl));

        Assert.Equal(
            MenuVisibilityCatalogue.Options.Select(option => (option.Key, option.Group)),
            options.Select(option => (option.Key, option.Group)));
        Assert.Equal(
            [
                (MenuVisibilityKeys.ReportsBookkeeping, "Reports", "Bookkeeping"),
                (MenuVisibilityKeys.ReportsDaily, "Reports", "Daily"),
                (MenuVisibilityKeys.ReportsTransactions, "Reports", "Transactions"),
                (MenuVisibilityKeys.ReportsReconciliation, "Reports", "Reconciliation"),
                (MenuVisibilityKeys.ReportsMachines, "Reports", "Machines"),
                (MenuVisibilityKeys.ReportsProducts, "Reports", "Products"),
                (MenuVisibilityKeys.ReportsSiteCommissions, "Reports", "Site Commissions"),
                (MenuVisibilityKeys.ReportsGst, "Reports", "GST"),
            ],
            options.Take(8).Select(option => (option.Key, option.Group, option.Label)).ToArray());
        Assert.All(options.Skip(8), option => Assert.Equal("Admin", option.Group));
    }

    /// <summary>
    /// Each Reports destination is switched by the administrator exactly as an Admin one is - the
    /// same authorization, the same per-key row and the same answer to every member - and switching
    /// one report off leaves the other seven offered.
    /// </summary>
    [Theory]
    [InlineData(MenuVisibilityKeys.ReportsBookkeeping)]
    [InlineData(MenuVisibilityKeys.ReportsDaily)]
    [InlineData(MenuVisibilityKeys.ReportsTransactions)]
    [InlineData(MenuVisibilityKeys.ReportsReconciliation)]
    [InlineData(MenuVisibilityKeys.ReportsMachines)]
    [InlineData(MenuVisibilityKeys.ReportsProducts)]
    [InlineData(MenuVisibilityKeys.ReportsSiteCommissions)]
    [InlineData(MenuVisibilityKeys.ReportsGst)]
    public async Task The_administrator_switches_one_report_off_and_every_member_is_told_about_that_one(string key)
    {
        var off = await PutAsync(_factory.AsAdministrator(), key, visible: false);

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var stored = await ReadOptionAsync(off);
        Assert.Equal(key, stored.Key);
        Assert.Equal("Reports", stored.Group);
        Assert.False(stored.Visible);
        var row = Assert.Single(await _factory.MenuVisibilityRowsAsync());
        Assert.Equal(key, row.Key);
        Assert.False(row.Visible);
        Assert.Equal([key], await SwitchedOffKeysAsync());

        var on = await PutAsync(_factory.AsAdministrator(), key, visible: true);

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await ReadOptionAsync(on)).Visible);
        Assert.Empty(await SwitchedOffKeysAsync());
    }

    /// <summary>
    /// The administrator switches one destination off and on again, and each committed answer is the
    /// one the next read reports - from a separate request, which is what "persisted across reloads
    /// and sessions" means at this boundary. One destination stays one row throughout.
    /// </summary>
    [Fact]
    public async Task The_administrator_switches_one_destination_off_and_on_and_each_answer_is_reported_back()
    {
        var off = await PutAsync(_factory.AsAdministrator(), MenuVisibilityKeys.Imports, visible: false);

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var stored = await ReadOptionAsync(off);
        Assert.Equal(MenuVisibilityKeys.Imports, stored.Key);
        Assert.Equal("Imports", stored.Label);
        Assert.False(stored.Visible);
        Assert.False(await ReportedVisibleAsync(MenuVisibilityKeys.Imports));
        var row = Assert.Single(await _factory.MenuVisibilityRowsAsync());
        Assert.Equal(MenuVisibilityKeys.Imports, row.Key);
        Assert.False(row.Visible);

        var on = await PutAsync(_factory.AsAdministrator(), MenuVisibilityKeys.Imports, visible: true);

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await ReadOptionAsync(on)).Visible);
        Assert.True(await ReportedVisibleAsync(MenuVisibilityKeys.Imports));
        Assert.True(Assert.Single(await _factory.MenuVisibilityRowsAsync()).Visible);
    }

    /// <summary>
    /// The acceptance criterion "a second write to a different key does not overwrite the first": two
    /// destinations are switched off one after the other, and both are off afterwards, as two
    /// independent rows. One row per destination is what makes that true however the two requests
    /// interleave - neither statement can reach the other's row.
    /// </summary>
    [Fact]
    public async Task Two_destinations_are_switched_independently_and_neither_overwrites_the_other()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(
                _factory.AsAdministrator(),
                MenuVisibilityKeys.AvcoTransition,
                visible: false)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(
                _factory.AsAdministrator(),
                MenuVisibilityKeys.CostingRepair,
                visible: false)).StatusCode);

        Assert.Equal(
            [MenuVisibilityKeys.AvcoTransition, MenuVisibilityKeys.CostingRepair],
            (await _factory.MenuVisibilityRowsAsync()).Select(row => row.Key).ToArray());
        Assert.False(await ReportedVisibleAsync(MenuVisibilityKeys.AvcoTransition));
        Assert.False(await ReportedVisibleAsync(MenuVisibilityKeys.CostingRepair));

        // And switching one back on leaves the other exactly as it was.
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(
                _factory.AsAdministrator(),
                MenuVisibilityKeys.AvcoTransition,
                visible: true)).StatusCode);

        Assert.True(await ReportedVisibleAsync(MenuVisibilityKeys.AvcoTransition));
        Assert.False(await ReportedVisibleAsync(MenuVisibilityKeys.CostingRepair));
    }

    /// <summary>
    /// A key the catalogue does not declare is refused and nothing is stored: the four Admin
    /// destinations that are deliberately never switchable, a Reports key #1153 has not decided, a
    /// key spelled differently, and an invented one.
    /// </summary>
    [Theory]
    [InlineData("admin.users")]
    [InlineData("admin.roles")]
    [InlineData("admin.business-settings")]
    [InlineData("admin.nayax-settings")]
    [InlineData("reports.sales")]
    [InlineData("Admin.Imports")]
    [InlineData("whatever")]
    public async Task An_unknown_key_is_refused_and_nothing_is_stored(string key)
    {
        var response = await PutAsync(_factory.AsAdministrator(), key, visible: false);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await _factory.MenuVisibilityRowsAsync());
        Assert.Empty(await SwitchedOffKeysAsync());
    }

    /// <summary>
    /// The acceptance criterion "any caller other than the platform administrator is refused", asked
    /// of the strongest business role there is and of three other callers, on both endpoints. An
    /// Owner holds every capability the application has and still cannot read or change this: a
    /// business role is not platform authority, and neither endpoint declares a capability any role
    /// could satisfy.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.ApplicantObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task A_caller_who_is_not_the_configured_administrator_is_refused_on_both_endpoints(
        string objectId)
    {
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.Authenticated(objectId).GetAsync(MenuVisibilityUrl)).StatusCode);

        var write = await PutAsync(
            _factory.Authenticated(objectId),
            MenuVisibilityKeys.Imports,
            visible: false);

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        // And a Reports key is the same surface, with nothing of its own: the same refusal (#1153).
        var report = await PutAsync(
            _factory.Authenticated(objectId),
            MenuVisibilityKeys.ReportsBookkeeping,
            visible: false);

        Assert.Equal(HttpStatusCode.Forbidden, report.StatusCode);
        Assert.Empty(await _factory.MenuVisibilityRowsAsync());
        Assert.Empty(await SwitchedOffKeysAsync());
    }

    /// <summary>
    /// A refusal leaves a destination that <em>has</em> been switched exactly as it was: the rejected
    /// write is not a write, in either direction. Switching one back on is the thing an ordinary
    /// member might most want to do, so that is the direction asserted here.
    /// </summary>
    [Fact]
    public async Task A_refused_write_leaves_an_already_switched_off_destination_switched_off()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(
                _factory.AsAdministrator(),
                MenuVisibilityKeys.Imports,
                visible: false)).StatusCode);

        var refused = await PutAsync(
            _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId),
            MenuVisibilityKeys.Imports,
            visible: true);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(Assert.Single(await _factory.MenuVisibilityRowsAsync()).Visible);
        Assert.False(await ReportedVisibleAsync(MenuVisibilityKeys.Imports));
    }

    /// <summary>
    /// <c>Visible</c> is a <c>bool</c>, so a body that omits it would otherwise bind to
    /// <c>false</c> indistinguishably from an explicit <c>visible: false</c> and silently switch the
    /// destination off. <c>JsonRequiredAttribute</c> refuses the under-posted body instead.
    /// </summary>
    [Fact]
    public async Task A_request_without_the_visible_field_is_refused_and_writes_nothing()
    {
        var response = await _factory
            .AsAdministrator()
            .PutAsJsonAsync($"{MenuVisibilityUrl}/{MenuVisibilityKeys.Imports}", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _factory.MenuVisibilityRowsAsync());
    }

    /// <summary>
    /// An anonymous request is refused by authentication with <c>401</c>, not turned into a
    /// <c>403</c>: "you are not signed in" and "you are signed in and not the administrator" stay
    /// different answers.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized_and_writes_nothing()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync(MenuVisibilityUrl)).StatusCode);

        var write = await PutAsync(
            _factory.CreateClient(),
            MenuVisibilityKeys.Imports,
            visible: false);

        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
        Assert.Empty(await _factory.MenuVisibilityRowsAsync());
    }

    /// <summary>
    /// The delegated scope is required of the administrator too: platform authority is an additional
    /// requirement on a delegated API call, never a replacement for one.
    /// </summary>
    [Fact]
    public async Task The_administrator_without_the_delegated_scope_is_refused_and_writes_nothing()
    {
        var client = _factory.WithoutScope(SuperAdminBusinessesApiFactory.AdministratorObjectId);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(MenuVisibilityUrl)).StatusCode);

        var write = await PutAsync(
            _factory.WithoutScope(SuperAdminBusinessesApiFactory.AdministratorObjectId),
            MenuVisibilityKeys.Imports,
            visible: false);

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Empty(await _factory.MenuVisibilityRowsAsync());
    }

    /// <summary>
    /// A member who presents a support-session id reaches nothing here either, and the refusal is
    /// the platform-admin one rather than anything about the session: these are
    /// <c>PlatformDiagnosticsEndpointAttribute</c> endpoints, so the <c>X-Support-Session</c> header
    /// is never acted on at all. A member who sets the header is still an ordinary member.
    /// </summary>
    [Fact]
    public async Task A_member_presenting_a_support_session_id_is_still_refused()
    {
        const string SessionId = "a-seeded-support-session-id-1148";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            SessionId);

        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        client.DefaultRequestHeaders.Add("X-Support-Session", SessionId);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(MenuVisibilityUrl)).StatusCode);

        var write = await PutAsync(client, MenuVisibilityKeys.Imports, visible: false);

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        var report = await PutAsync(client, MenuVisibilityKeys.ReportsDaily, visible: false);

        Assert.Equal(HttpStatusCode.Forbidden, report.StatusCode);
        Assert.Empty(await _factory.MenuVisibilityRowsAsync());
    }

    /// <summary>
    /// The access contract: every signed-in caller is told which destinations are switched off, and
    /// <strong>the capability set is byte-for-byte identical</strong> whatever the switches say. The
    /// legacy <c>historicalGstClassificationVisible</c> field is still reported beside it and is
    /// derived from its own key, which is what keeps the current frontend working while the API ships
    /// first.
    /// </summary>
    [Fact]
    public async Task Me_access_reports_the_switched_off_keys_without_changing_one_capability()
    {
        string[] capabilitiesBefore;
        using (var before = await AccessAsync())
        {
            capabilitiesBefore = Capabilities(before);

            Assert.Empty(SwitchedOffKeys(before));
            Assert.True(
                before.RootElement.GetProperty("historicalGstClassificationVisible").GetBoolean());
        }

        foreach (var key in new[]
        {
            MenuVisibilityKeys.HistoricalGstClassification,
            MenuVisibilityKeys.SiteCommissionAgreements,
        })
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await PutAsync(_factory.AsAdministrator(), key, visible: false)).StatusCode);
        }

        using var after = await AccessAsync();

        Assert.Equal(
            [
                MenuVisibilityKeys.SiteCommissionAgreements,
                MenuVisibilityKeys.HistoricalGstClassification,
            ],
            SwitchedOffKeys(after));
        // Derived from the catalogue key, and still reported (issue #1148's rollout criterion).
        Assert.False(after.RootElement.GetProperty("historicalGstClassificationVisible").GetBoolean());
        Assert.Equal(capabilitiesBefore, Capabilities(after));
    }

    /// <summary>
    /// <strong>The switches are visibility only.</strong> With a destination switched off, that
    /// page's own endpoints keep authorizing exactly as they did: the Owner's <c>Data.Repair</c>
    /// still reaches the classification preview. The issue excludes every endpoint authorization,
    /// plan and provider change explicitly, and this is the test that would fail if a switch ever
    /// started enforcing one - which would also have made it a permission change rather than a menu
    /// one.
    /// </summary>
    [Fact]
    public async Task Switching_a_destination_off_does_not_restrict_its_endpoints()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(
                _factory.AsAdministrator(),
                MenuVisibilityKeys.HistoricalGstClassification,
                visible: false)).StatusCode);

        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        var preview = await owner.PostAsync(PreviewUrl, content: null);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    /// <summary>
    /// The same criterion for a report (issue #1153): a switched-off report's own endpoint answers
    /// the Owner exactly what it answered before, with its capability, plan and provider checks
    /// untouched. The answer is compared with the one from before the switch rather than asserted as
    /// a fixed status, because what those checks decide is this business's business - the point is
    /// that the switch decides none of it.
    /// </summary>
    [Fact]
    public async Task Switching_a_report_off_does_not_change_what_its_endpoint_answers()
    {
        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        var before = await owner.GetAsync(BookkeepingReportUrl);

        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(
                _factory.AsAdministrator(),
                MenuVisibilityKeys.ReportsBookkeeping,
                visible: false)).StatusCode);

        var after = await _factory
            .Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)
            .GetAsync(BookkeepingReportUrl);

        Assert.Equal(before.StatusCode, after.StatusCode);
        Assert.Equal(
            await before.Content.ReadAsStringAsync(),
            await after.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// One structured audit event per committed change, naming the acting administrator, the
    /// destination and the state it ended in - and nothing about a business, a member or anybody's
    /// data, because this setting is about none of them.
    /// </summary>
    [Fact]
    public async Task Each_committed_change_is_audited_with_the_actor_the_key_and_the_resulting_state()
    {
        _factory.ClearLog();

        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(
                _factory.AsAdministrator(),
                MenuVisibilityKeys.Imports,
                visible: false)).StatusCode);

        var audit = Assert.Single(AuditEntries());

        Assert.Contains(
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MenuVisibilityKeys.Imports, audit, StringComparison.Ordinal);
        Assert.Contains("visible False", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.TradingBusinessName,
            audit,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A refused write, and a write for a key the catalogue does not declare, are not audited,
    /// because nothing happened: an audit trail that recorded attempts as changes would be reporting
    /// a mutation the database never saw.
    /// </summary>
    [Fact]
    public async Task A_refused_write_and_an_unknown_key_are_not_audited()
    {
        _factory.ClearLog();

        await PutAsync(
            _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId),
            MenuVisibilityKeys.Imports,
            visible: false);
        await PutAsync(_factory.AsAdministrator(), "admin.users", visible: false);

        Assert.Empty(AuditEntries());
    }

    private Task<HttpResponseMessage> PutAsync(HttpClient client, string key, bool visible) =>
        client.PutAsJsonAsync(
            $"{MenuVisibilityUrl}/{key}",
            new Dictionary<string, bool> { ["visible"] = visible });

    private List<string> AuditEntries() =>
        [.. _factory
            .LoggedEntries()
            .Where(entry => entry.Contains("menu visibility audit", StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// What the trading business's own Owner is told by <c>GET /api/me/access</c> - the one read
    /// through which a member learns what the application offers them.
    /// </summary>
    private async Task<JsonDocument> AccessAsync()
    {
        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        var response = await client.GetAsync(AccessUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<string[]> SwitchedOffKeysAsync()
    {
        using var access = await AccessAsync();
        return SwitchedOffKeys(access);
    }

    private async Task<bool> ReportedVisibleAsync(string key) =>
        !(await SwitchedOffKeysAsync()).Contains(key, StringComparer.Ordinal);

    private static string[] SwitchedOffKeys(JsonDocument access) =>
        [.. access.RootElement
            .GetProperty("menuVisibility")
            .GetProperty("switchedOffKeys")
            .EnumerateArray()
            .Select(key => key.GetString()!)];

    private static string[] Capabilities(JsonDocument access) =>
        [.. access.RootElement
            .GetProperty("capabilities")
            .EnumerateArray()
            .Select(capability => capability.GetString()!)];

    private static async Task<List<Option>> ReadOptionsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. document.RootElement.GetProperty("options").EnumerateArray().Select(Read)];
    }

    private static async Task<Option> ReadOptionAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Read(document.RootElement);
    }

    private static Option Read(JsonElement option) =>
        new(
            option.GetProperty("key").GetString()!,
            option.GetProperty("group").GetString()!,
            option.GetProperty("label").GetString()!,
            option.GetProperty("visible").GetBoolean());

    private sealed record Option(string Key, string Group, string Label, bool Visible);
}
