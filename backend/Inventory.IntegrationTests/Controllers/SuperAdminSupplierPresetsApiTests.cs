using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inventory.Application.Suppliers;
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

        var create = await client.PostAsJsonAsync(PresetsUrl, CreateBody("Aldi"));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("read_only_role_preview", await CodeAsync(create));

        var presetId = (await _factory.SupplierPresetsAsync())[0].Id;
        var update = await client.PutAsJsonAsync(
            PresetUrl(presetId),
            EditBody("Renamed", isEnabled: false));
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

        var create = await client.PostAsJsonAsync(PresetsUrl, CreateBody("Aldi"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await ReadJsonAsync(create)).RootElement.GetProperty("id").GetInt32();

        var edit = await client.PutAsJsonAsync(
            PresetUrl(created),
            EditBody("Aldi", isEnabled: false));
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
    /// The baseline four, by name, with the eleven declared fields and nothing else. In particular
    /// the stored normalised name is not published: it is the unique index's key, not a field of the
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
                [
                    "createdAtUtc",
                    "id",
                    "isEnabled",
                    "name",
                    "priceConnector",
                    "priceConnectorKey",
                    "pricesIncludeGst",
                    "servedStates",
                    "servesNationwide",
                    "updatedAtUtc",
                    "url",
                ],
                preset.EnumerateObject()
                    .Select(property => property.Name)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
        });
    }

    /// <summary>
    /// The supplier-master fields as the list reports them (issue #931): the market as the state
    /// codes themselves, the GST basis as a flag, and the connector as its enum's number with its
    /// key beside it. A nationwide preset reports an <em>empty</em> state list rather than all eight
    /// codes, because those are different claims.
    /// </summary>
    [Fact]
    public async Task The_list_reports_the_supplier_master_fields()
    {
        await _factory.SeedSupplierPresetAsync(
            "JB Metro Example",
            url: "https://www.jbmetro.example",
            servesNationwide: false,
            servedStates: "SA,NT",
            pricesIncludeGst: false,
            priceConnector: SupplierPriceConnector.JbMetro);
        await _factory.SeedSupplierPresetAsync(
            "Varnmill Example",
            priceConnector: SupplierPriceConnector.Varnmill,
            priceConnectorKey: "aldi");

        var body = await ReadJsonAsync(await _factory.AsAdministrator().GetAsync(PresetsUrl));
        var presets = body.RootElement.EnumerateArray().ToList();

        var jbMetro = presets.Single(preset => preset.GetProperty("name").GetString() == "JB Metro Example");
        Assert.False(jbMetro.GetProperty("servesNationwide").GetBoolean());
        Assert.Equal(
            ["SA", "NT"],
            jbMetro.GetProperty("servedStates").EnumerateArray().Select(code => code.GetString()).ToArray());
        Assert.False(jbMetro.GetProperty("pricesIncludeGst").GetBoolean());
        Assert.Equal(
            (int)SupplierPriceConnector.JbMetro,
            jbMetro.GetProperty("priceConnector").GetInt32());
        Assert.Equal(JsonValueKind.Null, jbMetro.GetProperty("priceConnectorKey").ValueKind);

        var varnmill = presets.Single(preset => preset.GetProperty("name").GetString() == "Varnmill Example");
        Assert.True(varnmill.GetProperty("servesNationwide").GetBoolean());
        Assert.Empty(varnmill.GetProperty("servedStates").EnumerateArray());
        Assert.True(varnmill.GetProperty("pricesIncludeGst").GetBoolean());
        Assert.Equal(
            (int)SupplierPriceConnector.Varnmill,
            varnmill.GetProperty("priceConnector").GetInt32());
        Assert.Equal("aldi", varnmill.GetProperty("priceConnectorKey").GetString());

        // A preset nobody has given a connector reports none, rather than a number standing for one.
        var coles = presets.Single(preset => preset.GetProperty("name").GetString() == "Coles");
        Assert.Equal(JsonValueKind.Null, coles.GetProperty("priceConnector").ValueKind);
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
            CreateBody("  Aldi  ", url: " https://www.aldi.com.au "));

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
            CreateBody("Aldi", url: "   "));

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
        var response = await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, CreateBody(name));

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
        var response = await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, CreateBody(name));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SupplierPresetPolicy.NameRequiredMessage, await MessageAsync(response));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    [Fact]
    public async Task A_name_longer_than_the_limit_is_refused()
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            CreateBody(new string('n', SupplierPresetPolicy.MaxNameLength + 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SupplierPresetPolicy.NameTooLongMessage, await MessageAsync(response));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    /// <summary>
    /// The refusal is issue #847's own sentence, asserted against
    /// <see cref="SupplierUrlPolicy.InvalidMessage"/> itself: the preset surface runs the one
    /// supplier-website rule there is rather than a second copy of it, so this test fails the day
    /// a preset starts accepting or refusing an address a business's own supplier would not.
    /// </summary>
    [Theory]
    [InlineData("www.aldi.com.au")]
    [InlineData("aldi.com.au")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://files.aldi.com.au")]
    public async Task A_website_that_is_not_an_absolute_http_address_is_refused(string url)
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            CreateBody("Aldi", url));

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
            CreateBody("Aldi", url: tooLong));

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
        var first = await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, CreateBody("Aldi"));
        var second = await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, CreateBody("aldi"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(SupplierPresetPolicy.DuplicateNameMessage, await MessageAsync(second));

        Assert.Single(
            await _factory.SupplierPresetsAsync(),
            preset => preset.NormalisedName == "ALDI");
    }

    #endregion

    #region The supplier-master fields (issue #931)

    /// <summary>
    /// A create accepts and returns the supplier-master fields, and stores exactly what the Domain
    /// made of them: the states in canonical order, the connector key trimmed.
    /// </summary>
    [Fact]
    public async Task A_preset_is_created_with_its_served_states_gst_basis_and_connector()
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            CreateBody(
                "JB Metro Example",
                url: "https://www.jbmetro.example",
                servesNationwide: false,
                servedStates: ["nt", " sa "],
                pricesIncludeGst: false,
                priceConnector: (int)SupplierPriceConnector.Varnmill,
                priceConnectorKey: "  jbmetro  "));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = (await ReadJsonAsync(response)).RootElement;
        Assert.False(created.GetProperty("servesNationwide").GetBoolean());
        Assert.Equal(
            ["SA", "NT"],
            created.GetProperty("servedStates").EnumerateArray().Select(code => code.GetString()).ToArray());
        Assert.False(created.GetProperty("pricesIncludeGst").GetBoolean());
        Assert.Equal((int)SupplierPriceConnector.Varnmill, created.GetProperty("priceConnector").GetInt32());
        Assert.Equal("jbmetro", created.GetProperty("priceConnectorKey").GetString());

        var stored = (await _factory.SupplierPresetsAsync())
            .Single(preset => preset.Name == "JB Metro Example");
        Assert.False(stored.ServesNationwide);
        Assert.Equal("SA,NT", stored.ServedStates);
        Assert.False(stored.PricesIncludeGst);
        Assert.Equal(SupplierPriceConnector.Varnmill, stored.PriceConnector);
        Assert.Equal("jbmetro", stored.PriceConnectorKey);
    }

    /// <summary>A nationwide preset stores no state list, which is a different claim from all eight.</summary>
    [Fact]
    public async Task A_nationwide_preset_stores_no_state_list()
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            CreateBody("Kmart Example", servesNationwide: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(
            (await ReadJsonAsync(response)).RootElement.GetProperty("servedStates").EnumerateArray());
        Assert.Equal(
            string.Empty,
            (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Kmart Example").ServedStates);
    }

    /// <summary>
    /// An edit rewrites the whole market, the GST basis and the route together, and the answer is the
    /// stored state rather than the submission.
    /// </summary>
    [Fact]
    public async Task An_edit_stores_the_served_states_the_gst_basis_and_the_connector()
    {
        var id = await _factory.SeedSupplierPresetAsync(
            "Regional Supplier",
            servesNationwide: false,
            servedStates: "VIC",
            pricesIncludeGst: false,
            priceConnector: SupplierPriceConnector.Varnmill,
            priceConnectorKey: "regional");

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            EditBody(
                "Regional Supplier",
                isEnabled: true,
                servesNationwide: true,
                pricesIncludeGst: true,
                priceConnector: (int)SupplierPriceConnector.Kmart));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = (await ReadJsonAsync(response)).RootElement;
        Assert.True(updated.GetProperty("servesNationwide").GetBoolean());
        Assert.Empty(updated.GetProperty("servedStates").EnumerateArray());
        Assert.True(updated.GetProperty("pricesIncludeGst").GetBoolean());
        Assert.Equal((int)SupplierPriceConnector.Kmart, updated.GetProperty("priceConnector").GetInt32());

        var stored = (await _factory.SupplierPresetsAsync()).Single(preset => preset.Id == id);
        Assert.True(stored.ServesNationwide);
        Assert.Equal(string.Empty, stored.ServedStates);
        Assert.True(stored.PricesIncludeGst);
        Assert.Equal(SupplierPriceConnector.Kmart, stored.PriceConnector);
        // Moving to a route that takes no key clears the one the old route needed, rather than
        // leaving behind a value nothing reads.
        Assert.Null(stored.PriceConnectorKey);
    }

    /// <summary>
    /// An edit may also take a preset to no connector at all, which is the state a supplier somebody
    /// price-checks by hand is in.
    /// </summary>
    [Fact]
    public async Task An_edit_may_remove_the_connector_altogether()
    {
        var id = await _factory.SeedSupplierPresetAsync(
            "Hand Checked",
            priceConnector: SupplierPriceConnector.Varnmill,
            priceConnectorKey: "hand");

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            EditBody("Hand Checked", isEnabled: true, priceConnector: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            JsonValueKind.Null,
            (await ReadJsonAsync(response)).RootElement.GetProperty("priceConnector").ValueKind);

        var stored = (await _factory.SupplierPresetsAsync()).Single(preset => preset.Id == id);
        Assert.Null(stored.PriceConnector);
        Assert.Null(stored.PriceConnectorKey);
    }

    /// <summary>
    /// Every invalid supplier-master combination is a <c>400</c> carrying the Domain's own sentence,
    /// and each one writes nothing: the create is refused before the preset exists, so the list is the
    /// baseline four afterwards.
    /// </summary>
    [Fact]
    public async Task An_invalid_served_state_or_connector_combination_is_refused_with_a_message()
    {
        var client = _factory.AsAdministrator();

        var both = await client.PostAsJsonAsync(
            PresetsUrl,
            CreateBody("Aldi", servesNationwide: true, servedStates: ["NSW"]));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(SupplierPresetMasterPolicy.NationwideAndStatesMessage, await MessageAsync(both));

        var neither = await client.PostAsJsonAsync(
            PresetsUrl,
            CreateBody("Aldi", servesNationwide: false, servedStates: []));
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);
        Assert.Equal(SupplierPresetMasterPolicy.ServedStatesRequiredMessage, await MessageAsync(neither));

        var unknownState = await client.PostAsJsonAsync(
            PresetsUrl,
            CreateBody("Aldi", servesNationwide: false, servedStates: ["NSW", "JBT"]));
        Assert.Equal(HttpStatusCode.BadRequest, unknownState.StatusCode);
        Assert.Equal(SupplierPresetMasterPolicy.UnknownServedStateMessage, await MessageAsync(unknownState));

        var missingKey = await client.PostAsJsonAsync(
            PresetsUrl,
            CreateBody("Aldi", priceConnector: (int)SupplierPriceConnector.Varnmill));
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        Assert.Equal(SupplierPresetMasterPolicy.ConnectorKeyRequiredMessage, await MessageAsync(missingKey));

        var keyWithNoRoute = await client.PostAsJsonAsync(
            PresetsUrl,
            CreateBody("Aldi", priceConnector: null, priceConnectorKey: "coles"));
        Assert.Equal(HttpStatusCode.BadRequest, keyWithNoRoute.StatusCode);
        Assert.Equal(
            SupplierPresetMasterPolicy.ConnectorKeyNotAllowedMessage,
            await MessageAsync(keyWithNoRoute));

        var keyOnAKeylessRoute = await client.PostAsJsonAsync(
            PresetsUrl,
            CreateBody(
                "Aldi",
                priceConnector: (int)SupplierPriceConnector.JbMetro,
                priceConnectorKey: "jb"));
        Assert.Equal(HttpStatusCode.BadRequest, keyOnAKeylessRoute.StatusCode);
        Assert.Equal(
            SupplierPresetMasterPolicy.ConnectorKeyNotAllowedMessage,
            await MessageAsync(keyOnAKeylessRoute));

        var tooLongKey = await client.PostAsJsonAsync(
            PresetsUrl,
            CreateBody(
                "Aldi",
                priceConnector: (int)SupplierPriceConnector.Varnmill,
                priceConnectorKey: new string('k', SupplierPresetMasterPolicy.MaxConnectorKeyLength + 1)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLongKey.StatusCode);
        Assert.Equal(SupplierPresetMasterPolicy.ConnectorKeyTooLongMessage, await MessageAsync(tooLongKey));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    /// <summary>
    /// A connector number outside the declared vocabulary is refused too. An enum is a compile-time
    /// constraint and a JSON number is not: <c>0</c> and <c>99</c> bind to a
    /// <c>SupplierPriceConnector</c> that names no collection route, which is not the same thing as
    /// "no route".
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task An_undeclared_connector_number_is_refused(int connector)
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            CreateBody("Aldi", priceConnector: connector));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SupplierPresetMasterPolicy.UnknownConnectorMessage, await MessageAsync(response));

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    /// <summary>
    /// An invalid combination on an <em>edit</em> leaves the preset exactly as it was - every field of
    /// it, not only the one that was refused.
    /// </summary>
    [Fact]
    public async Task An_edit_with_an_invalid_combination_changes_nothing()
    {
        var id = await _factory.SeedSupplierPresetAsync(
            "Regional Supplier",
            servesNationwide: false,
            servedStates: "VIC",
            pricesIncludeGst: false,
            priceConnector: SupplierPriceConnector.Varnmill,
            priceConnectorKey: "regional");

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            EditBody(
                "Regional Supplier",
                isEnabled: true,
                servesNationwide: true,
                servedStates: ["NSW"],
                pricesIncludeGst: true,
                priceConnector: (int)SupplierPriceConnector.Kmart));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SupplierPresetMasterPolicy.NationwideAndStatesMessage, await MessageAsync(response));

        var stored = (await _factory.SupplierPresetsAsync()).Single(preset => preset.Id == id);
        Assert.False(stored.ServesNationwide);
        Assert.Equal("VIC", stored.ServedStates);
        Assert.False(stored.PricesIncludeGst);
        Assert.Equal(SupplierPriceConnector.Varnmill, stored.PriceConnector);
        Assert.Equal("regional", stored.PriceConnectorKey);
    }

    /// <summary>
    /// <c>pricesIncludeGst</c> is a <c>bool</c>, so a body that omitted it would bind <c>false</c>
    /// indistinguishably from an explicit <c>false</c> and record the supplier's prices as ex-GST - a
    /// wrong tax basis, stored silently, on a field AGENTS.md treats as financial data.
    /// <c>JsonRequiredAttribute</c> refuses the under-posted body instead, on both writes.
    /// </summary>
    [Fact]
    public async Task A_write_without_pricesIncludeGst_is_refused_and_changes_nothing()
    {
        var preset = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Coles");

        var created = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            new { name = "Aldi", servesNationwide = true });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);

        var updated = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(preset.Id),
            new { name = "Coles", isEnabled = true, servesNationwide = true });
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);

        var stored = (await _factory.SupplierPresetsAsync()).Single(p => p.Id == preset.Id);
        Assert.Equal(preset.PricesIncludeGst, stored.PricesIncludeGst);
        Assert.Equal(preset.UpdatedAtUtc, stored.UpdatedAtUtc);

        await AssertOnlyTheShippedPresetsExistAsync();
    }

    #endregion

    #region Editing

    [Fact]
    public async Task An_edit_stores_the_name_the_website_and_the_enabled_state()
    {
        var id = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Coles").Id;

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            EditBody(" Coles Supermarkets ", isEnabled: false, url: "https://www.coles.com.au"));

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
            EditBody("Retired Wholesaler", isEnabled: true));

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
            EditBody("coles", isEnabled: true, url: "https://www.coles.com.au"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("coles", (await _factory.SupplierPresetsAsync()).Single(p => p.Id == id).Name);
    }

    [Fact]
    public async Task An_edit_to_another_preset_s_name_is_refused_with_a_conflict()
    {
        var id = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Coles").Id;

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            EditBody("woolworths", isEnabled: true));

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
            EditBody("   ", isEnabled: false));

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
            EditBody("Aldi", isEnabled: true));

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
    /// Each committed write emits one event with the actor, the preset's key, the change, the
    /// resulting state and the supplier-master fields it ended in - and never the preset's name or
    /// its website, which is the restraint the two platform-business audits already show about a
    /// business's name and contact address.
    ///
    /// <para>The name here is deliberately one whose website shares a word with it, so "the name is
    /// absent" and "the website is absent" are both real assertions rather than one accidentally
    /// proving the other.</para>
    /// </summary>
    [Fact]
    public async Task A_committed_write_is_audited_without_the_name_or_the_website()
    {
        _factory.ClearLog();

        var created = await _factory.AsAdministrator().PostAsJsonAsync(
            PresetsUrl,
            CreateBody("Aldi Stores", url: "https://www.aldi.com.au"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Aldi Stores").Id;
        var updated = await _factory.AsAdministrator().PutAsJsonAsync(
            PresetUrl(id),
            EditBody(
                "Aldi Stores",
                isEnabled: false,
                url: "https://www.aldi.com.au",
                servesNationwide: false,
                servedStates: ["ACT", "NSW"],
                pricesIncludeGst: false,
                priceConnector: (int)SupplierPriceConnector.Varnmill,
                priceConnectorKey: "aldi-key"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var audit = _factory.LoggedEntries()
            .Where(entry => entry.Contains("Platform supplier preset audit", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, audit.Count);
        Assert.All(audit, entry =>
        {
            Assert.Contains(SuperAdminBusinessesApiFactory.AdministratorObjectId, entry, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(id.ToString(CultureInfo.InvariantCulture), entry, StringComparison.Ordinal);
            Assert.DoesNotContain("Aldi Stores", entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aldi.com.au", entry, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Contains("change Created", audit[0], StringComparison.Ordinal);
        Assert.Contains("enabled True", audit[0], StringComparison.Ordinal);
        Assert.Contains("change Updated", audit[1], StringComparison.Ordinal);
        Assert.Contains("enabled False", audit[1], StringComparison.Ordinal);

        // The create's own supplier-master state: nationwide, GST-inclusive, collected by nobody.
        Assert.Contains("nationwide True", audit[0], StringComparison.Ordinal);
        Assert.Contains("prices include GST True", audit[0], StringComparison.Ordinal);

        // And the edit's, including the connector key that says which retailer the shared Varnmill
        // route is for. It is recorded in the state the Domain stored it in - the states in canonical
        // order rather than the order the request listed them.
        Assert.Contains("nationwide False", audit[1], StringComparison.Ordinal);
        Assert.Contains("states NSW,ACT", audit[1], StringComparison.Ordinal);
        Assert.Contains("prices include GST False", audit[1], StringComparison.Ordinal);
        Assert.Contains("connector Varnmill", audit[1], StringComparison.Ordinal);
        Assert.Contains("connector key aldi-key", audit[1], StringComparison.Ordinal);
    }

    /// <summary>A refused write is not audited, because nothing happened.</summary>
    [Fact]
    public async Task A_refused_write_is_not_audited()
    {
        _factory.ClearLog();

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, CreateBody("coles"))).StatusCode);

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
            (await _factory.AsAdministrator().PostAsJsonAsync(PresetsUrl, CreateBody("Aldi"))).StatusCode);

        var id = (await _factory.SupplierPresetsAsync()).Single(p => p.Name == "Aldi").Id;
        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.AsAdministrator().PutAsJsonAsync(
                PresetUrl(id),
                EditBody("Aldi", isEnabled: false))).StatusCode);

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
    /// A create body that is valid in every respect a test is not about: nationwide, GST-inclusive
    /// and with no price connector. Each test overrides the one field it is testing, so a refusal it
    /// asserts is about that field and never about a value it forgot to send.
    /// </summary>
    private static object CreateBody(
        string? name,
        string? url = null,
        bool servesNationwide = true,
        string?[]? servedStates = null,
        bool pricesIncludeGst = true,
        int? priceConnector = null,
        string? priceConnectorKey = null) =>
        new
        {
            name,
            url,
            servesNationwide,
            servedStates,
            pricesIncludeGst,
            priceConnector,
            priceConnectorKey,
        };

    /// <summary>
    /// An edit body on the same terms, plus the enabled flag the <c>PUT</c> always carries.
    /// </summary>
    private static object EditBody(
        string? name,
        bool isEnabled,
        string? url = null,
        bool servesNationwide = true,
        string?[]? servedStates = null,
        bool pricesIncludeGst = true,
        int? priceConnector = null,
        string? priceConnectorKey = null) =>
        new
        {
            name,
            url,
            isEnabled,
            servesNationwide,
            servedStates,
            pricesIncludeGst,
            priceConnector,
            priceConnectorKey,
        };

    /// <summary>
    /// Every endpoint on the surface, called once with the same client, so an authorization claim is
    /// made about the whole controller rather than about whichever action a test happened to pick.
    /// </summary>
    private static async Task<IReadOnlyList<HttpResponseMessage>> AllEndpointsAsync(HttpClient client) =>
    [
        await client.GetAsync(PresetsUrl),
        await client.PostAsync(PresetsUrl, Json(CreateBody("Smuggled", url: "https://smuggled.test"))),
        await client.PutAsync(PresetUrl(1), Json(EditBody("Smuggled", isEnabled: false))),
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
            // The baseline's supplier-master values too, so "nothing was written" covers the fields
            // of issue #931 and not only the three #848 had.
            Assert.True(preset.ServesNationwide);
            Assert.Equal(string.Empty, preset.ServedStates);
            Assert.True(preset.PricesIncludeGst);
            Assert.Null(preset.PriceConnector);
            Assert.Null(preset.PriceConnectorKey);
        });
    }
}
