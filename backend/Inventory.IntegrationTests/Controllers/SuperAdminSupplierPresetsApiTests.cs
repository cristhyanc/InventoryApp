using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inventory.Domain.Suppliers;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Super Admin supplier presets API over real HTTP (issue #848): who may reach it, what it
/// reports, what each write stores, and what it refuses.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>,
/// the platform-admin policy, <c>[RequiredScope]</c>, <c>RolePreviewMiddleware</c>,
/// <c>BusinessScopeMiddleware</c>'s independent re-check of the same policy, the tenant query
/// filters and <c>BusinessOwnershipEnforcer</c> - so the authorization under test is the real one.
/// Only the authentication is synthetic.</para>
/// </summary>
public class SuperAdminSupplierPresetsApiTests
    : IClassFixture<SuperAdminBusinessesApiFactory>, IAsyncLifetime
{
    private const string PresetsUrl = "/api/super-admin/supplier-presets";

    private readonly SuperAdminBusinessesApiFactory _factory;

    public SuperAdminSupplierPresetsApiTests(SuperAdminBusinessesApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetAsync();
        await _factory.ResetSupplierPresetsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    #region Who may reach it

    /// <summary>
    /// The acceptance criterion "Business Owners/Admins/members get 403", asked of every endpoint on
    /// the surface. An Owner holds the strongest business role there is and still reaches none of it:
    /// a business role is not platform authority, and this controller declares no capability any role
    /// could satisfy.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.ApplicantObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task Every_endpoint_refuses_a_caller_who_is_not_the_configured_administrator(
        string objectId)
    {
        var client = _factory.Authenticated(objectId);

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    /// <summary>
    /// An anonymous request is refused by authentication with <c>401</c>, not turned into a
    /// <c>403</c>: "you are not signed in" and "you are signed in and not the administrator" stay
    /// different answers.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized()
    {
        var client = _factory.CreateClient();

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// The delegated scope is required of the administrator too: platform authority is an additional
    /// requirement on a delegated API call, never a replacement for one.
    /// </summary>
    [Fact]
    public async Task The_administrator_without_the_delegated_scope_is_refused()
    {
        var client = _factory.WithoutScope(SuperAdminBusinessesApiFactory.AdministratorObjectId);

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    /// <summary>
    /// A member who presents a support-session id reaches nothing here either, and the refusal is the
    /// platform-admin one rather than anything about the session: this is a
    /// <c>PlatformDiagnosticsEndpointAttribute</c> endpoint, so the <c>X-Support-Session</c> header is
    /// never acted on at all (docs/architecture.md § Support sessions in the request pipeline). A
    /// member who sets the header is still an ordinary member.
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

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    /// <summary>
    /// A role preview is read-only everywhere, this surface included (issue #720): the two writes are
    /// refused with the preview's own stable code before the endpoint runs, and nothing is stored.
    /// The read is unaffected, because a preview narrows what a caller may do and the administrator's
    /// own authority is not a business role.
    /// </summary>
    [Theory]
    [InlineData("Owner")]
    [InlineData("Manager")]
    [InlineData("Operator")]
    public async Task A_role_preview_write_is_refused_as_read_only_and_stores_nothing(string role)
    {
        var client = _factory.AsAdministrator();
        client.DefaultRequestHeaders.Add("X-Role-Preview", role);

        var create = await client.PostAsJsonAsync(PresetsUrl, new { name = "Aldi" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("read_only_role_preview", await CodeAsync(create));

        var presetId = (await _factory.SupplierPresetsAsync())[0].Id;
        var update = await client.PutAsJsonAsync(
            PresetUrl(presetId),
            new { name = "Renamed", isEnabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal("read_only_role_preview", await CodeAsync(update));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    /// <summary>
    /// The other half of the support-session criterion, stated rather than implied: the configured
    /// administrator who holds a <em>live</em> session of their own still reaches this surface, writes
    /// included.
    ///
    /// <para>That is the existing design of this endpoint class rather than a choice made here. The
    /// controller carries <c>PlatformDiagnosticsEndpointAttribute</c>, so
    /// <c>BusinessScopeMiddleware</c> decides the request on the platform-admin policy before it
    /// resolves a session at all (docs/architecture.md § Super Admin supplier presets and § Support
    /// sessions in the request pipeline): the control surfaces are reached <em>as</em> the
    /// administrator, which is also why they neither need nor accept the header. The request still
    /// carries a <strong>denied</strong> <c>BusinessScope</c>, so nothing tenant-owned is reachable
    /// through it: the session changes nothing about what this surface reads or writes. On an
    /// <em>ordinary</em> business endpoint the same header still does what issue #551 gives it -
    /// resolves that one business, read-only - and the presets are not that business's suppliers,
    /// which the tail of this test checks in the same request sequence.</para>
    ///
    /// <para>Issue #848's criterion says "support sessions ... get <c>403</c>" without distinguishing
    /// the caller, so this test pins the answer the application actually gives instead of leaving it
    /// to prose. Refusing this surface <em>because</em> its caller holds a session would be a
    /// per-surface refusal rule contradicting that design, which is a decision for a human and is
    /// recorded as such on PR #859.</para>
    /// </summary>
    [Fact]
    public async Task The_administrator_holding_a_live_support_session_still_reaches_this_surface()
    {
        const string SessionId = "a-live-support-session-id";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            SessionId);

        var client = _factory.AsAdministrator();
        client.DefaultRequestHeaders.Add("X-Support-Session", SessionId);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(PresetsUrl)).StatusCode);

        var create = await client.PostAsJsonAsync(PresetsUrl, new { name = "Aldi" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await ReadJsonAsync(create)).RootElement.GetProperty("id").GetInt32();

        var edit = await client.PutAsJsonAsync(
            PresetUrl(created),
            new { name = "Aldi", url = (string?)null, isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var stored = (await _factory.SupplierPresetsAsync()).Single(preset => preset.Id == created);
        Assert.False(stored.IsEnabled);

        // The session is still a session: on an ordinary business endpoint the same header resolves
        // that business read-only (issue #551), and what it reads there is the business's own
        // suppliers - of which it has none. A preset is not a supplier, in a support session either.
        var suppliers = await ReadJsonAsync(await client.GetAsync("/api/suppliers"));
        Assert.Empty(suppliers.RootElement.EnumerateArray());
    }

    /// <summary>
    /// The read half of the role-preview criterion, pinned for each previewable role: a previewed
    /// <c>GET</c> on this surface is unaffected and reports the same list, exactly as a previewed
    /// <c>GET</c> of <c>/api/super-admin/businesses</c> is unaffected
    /// (<c>RolePreviewApiTests.A_previewed_read_of_the_super_admin_surface_is_unaffected</c>). A
    /// preview narrows what a caller may do, and this controller declares no capability for it to
    /// narrow; what the preview does reach here is the read-only rule above, which refuses the two
    /// writes. Refusing the read only on this surface would be a rule confined to one controller,
    /// which issue #720 deliberately does not have - see the test above for the same open question.
    /// </summary>
    [Theory]
    [InlineData("Owner")]
    [InlineData("Manager")]
    [InlineData("Operator")]
    public async Task A_role_preview_read_is_unaffected(string role)
    {
        var client = _factory.AsAdministrator();
        client.DefaultRequestHeaders.Add("X-Role-Preview", role);

        var body = await ReadJsonAsync(await client.GetAsync(PresetsUrl));

        Assert.Equal(
            ["Big W", "Coles", "Costco", "Woolworths"],
            body.RootElement
                .EnumerateArray()
                .Select(preset => preset.GetProperty("name").GetString())
                .ToArray());
    }

    /// <summary>
    /// Reaching this surface grants nothing else: the administrator is a member of no business, so
    /// every business endpoint still refuses them - the suppliers endpoint included, which is the one
    /// a preset could be confused with.
    /// </summary>
    [Fact]
    public async Task The_administrator_still_reaches_no_business_endpoint()
    {
        var client = _factory.AsAdministrator();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/products")).StatusCode);
    }

    #endregion

    #region The list

    /// <summary>
    /// The shipped four, by name, with the six declared fields and nothing else. In particular the
    /// stored normalised name is not published: it is the unique index's key, not a field of the
    /// resource.
    /// </summary>
    [Fact]
    public async Task The_list_reports_every_preset_ordered_by_name()
    {
        var body = await ReadJsonAsync(await _factory.AsAdministrator().GetAsync(PresetsUrl));

        var presets = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(
            ["Big W", "Coles", "Costco", "Woolworths"],
            presets.Select(preset => preset.GetProperty("name").GetString()).ToArray());

        Assert.All(presets, preset =>
        {
            Assert.True(preset.GetProperty("isEnabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, preset.GetProperty("url").ValueKind);
            Assert.True(preset.GetProperty("id").GetInt32() > 0);
            Assert.Equal(
                ["createdAtUtc", "id", "isEnabled", "name", "updatedAtUtc", "url"],
                preset.EnumerateObject()
                    .Select(property => property.Name)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
        });
    }

    /// <summary>
    /// A disabled preset stays in the list, deliberately: a preset nobody can see is a preset nobody
    /// can re-enable.
    /// </summary>
    [Fact]
    public async Task The_list_reports_disabled_presets_too()
    {
        await _factory.SeedSupplierPresetAsync("Retired Wholesaler", isEnabled: false);

        var body = await ReadJsonAsync(await _factory.AsAdministrator().GetAsync(PresetsUrl));

        var retired = body.RootElement
            .EnumerateArray()
            .Single(preset => preset.GetProperty("name").GetString() == "Retired Wholesaler");

        Assert.False(retired.GetProperty("isEnabled").GetBoolean());
    }

    #endregion

    #region Creating

    [Fact]
    public async Task A_preset_is_created_enabled_with_its_name_trimmed_and_its_url_kept()
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            new { name = "  Aldi  ", url = " https://www.aldi.com.au " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = (await ReadJsonAsync(response)).RootElement;
        Assert.Equal("Aldi", created.GetProperty("name").GetString());
        Assert.Equal("https://www.aldi.com.au", created.GetProperty("url").GetString());
        Assert.True(created.GetProperty("isEnabled").GetBoolean());

        var stored = (await _factory.SupplierPresetsAsync())
            .Single(preset => preset.Name == "Aldi");
        Assert.Equal("ALDI", stored.NormalisedName);
        Assert.Equal("https://www.aldi.com.au", stored.Url);
        Assert.True(stored.IsEnabled);
        Assert.Equal(stored.CreatedAtUtc, stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task A_preset_may_be_created_without_a_website()
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            new { name = "Aldi", url = "   " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            JsonValueKind.Null,
            (await ReadJsonAsync(response)).RootElement.GetProperty("url").ValueKind);

        // Blank is stored as null rather than as an empty string: "no website" has one spelling.
        Assert.Null((await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Aldi").Url);
    }

    /// <summary>
    /// The duplicate rule, ignoring case and padding, with the Domain's own sentence. The caller gets
    /// <c>409</c> and the list is unchanged.
    /// </summary>
    [Theory]
    [InlineData("coles")]
    [InlineData("COLES")]
    [InlineData("  CoLeS  ")]
    public async Task A_duplicate_name_is_refused_with_a_conflict(string name)
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, new { name });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SupplierPresetPolicy.DuplicateNameMessage, await MessageAsync(response));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_refused(string? name)
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SupplierPresetPolicy.NameRequiredMessage, await MessageAsync(response));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    [Fact]
    public async Task A_name_longer_than_the_limit_is_refused()
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            new { name = new string('n', SupplierPresetPolicy.MaxNameLength + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SupplierPresetPolicy.NameTooLongMessage, await MessageAsync(response));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    [Theory]
    [InlineData("www.aldi.com.au")]
    [InlineData("aldi.com.au")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://files.aldi.com.au")]
    public async Task A_website_that_is_not_an_absolute_http_address_is_refused(string url)
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            new { name = "Aldi", url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SupplierUrlPolicy.InvalidMessage, await MessageAsync(response));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    [Fact]
    public async Task A_website_longer_than_the_limit_is_refused()
    {
        var tooLong = "https://example.test/"
            + new string('a', SupplierUrlPolicy.MaxLength + 1 - "https://example.test/".Length);

        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            new { name = "Aldi", url = tooLong });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SupplierUrlPolicy.TooLongMessage, await MessageAsync(response));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    /// <summary>
    /// Two creates of one name, differing only in case, leave one row and one <c>409</c>.
    ///
    /// <para>This is the ordinary path, where the second request's pre-check sees the first
    /// request's row. The path the unique index exists for - both pre-checks passing, and the index
    /// refusing the loser - is staged deterministically in
    /// <c>EfSupplierPresetStoreTests.A_create_whose_pre_check_missed_the_duplicate_is_still_a_conflict</c>,
    /// because racing two HTTP requests here would prove nothing on the run where they do not
    /// interleave: these tests share one in-memory SQLite connection, so genuinely parallel requests
    /// contend for it rather than for the index.</para>
    /// </summary>
    [Fact]
    public async Task A_second_create_of_one_name_leaves_one_row_and_one_conflict()
    {
        var first = await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, new { name = "Aldi" });
        var second = await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, new { name = "aldi" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(SupplierPresetPolicy.DuplicateNameMessage, await MessageAsync(second));

        Assert.Single(
            await _factory.SupplierPresetsAsync(),
            preset => preset.NormalisedName == "ALDI");
    }

    #endregion

    #region Editing

    [Fact]
    public async Task An_edit_stores_the_name_the_website_and_the_enabled_state()
    {
        var id = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Coles").Id;

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            new { name = " Coles Supermarkets ", url = "https://www.coles.com.au", isEnabled = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = (await ReadJsonAsync(response)).RootElement;
        Assert.Equal("Coles Supermarkets", updated.GetProperty("name").GetString());
        Assert.Equal("https://www.coles.com.au", updated.GetProperty("url").GetString());
        Assert.False(updated.GetProperty("isEnabled").GetBoolean());

        var stored = (await _factory.SupplierPresetsAsync()).Single(preset => preset.Id == id);
        Assert.Equal("COLES SUPERMARKETS", stored.NormalisedName);
        Assert.False(stored.IsEnabled);
        Assert.True(stored.UpdatedAtUtc >= stored.CreatedAtUtc);
    }

    /// <summary>
    /// Disabling and re-enabling is the same edit, which is why there is no separate toggle endpoint
    /// over the same row.
    /// </summary>
    [Fact]
    public async Task A_disabled_preset_can_be_enabled_again()
    {
        var id = await _factory.SeedSupplierPresetAsync("Retired Wholesaler", isEnabled: false);

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            new { name = "Retired Wholesaler", isEnabled = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await _factory.SupplierPresetsAsync()).Single(p => p.Id == id).IsEnabled);
    }

    /// <summary>
    /// A preset keeps its own name on an edit: the duplicate check excludes the row being edited, so
    /// changing only the website or only the enabled flag is not refused as a duplicate of itself.
    /// </summary>
    [Fact]
    public async Task An_edit_that_keeps_the_preset_s_own_name_is_not_a_duplicate()
    {
        var id = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Coles").Id;

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            new { name = "coles", url = "https://www.coles.com.au", isEnabled = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("coles", (await _factory.SupplierPresetsAsync()).Single(p => p.Id == id).Name);
    }

    [Fact]
    public async Task An_edit_to_another_preset_s_name_is_refused_with_a_conflict()
    {
        var id = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Coles").Id;

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            new { name = "woolworths", isEnabled = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SupplierPresetPolicy.DuplicateNameMessage, await MessageAsync(response));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    [Fact]
    public async Task An_edit_with_a_blank_name_is_refused_and_stores_nothing()
    {
        var id = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Coles").Id;

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            new { name = "   ", isEnabled = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SupplierPresetPolicy.NameRequiredMessage, await MessageAsync(response));

        var stored = (await _factory.SupplierPresetsAsync()).Single(preset => preset.Id == id);
        Assert.Equal("Coles", stored.Name);
        Assert.True(stored.IsEnabled);
    }

    [Fact]
    public async Task An_edit_of_an_unknown_preset_is_not_found()
    {
        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(999999),
            new { name = "Aldi", isEnabled = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertOnlyTheShippedPresetsExistAsync();
    }

    /// <summary>
    /// <c>IsEnabled</c> is a <c>bool</c>, so a body that omits it would otherwise bind to
    /// <c>false</c> indistinguishably from an explicit <c>isEnabled: false</c> and silently disable
    /// the preset. <c>JsonRequiredAttribute</c> refuses the under-posted body instead.
    /// </summary>
    [Fact]
    public async Task An_edit_without_isEnabled_is_refused_and_changes_nothing()
    {
        var preset = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Coles");

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(preset.Id),
            new { name = "Coles" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var stored = (await _factory.SupplierPresetsAsync()).Single(p => p.Id == preset.Id);
        Assert.Equal(preset.IsEnabled, stored.IsEnabled);
        Assert.Equal(preset.UpdatedAtUtc, stored.UpdatedAtUtc);
    }

    /// <summary>
    /// There is no delete, and no preset can be removed through this surface. The controller routes
    /// no <c>DELETE</c> action, so the request matches no action at all; the method-not-allowed
    /// candidate routing selects carries none of the controller's authorization metadata either, so
    /// the business-scope middleware refuses the administrator - who is a member of no business -
    /// before any status about the method is produced. What matters to the criterion is the same
    /// either way: it is not a success, and the list is unchanged.
    /// </summary>
    [Fact]
    public async Task There_is_no_delete_endpoint()
    {
        var id = (await _factory.SupplierPresetsAsync())[0].Id;

        var response = await _factory.AsAdministrator().DeleteAsync(PresetUrl(id));

        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains(
            response.StatusCode,
            new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        await AssertOnlyTheShippedPresetsExistAsync();
    }

    #endregion

    #region The audit trail

    /// <summary>
    /// Each committed write emits one event with the actor, the preset's key, the change and the
    /// resulting state - and never the preset's name or its website, which is the restraint the two
    /// platform-business audits already show about a business's name and contact address.
    /// </summary>
    [Fact]
    public async Task A_committed_write_is_audited_without_the_name_or_the_website()
    {
        _factory.ClearLog();

        var created = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            new { name = "Aldi Stores", url = "https://www.aldi.com.au" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Aldi Stores").Id;
        var updated = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            new { name = "Aldi Stores", url = "https://www.aldi.com.au", isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var audit = _factory.LoggedEntries()
            .Where(entry => entry.Contains("Platform supplier preset audit", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, audit.Count);
        Assert.All(audit, entry =>
        {
            Assert.Contains(SuperAdminBusinessesApiFactory.AdministratorObjectId, entry, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(id.ToString(CultureInfo.InvariantCulture), entry, StringComparison.Ordinal);
            Assert.DoesNotContain("Aldi", entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aldi.com.au", entry, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Contains("change Created", audit[0], StringComparison.Ordinal);
        Assert.Contains("enabled True", audit[0], StringComparison.Ordinal);
        Assert.Contains("change Updated", audit[1], StringComparison.Ordinal);
        Assert.Contains("enabled False", audit[1], StringComparison.Ordinal);
    }

    /// <summary>A refused write is not audited, because nothing happened.</summary>
    [Fact]
    public async Task A_refused_write_is_not_audited()
    {
        _factory.ClearLog();

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, new { name = "coles" })).StatusCode);

        Assert.DoesNotContain(
            _factory.LoggedEntries(),
            entry => entry.Contains("Platform supplier preset audit", StringComparison.Ordinal));
    }

    #endregion

    #region The business's own suppliers are untouched

    /// <summary>
    /// A preset is not a supplier: creating, renaming and disabling presets changes nothing a business
    /// sees through <c>/api/suppliers</c>, and no preset appears there. Copying a preset into a
    /// business is issue #835 child C and is explicitly out of scope here.
    /// </summary>
    [Fact]
    public async Task Presets_never_appear_in_a_business_s_own_suppliers()
    {
        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        var before = await ReadJsonAsync(await owner.GetAsync("/api/suppliers"));
        Assert.Empty(before.RootElement.EnumerateArray());

        Assert.Equal(
            HttpStatusCode.Created,
            (await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, new { name = "Aldi" })).StatusCode);

        var id = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Aldi").Id;
        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.AsAdministrator().PutAsJsonAsync(
                PresetUrl(id),
                new { name = "Aldi", isEnabled = false })).StatusCode);

        var after = await ReadJsonAsync(await owner.GetAsync("/api/suppliers"));
        Assert.Empty(after.RootElement.EnumerateArray());
    }

    /// <summary>
    /// And the reverse: a business's own supplier of the same name is not a preset, and is reported
    /// by neither surface as the other's row.
    /// </summary>
    [Fact]
    public async Task A_business_s_own_supplier_is_not_reported_as_a_preset()
    {
        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        var created = await owner.PostAsJsonAsync("/api/suppliers", new { name = "Coles" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var presets = await ReadJsonAsync(await _factory.AsAdministrator().GetAsync(PresetsUrl));
        Assert.Equal(4, presets.RootElement.EnumerateArray().Count());

        var suppliers = await ReadJsonAsync(await owner.GetAsync("/api/suppliers"));
        Assert.Single(suppliers.RootElement.EnumerateArray());
    }

    #endregion

    private static string PresetUrl(int id) =>
        string.Create(CultureInfo.InvariantCulture, $"{PresetsUrl}/{id}");

    /// <summary>
    /// Every endpoint on the surface, called once with the same client, so an authorization claim is
    /// made about the whole controller rather than about whichever action a test happened to pick.
    /// </summary>
    private static async Task<IReadOnlyList<HttpResponseMessage>> AllEndpointsAsync(HttpClient client) =>
    [
        await client.GetAsync(PresetsUrl),
        await client.PostAsync(PresetsUrl, Json(new { name = "Smuggled", url = "https://smuggled.test" })),
        await client.PutAsync(PresetUrl(1), Json(new { name = "Smuggled", isEnabled = false })),
    ];

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>The <c>ProblemDetails</c> message the Domain refusal carried.</summary>
    private static async Task<string?> MessageAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("message", out var message)
            ? message.GetString()
            : body.RootElement.TryGetProperty("detail", out var detail)
                ? detail.GetString()
                : null;
    }

    /// <summary>The stable error code a middleware refusal carries.</summary>
    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task AssertOnlyTheShippedPresetsExistAsync()
    {
        var presets = await _factory.SupplierPresetsAsync();

        Assert.Equal(
            SuperAdminBusinessesApiFactory.ShippedSupplierPresetNames.Order(StringComparer.Ordinal),
            presets.Select(preset => preset.Name).Order(StringComparer.Ordinal));
        Assert.All(presets, preset =>
        {
            Assert.True(preset.IsEnabled);
            Assert.Null(preset.Url);
        });
    }
}
