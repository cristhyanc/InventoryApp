using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Application.PlatformOptions;
using Inventory.Infrastructure.Models;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The switchable Admin option's visibility over real HTTP (issue #873): who may change it, what it
/// defaults to, what every member is told, and what a refused write leaves behind.
///
/// <para><strong>Since issue #1148 this is the legacy endpoint's compatibility contract.</strong>
/// <c>PUT /api/super-admin/option-visibility</c> and the <c>historicalGstClassificationVisible</c>
/// field are kept for the rollout, because the API deploys before the frontend, and both now work
/// over the keyed menu-visibility storage: the write stores the
/// <c>admin.historical-gst-classification</c> row and the superseded <c>PlatformOptionVisibility</c>
/// table is never written again. Every assertion below is the behaviour issue #873 specified; what
/// changed is only where the answer is stored, which is why the row assertions read the new table
/// and prove the old one stays empty. The new surface's own contract is
/// <c>SuperAdminMenuVisibilityApiTests</c>.</para>
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>,
/// the platform-admin policy, <c>[RequiredScope]</c>, <c>RolePreviewMiddleware</c>,
/// <c>BusinessScopeMiddleware</c>'s independent re-check of the same policy, the tenant query
/// filters and <c>BusinessOwnershipEnforcer</c> - so the authorization under test is the real one.
/// Only the authentication is synthetic.</para>
///
/// <para>The <em>member</em> half of the contract is asserted through <c>GET /api/me/access</c>, by
/// the trading business's own Owner, because that is where a member learns what the application
/// offers. The setting is shared by the whole application, so there is no business in either
/// request: the administrator names none when writing it, and the member's read reports the one
/// shared answer.</para>
/// </summary>
public class SuperAdminOptionVisibilityApiTests
    : IClassFixture<SuperAdminBusinessesApiFactory>, IAsyncLifetime
{
    private const string VisibilityUrl = "/api/super-admin/option-visibility";
    private const string AccessUrl = "/api/me/access";
    private const string PreviewUrl = "/api/admin/historical-gst-classification/preview";
    private const string VisibleProperty = "historicalGstClassificationVisible";

    private readonly SuperAdminBusinessesApiFactory _factory;

    public SuperAdminOptionVisibilityApiTests(SuperAdminBusinessesApiFactory factory)
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
    /// The default an existing installation keeps: nothing has ever been switched off, so there is
    /// no stored row at all and the option is offered. This is the acceptance criterion "existing
    /// installations default to On to preserve current visibility until explicitly switched Off",
    /// and it is asserted on the member read rather than on the absence of the row, because what a
    /// member is told is the behaviour that matters.
    /// </summary>
    [Fact]
    public async Task The_option_is_offered_by_default_with_no_stored_setting_at_all()
    {
        Assert.Empty(await StoredRowsAsync());

        Assert.True(await VisibleToTheOwnerAsync());
    }

    /// <summary>
    /// The administrator switches it off and on again, and each committed answer is the one every
    /// member read reports afterwards - from a separate request, which is what "persisted across
    /// reloads and sessions" means at this boundary. The stored state stays one row throughout: the
    /// setting is a single shared answer, not a growing list of them.
    /// </summary>
    [Fact]
    public async Task The_administrator_switches_it_off_and_on_and_every_member_read_reports_the_stored_answer()
    {
        var off = await PutAsync(_factory.AsAdministrator(), visible: false);

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(await ReadVisibleAsync(off));
        Assert.False(await VisibleToTheOwnerAsync());
        Assert.False(await StoredVisibleAsync());

        var on = await PutAsync(_factory.AsAdministrator(), visible: true);

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True(await ReadVisibleAsync(on));
        Assert.True(await VisibleToTheOwnerAsync());
        Assert.True(await StoredVisibleAsync());
    }

    /// <summary>
    /// The acceptance criterion "a non-super-admin direct update request is rejected without
    /// mutation", asked of the strongest business role there is and of three other callers. An Owner
    /// holds every capability the application has and still cannot change this: a business role is
    /// not platform authority, and this endpoint declares no capability any role could satisfy.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.ApplicantObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task A_caller_who_is_not_the_configured_administrator_is_refused_and_writes_nothing(
        string objectId)
    {
        var response = await PutAsync(_factory.Authenticated(objectId), visible: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await StoredRowsAsync());
        Assert.True(await VisibleToTheOwnerAsync());
    }

    /// <summary>
    /// A refusal leaves a setting that <em>has</em> been switched exactly as it was: the rejected
    /// write is not a write, in either direction. Switching it back on is the one thing an ordinary
    /// member might most want to do, so that is the direction asserted here.
    /// </summary>
    [Fact]
    public async Task A_refused_write_leaves_an_already_switched_off_setting_switched_off()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(_factory.AsAdministrator(), visible: false)).StatusCode);

        var refused = await PutAsync(
            _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId),
            visible: true);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(await StoredVisibleAsync());
        Assert.False(await VisibleToTheOwnerAsync());
    }

    /// <summary>
    /// <c>HistoricalGstClassificationVisible</c> is a <c>bool</c>, so a body that omits it would
    /// otherwise bind to <c>false</c> indistinguishably from an explicit
    /// <c>historicalGstClassificationVisible: false</c> and silently switch the option off.
    /// <c>JsonRequiredAttribute</c> refuses the under-posted body instead.
    /// </summary>
    [Fact]
    public async Task A_request_without_the_visible_field_is_refused_and_writes_nothing()
    {
        var response = await _factory.AsAdministrator().PutAsJsonAsync(VisibilityUrl, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await StoredRowsAsync());
        Assert.True(await VisibleToTheOwnerAsync());
    }

    /// <summary>
    /// An anonymous request is refused by authentication with <c>401</c>, not turned into a
    /// <c>403</c>: "you are not signed in" and "you are signed in and not the administrator" stay
    /// different answers.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized_and_writes_nothing()
    {
        var response = await PutAsync(_factory.CreateClient(), visible: false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await StoredRowsAsync());
    }

    /// <summary>
    /// The delegated scope is required of the administrator too: platform authority is an additional
    /// requirement on a delegated API call, never a replacement for one.
    /// </summary>
    [Fact]
    public async Task The_administrator_without_the_delegated_scope_is_refused_and_writes_nothing()
    {
        var response = await PutAsync(
            _factory.WithoutScope(SuperAdminBusinessesApiFactory.AdministratorObjectId),
            visible: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await StoredRowsAsync());
    }

    /// <summary>
    /// A member who presents a support-session id reaches nothing here either, and the refusal is
    /// the platform-admin one rather than anything about the session: this is a
    /// <c>PlatformDiagnosticsEndpointAttribute</c> endpoint, so the <c>X-Support-Session</c> header
    /// is never acted on at all. A member who sets the header is still an ordinary member.
    /// </summary>
    [Fact]
    public async Task A_member_presenting_a_support_session_id_is_still_refused()
    {
        const string SessionId = "a-seeded-support-session-id";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            SessionId);

        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        client.DefaultRequestHeaders.Add("X-Support-Session", SessionId);

        var response = await PutAsync(client, visible: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await StoredRowsAsync());
    }

    /// <summary>
    /// <strong>The switch is visibility only.</strong> With the option switched off, the maintenance
    /// tool's own endpoints keep authorizing exactly as issue #433 left them: the Owner's
    /// <c>Data.Repair</c> still reaches the preview. The issue excludes new direct-route or API
    /// restrictions explicitly, and this is the test that would fail if the switch ever started
    /// enforcing one - which would also have made it a permission change rather than a menu one.
    /// </summary>
    [Fact]
    public async Task Switching_the_option_off_does_not_restrict_the_classification_tool_itself()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(_factory.AsAdministrator(), visible: false)).StatusCode);

        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        var preview = await owner.PostAsync(PreviewUrl, content: null);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    /// <summary>
    /// One structured audit event per committed change, naming the acting administrator and the
    /// state the setting ended in - and nothing about a business, a member or anybody's data, because
    /// this setting is about none of them.
    /// </summary>
    [Fact]
    public async Task Each_committed_change_is_audited_with_the_actor_and_the_resulting_state()
    {
        _factory.ClearLog();

        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(_factory.AsAdministrator(), visible: false)).StatusCode);

        var audits = _factory
            .LoggedEntries()
            .Where(entry => entry.Contains("menu visibility audit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var audit = Assert.Single(audits);
        Assert.Contains(
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            audit,
            StringComparison.OrdinalIgnoreCase);
        // The destination the legacy endpoint writes is named in the trail like any other
        // (issue #1148): one event, one key, one resulting state.
        Assert.Contains(
            MenuVisibilityKeys.HistoricalGstClassification,
            audit,
            StringComparison.Ordinal);
        Assert.Contains("visible False", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.TradingBusinessName,
            audit,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A refused write is not audited, because nothing happened: an audit trail that recorded
    /// attempts as changes would be reporting a mutation that the database never saw.
    /// </summary>
    [Fact]
    public async Task A_refused_write_is_not_audited()
    {
        _factory.ClearLog();

        await PutAsync(
            _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId),
            visible: false);

        Assert.DoesNotContain(
            _factory.LoggedEntries(),
            entry => entry.Contains("menu visibility audit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The compatibility criterion of issue #1148, in one request each way: what this legacy
    /// endpoint writes is the <em>same stored answer</em> the new surfaces report - the catalogue key
    /// on <c>GET /api/super-admin/menu-visibility</c>, and the switched-off list on
    /// <c>GET /api/me/access</c> beside the legacy boolean it is derived from. There are not two
    /// settings; there is one, with two ways in during the rollout.
    /// </summary>
    [Fact]
    public async Task The_legacy_write_is_the_answer_the_new_surfaces_report()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(_factory.AsAdministrator(), visible: false)).StatusCode);

        using var catalogue = JsonDocument.Parse(
            await (await _factory.AsAdministrator().GetAsync("/api/super-admin/menu-visibility"))
                .Content.ReadAsStringAsync());
        var option = catalogue.RootElement
            .GetProperty("options")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("key").GetString()
                == MenuVisibilityKeys.HistoricalGstClassification);

        Assert.False(option.GetProperty("visible").GetBoolean());

        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        using var access = JsonDocument.Parse(
            await (await owner.GetAsync(AccessUrl)).Content.ReadAsStringAsync());

        Assert.Equal(
            [MenuVisibilityKeys.HistoricalGstClassification],
            access.RootElement
                .GetProperty("menuVisibility")
                .GetProperty("switchedOffKeys")
                .EnumerateArray()
                .Select(key => key.GetString())
                .ToArray());
        Assert.False(access.RootElement.GetProperty(VisibleProperty).GetBoolean());
    }

    /// <summary>
    /// Every stored menu-visibility row (issue #1148) - where this endpoint's answer now lives - and
    /// the proof that the superseded single-option table is not written by it at all.
    /// </summary>
    private async Task<IReadOnlyList<PlatformMenuVisibility>> StoredRowsAsync()
    {
        Assert.Empty(await _factory.OptionVisibilityRowsAsync());
        return await _factory.MenuVisibilityRowsAsync();
    }

    /// <summary>
    /// The one destination this legacy endpoint writes, as it is stored: one row, under the
    /// catalogue key, and nothing else switched.
    /// </summary>
    private async Task<bool> StoredVisibleAsync()
    {
        var row = Assert.Single(await StoredRowsAsync());

        Assert.Equal(MenuVisibilityKeys.HistoricalGstClassification, row.Key);
        return row.Visible;
    }

    private Task<HttpResponseMessage> PutAsync(HttpClient client, bool visible) =>
        client.PutAsJsonAsync(
            VisibilityUrl,
            new Dictionary<string, bool> { [VisibleProperty] = visible });

    /// <summary>
    /// What the trading business's own Owner is told by <c>GET /api/me/access</c> - the one read
    /// through which a member learns whether the option is offered.
    /// </summary>
    private async Task<bool> VisibleToTheOwnerAsync()
    {
        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        var response = await client.GetAsync(AccessUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadVisibleAsync(response);
    }

    private static async Task<bool> ReadVisibleAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(VisibleProperty).GetBoolean();
    }
}
