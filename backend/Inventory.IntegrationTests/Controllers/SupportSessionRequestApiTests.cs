using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Application.SupportSessions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// A request made inside a support session, end to end (issue #551): over real HTTP, through the
/// whole pipeline - authentication, <c>[Authorize]</c>, the capability policies, the support-session
/// accessor's own re-evaluation of the platform-admin policy,
/// <c>BusinessScopeMiddleware</c>, the tenant query filters and
/// <c>BusinessOwnershipEnforcer</c> - against relational SQLite with the real migration history.
///
/// <para>Two businesses exist throughout, each with its own rows, so "only the chosen business is
/// visible" is a claim about a boundary rather than about an API that has one tenant. The
/// administrator is a member of neither, which is the whole point: everything they can read here,
/// they can read <em>only</em> because of the session they presented.</para>
///
/// <para>The Nayax client this host registers is a faithful fake: it resolves credentials through
/// <c>INayaxRequestCredentialProvider</c> first, exactly as the real client does, and then records
/// the call. That is what makes "zero calls" mean something - a fake that answered without asking
/// for credentials would pass this test while a real deployment spent the business's token.</para>
///
/// <para>The per-capability matrix of "a read passes, a write does not" is complete at the decision
/// point in <c>InventoryApi.Tests.Auth.BusinessCapabilityAuthorizationHandlerTests</c>, including
/// the capabilities no endpoint declares yet. What is here is the endpoints that exist.</para>
/// </summary>
public class SupportSessionRequestApiTests
    : IClassFixture<SupportSessionRequestApiTests.ApiFactory>, IAsyncLifetime
{
    private const string DashboardSummary = "/api/dashboard/summary";

    private readonly ApiFactory _factory;

    public SupportSessionRequestApiTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region Only the chosen business is visible

    /// <summary>
    /// The criterion: tenant filters treat the session as the business it names. The supported
    /// business's product is there, the other business's is not, and nothing in the request chose
    /// either - the id came from the stored session row.
    /// </summary>
    [Fact]
    public async Task Only_the_chosen_businesss_data_is_visible()
    {
        var response = await (await _factory.InSupportSessionAsync()).GetAsync("/api/products");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ApiFactory.SupportedProductName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.OtherProductName, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the other direction, which is what makes the claim above an isolation claim: a session
    /// for the second business reads the second business's rows and not the first's.
    /// </summary>
    [Fact]
    public async Task A_session_for_the_other_business_reads_the_other_business()
    {
        var client = await _factory.InSupportSessionAsync(_factory.OtherBusinessId);

        var body = await (await client.GetAsync("/api/products")).Content.ReadAsStringAsync();

        Assert.Contains(ApiFactory.OtherProductName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.SupportedProductName, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The business the session reads is also the business whose own name and time zone the request
    /// derives everything from - not the administrator's anything, because they have none.
    /// </summary>
    [Fact]
    public async Task The_current_business_is_the_one_the_session_named()
    {
        var response = await (await _factory.InSupportSessionAsync()).GetAsync("/api/business/current");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        Assert.Equal(
            SuperAdminBusinessesApiFactory.TradingBusinessName,
            json.RootElement.GetProperty("name").GetString());
    }

    #endregion

    #region How reads pass and writes fail

    /// <summary>
    /// The criterion, for every capability in the set that has a read endpoint today: the read is
    /// answered normally. Each of these reaches real tenant-owned or business data, so "200" means
    /// the capability passed <em>and</em> the business resolved.
    /// </summary>
    [Theory]
    [InlineData("Dashboard.View", "/api/business/current")]
    [InlineData("Inventory.Operate", "/api/products")]
    [InlineData("Inventory.Operate", "/api/categories")]
    [InlineData("Inventory.Operate", "/api/stock-history")]
    [InlineData("Purchasing.Operate", "/api/suppliers")]
    [InlineData("Purchasing.Operate", "/api/purchases")]
    [InlineData("Expenses.Manage", "/api/operating-expenses")]
    [InlineData("SiteCommissions.Manage", "/api/site-commissions/agreements")]
    [InlineData("Integration.Manage", "/api/settings/nayax-processing-fee-rates")]
    [InlineData("Business.Manage", "/api/business/settings")]
    public async Task A_read_in_the_support_set_is_answered_normally(string capability, string route)
    {
        var response = await (await _factory.InSupportSessionAsync()).GetAsync(route);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {route} ({capability}) answered {(int)response.StatusCode} in a support session.");
    }

    /// <summary>
    /// The criterion: any method that is not a read is <c>403</c> with
    /// <c>read_only_support_session</c>, whatever the capability - including the ones the session's
    /// own set grants, which is what makes the method rule independent of the capability rule.
    /// </summary>
    [Theory]
    [InlineData("Inventory.Operate", "POST", "/api/products/1/inventory-count/apply")]
    [InlineData("Purchasing.Operate", "POST", "/api/supplierorders")]
    [InlineData("Expenses.Manage", "POST", "/api/operating-expenses")]
    [InlineData("Expenses.Manage", "PUT", "/api/operating-expenses/999999")]
    [InlineData("Expenses.Manage", "DELETE", "/api/operating-expenses/999999")]
    [InlineData("Imports.Run", "POST", "/api/imports/products")]
    [InlineData("SiteCommissions.Manage", "POST", "/api/site-commissions/agreements")]
    [InlineData("Integration.Manage", "POST", "/api/settings/nayax-processing-fee-rates")]
    [InlineData("Data.Repair", "POST", "/api/admin/historical-gst-classification/preview")]
    [InlineData("Business.Manage", "PUT", "/api/business/settings")]
    [InlineData("Suppliers.ManageGstDefaults", "PUT", "/api/suppliers/1/gst-defaults")]
    [InlineData("Dashboard.View", "POST", "/api/nayax-sales-sync")]
    public async Task A_write_is_refused_as_read_only(string capability, string method, string route)
    {
        var client = await _factory.InSupportSessionAsync();

        var response = await client.SendAsync(Request(method, route));

        Assert.True(
            response.StatusCode == HttpStatusCode.Forbidden,
            $"{method} {route} ({capability}) answered {(int)response.StatusCode} in a support session.");
        Assert.Equal(SupportSessionErrorCodes.ReadOnly, await CodeAsync(response));
    }

    /// <summary>
    /// The criterion: the two excluded capabilities are refused even for a read. The members list
    /// carries people's names and email addresses, and a supporter from outside the business does
    /// not see those in v1.
    /// </summary>
    [Theory]
    [InlineData("/api/members")]
    [InlineData("/api/members/invites")]
    public async Task A_read_outside_the_support_set_is_refused(string route)
    {
        var response = await (await _factory.InSupportSessionAsync()).GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // Refused as a capability the session does not hold, which says nothing at all - not as a
        // read-only refusal, which would wrongly suggest a read would have been fine.
        Assert.DoesNotContain(SupportSessionErrorCodes.ReadOnly, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.OwnerDisplayName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.OwnerEmail, body, StringComparison.Ordinal);
    }

    #endregion

    #region Nayax is blocked in the same change

    /// <summary>
    /// The criterion: every Nayax-backed endpoint fails closed with
    /// <c>unavailable_in_support_view</c>, and the client records no call at all - the credentials
    /// were never resolved, so nothing left the API and the business's token was never decrypted.
    ///
    /// <para>The dashboard summary was among these until issue #552 gave it a degraded shape; it is
    /// now answered <c>200</c> with its two Nayax-backed sections replaced, which is the region
    /// below.</para>
    /// </summary>
    [Theory]
    [InlineData("/api/pick-list?machineIds=1")]
    [InlineData("/api/machines")]
    public async Task A_nayax_backed_read_fails_closed_without_a_single_call(string route)
    {
        await _factory.SeedNayaxConnectionAsync();
        var client = await _factory.InSupportSessionAsync();
        _factory.ResetNayaxCalls();

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SupportSessionErrorCodes.NayaxUnavailable, await CodeAsync(response));
        Assert.Equal(0, _factory.NayaxCalls);
    }

    /// <summary>
    /// The control that makes the claim above about support sessions rather than about a host where
    /// nothing ever calls Nayax: with the same connection stored and the same fake client, the
    /// business's own Owner reaches the same endpoints and the client really is called.
    /// </summary>
    [Theory]
    [InlineData("/api/dashboard/summary")]
    [InlineData("/api/pick-list?machineIds=1")]
    [InlineData("/api/machines")]
    public async Task The_same_endpoint_really_does_call_nayax_for_a_member(string route)
    {
        await _factory.SeedNayaxConnectionAsync();
        _factory.ResetNayaxCalls();

        var response = await _factory
            .Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)
            .GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_factory.NayaxCalls > 0, $"GET {route} made no Nayax call for a member.");
    }

    #endregion

    #region The dashboard's degraded shape (issue #552)

    /// <summary>
    /// The criterion: in a support view the summary is answered <c>200</c> with its local sections
    /// intact and the two sections the live fleet feeds replaced by
    /// <c>{ "available": false, "reason": "SupportView" }</c> - the shape issue #329 uses for
    /// <c>NayaxNotConnected</c> - and the Nayax client records no call at all.
    ///
    /// <para>The property sets are asserted exactly, so a section that was emitted as a partial
    /// count, a <c>null</c> or a zero - any of which would read as a real figure about the
    /// business's machines - fails here.</para>
    /// </summary>
    [Fact]
    public async Task The_dashboard_summary_replaces_its_fleet_sections_without_a_single_call()
    {
        await _factory.SeedNayaxConnectionAsync();
        var client = await _factory.InSupportSessionAsync();
        _factory.ResetNayaxCalls();

        var response = await client.GetAsync(DashboardSummary);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _factory.NayaxCalls);

        using var json = await ReadJsonAsync(response);
        var summary = json.RootElement;
        Assert.Equal(
            ["asOfUtc", "businessDate", "inventory", "needsOrdering", "needsRefill", "salesThisWeek"],
            Properties(summary));
        foreach (var section in new[] { "needsRefill", "needsOrdering" })
        {
            Assert.Equal(["available", "reason"], Properties(summary.GetProperty(section)));
            Assert.False(summary.GetProperty(section).GetProperty("available").GetBoolean());
            Assert.Equal("SupportView", summary.GetProperty(section).GetProperty("reason").GetString());
        }
    }

    /// <summary>
    /// The other half of the criterion: the sections that need nothing from Nayax are the real
    /// local ones, computed over the supported business's own catalogue - the point of the issue is
    /// that the dashboard still shows what it can rather than failing whole.
    /// </summary>
    [Fact]
    public async Task The_dashboard_summary_still_reports_the_local_sections()
    {
        var client = await _factory.InSupportSessionAsync();

        using var json = await ReadJsonAsync(await client.GetAsync(DashboardSummary));
        var summary = json.RootElement;

        var inventory = summary.GetProperty("inventory");
        // The one product this file seeds for the supported business, with its five units: the
        // local sections describe that business and not the other one or an empty catalogue.
        Assert.Equal(1, inventory.GetProperty("productCount").GetInt32());
        Assert.Equal(5, inventory.GetProperty("unitsInStorage").GetInt32());
        // A support session grants Dashboard.ViewFinancials, so the week's figures are published.
        Assert.Equal(
            ["inventoryValueAtCost", "isInventoryValueComplete", "productCount", "productsWithUnknownCost",
                "unitsInStorage"],
            Properties(inventory));
        Assert.Equal(0m, summary.GetProperty("salesThisWeek").GetProperty("sales").GetDecimal());
    }

    /// <summary>
    /// The criterion that keeps the one above a support-session claim rather than a change to the
    /// endpoint: the business's own Owner is answered the ordinary contract over the same host and
    /// the same stored connection - the real refill and ordering cards, no availability flag
    /// anywhere, and the Nayax client really is called.
    /// </summary>
    [Fact]
    public async Task The_same_summary_keeps_its_normal_shape_for_a_member()
    {
        await _factory.SeedNayaxConnectionAsync();
        _factory.ResetNayaxCalls();

        var response = await _factory
            .Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)
            .GetAsync(DashboardSummary);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_factory.NayaxCalls > 0, "The summary made no Nayax call for a member.");

        using var json = await ReadJsonAsync(response);
        var summary = json.RootElement;
        Assert.Equal(
            ["asOfUtc", "businessDate", "inventory", "needsOrdering", "needsRefill", "salesThisWeek"],
            Properties(summary));
        Assert.Equal(
            ["emptySelectionCount", "lowSelectionCount", "machinesEvaluated", "machinesNeedingRefill",
                "machinesWithEmptySelections", "machinesWithLowSelections", "selectionsEvaluated"],
            Properties(summary.GetProperty("needsRefill")));
        Assert.Equal(
            ["productsEvaluated", "productsNeedingOrdering"],
            Properties(summary.GetProperty("needsOrdering")));
        Assert.DoesNotContain("SupportView", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    #endregion

    #region Joining, and not joining, a session

    /// <summary>
    /// The criterion: an expired session id is refused with <c>support_session_ended</c>, and never
    /// falls back to the administrator's own membership - they have none, so a fallback would be a
    /// membership refusal with a different code and a different meaning.
    /// </summary>
    [Fact]
    public async Task An_expired_session_id_is_refused_as_ended()
    {
        const string sessionId = "an-id-whose-session-has-run-out";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            sessionId,
            startedAtUtc: DateTime.UtcNow.AddHours(-3),
            expiresAtUtc: DateTime.UtcNow.AddHours(-2));

        var response = await _factory.WithSupportSession(sessionId).GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SupportSessionErrorCodes.SessionEnded, await CodeAsync(response));
    }

    /// <summary>
    /// An id that was ended, and one that was never issued, answer the same way: one sentence and
    /// one code for every unusable session, because the differences between them are facts about
    /// somebody else's session.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_ended_or_unknown_session_id_is_refused_the_same_way(bool seedAnEndedSession)
    {
        const string sessionId = "an-id-that-will-not-work";
        if (seedAnEndedSession)
        {
            await _factory.SeedSupportSessionAsync(
                _factory.TradingBusinessId,
                SuperAdminBusinessesApiFactory.AdministratorObjectId,
                sessionId,
                endedAtUtc: DateTime.UtcNow.AddMinutes(-1));
        }

        var response = await _factory.WithSupportSession(sessionId).GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SupportSessionErrorCodes.SessionEnded, await CodeAsync(response));
    }

    /// <summary>
    /// A session is usable only by the administrator it was granted to, so a leaked id is useless:
    /// another administrator presenting it is refused exactly as if it named nothing.
    /// </summary>
    [Fact]
    public async Task Another_administrators_session_id_is_useless()
    {
        const string sessionId = "somebody-elses-session-id";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            "cccccccc-cccc-cccc-cccc-cccccccccccc",
            sessionId);

        var response = await _factory.WithSupportSession(sessionId).GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SupportSessionErrorCodes.SessionEnded, await CodeAsync(response));
    }

    /// <summary>
    /// The criterion: deactivating the business mid-session closes the view on the very next
    /// request. The session row's own end is the deactivation hook's job (issue #550); this is the
    /// independent check that the request makes, so a business that was switched off some other way
    /// is still unreadable.
    /// </summary>
    [Fact]
    public async Task Deactivating_the_business_mid_session_refuses_the_next_request()
    {
        const string sessionId = "a-session-for-a-business-about-to-stop";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId, SuperAdminBusinessesApiFactory.AdministratorObjectId, sessionId);
        var client = _factory.WithSupportSession(sessionId);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);

        await _factory.SetBusinessStatusAsync(_factory.TradingBusinessId, BusinessStatus.Deactivated);

        var afterwards = await client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.Forbidden, afterwards.StatusCode);
        Assert.Equal(SupportSessionErrorCodes.SessionEnded, await CodeAsync(afterwards));
    }

    /// <summary>
    /// The criterion: a request without the header uses the administrator's own membership. They
    /// have none, so every business endpoint refuses them - and refuses them as a non-member, with
    /// no support-session code at all.
    /// </summary>
    [Fact]
    public async Task A_request_without_the_header_uses_the_administrators_own_membership()
    {
        // A usable session exists; this request simply does not present it.
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            "a-session-this-request-does-not-present");

        var response = await _factory.AsAdministrator().GetAsync("/api/products");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(SupportSessionErrorCodes.SessionEnded, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.SupportedProductName, body, StringComparison.Ordinal);
    }

    #endregion

    #region Normal members are unaffected

    /// <summary>
    /// The regression this whole slice has to avoid: an ordinary member's request is exactly what it
    /// was. Their own business's rows, their own role's capabilities, and their writes still allowed.
    /// </summary>
    [Fact]
    public async Task A_members_own_request_is_unchanged()
    {
        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        var read = await client.GetAsync("/api/products");
        var write = await client.SendAsync(Request("PUT", "/api/suppliers/999999/gst-defaults"));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Contains(
            ApiFactory.SupportedProductName,
            await read.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        // Not 403: the Owner holds Suppliers.ManageGstDefaults, so the request reaches the endpoint
        // and is answered by its own validation. The read-only rule is not theirs.
        Assert.NotEqual(HttpStatusCode.Forbidden, write.StatusCode);
    }

    /// <summary>
    /// A member who sets the header gains nothing and loses nothing: they are not the platform
    /// administrator, so its value is not read at all and their request is an ordinary one.
    /// </summary>
    [Fact]
    public async Task A_member_who_sets_the_header_is_still_an_ordinary_member()
    {
        const string sessionId = "a-session-id-a-member-got-hold-of";
        await _factory.SeedSupportSessionAsync(
            _factory.OtherBusinessId, SuperAdminBusinessesApiFactory.AdministratorObjectId, sessionId);

        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        client.DefaultRequestHeaders.Add(SupportSessionHeader.Name, sessionId);

        var response = await client.GetAsync("/api/products");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ApiFactory.SupportedProductName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.OtherProductName, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A support session writes nothing about anybody's membership - no row for the administrator,
    /// and no change to the member whose business they are looking at. Issue #524's rule, from the
    /// outside; that nothing is even <em>scheduled</em> is pinned on the middleware itself in
    /// <c>BusinessScopeMiddlewareSupportSessionTests</c>.
    /// </summary>
    [Fact]
    public async Task A_support_session_writes_no_membership()
    {
        var client = await _factory.InSupportSessionAsync();

        _ = await client.GetAsync("/api/products");

        var memberships = await _factory.MembershipsOfAsync(_factory.TradingBusinessId);
        Assert.DoesNotContain(
            memberships,
            membership => string.Equals(
                membership.ObjectId,
                SuperAdminBusinessesApiFactory.AdministratorObjectId,
                StringComparison.OrdinalIgnoreCase));
        var owner = await _factory.MembershipAsync(_factory.TradingOwnerMembershipId);
        Assert.Equal(ApiFactory.OwnerDisplayName, owner.DisplayName);
        Assert.Equal(ApiFactory.OwnerEmail, owner.Email);
    }

    #endregion

    #region What the caller is told, and what is recorded

    /// <summary>
    /// The criterion: <c>/api/me/access</c> in a session reports that session's exact capability set
    /// plus the session block - whose data it is, when it runs out, and that it is read-only - and no
    /// role, because the administrator holds none.
    /// </summary>
    [Fact]
    public async Task Me_access_reports_the_support_set_and_the_session()
    {
        var response = await (await _factory.InSupportSessionAsync()).GetAsync("/api/me/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        var body = json.RootElement;

        Assert.Equal(JsonValueKind.Null, body.GetProperty("role").ValueKind);
        Assert.Equal(
            [
                "Dashboard.View",
                "Dashboard.ViewFinancials",
                "PickList.View",
                "Inventory.Operate",
                "Purchasing.Operate",
                "Expenses.Manage",
                "Reports.View",
                "Imports.Run",
                "SiteCommissions.Manage",
                "Integration.Manage",
                "Data.Repair",
                "Roles.View",
                "Business.Manage",
            ],
            body.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).ToArray());

        var session = body.GetProperty("supportSession");
        Assert.Equal(
            SuperAdminBusinessesApiFactory.TradingBusinessName,
            session.GetProperty("businessName").GetString());
        Assert.True(session.GetProperty("readOnly").GetBoolean());
        Assert.True(session.GetProperty("expiresAtUtc").GetDateTime() > DateTime.UtcNow);
        // The contract is exactly these three properties: no session id, no business id, no actor.
        Assert.Equal(
            ["businessName", "expiresAtUtc", "readOnly"],
            session.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The criterion: one audit event per request in a session, naming the route template, the
    /// method and the status - and no result data. The route <em>template</em> is the point: it says
    /// which endpoint was reached without recording which record.
    /// </summary>
    [Fact]
    public async Task Each_request_in_a_session_is_audited_with_its_route_method_and_status()
    {
        var client = await _factory.InSupportSessionAsync();
        _factory.ClearLog();

        _ = await client.GetAsync("/api/products");

        var audit = SingleAuditEvent();
        // The route template, not the path: no id, name or query value a request carried.
        Assert.Contains("Route=api/products", audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Method=GET", audit, StringComparison.Ordinal);
        Assert.Contains("Status=200", audit, StringComparison.Ordinal);
        Assert.Contains($"BusinessId={_factory.TradingBusinessId}", audit, StringComparison.Ordinal);
        // Nothing that was read, and never the session id in either form.
        Assert.DoesNotContain(ApiFactory.SupportedProductName, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.SessionId, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(SupportSessionId.Hash(ApiFactory.SessionId), audit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The criterion (issue #672): a read the session's capability set does not grant is refused by
    /// the authorization middleware - which runs <em>before</em> <c>BusinessScopeMiddleware</c> - and
    /// is still recorded, with the <c>403</c> the caller actually got. An attempt that was refused is
    /// exactly the attempt a support trail has to show.
    /// </summary>
    [Fact]
    public async Task A_read_refused_as_a_missing_capability_is_audited_with_its_403()
    {
        var client = await _factory.InSupportSessionAsync();
        _factory.ClearLog();

        var response = await client.GetAsync("/api/members");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var audit = SingleAuditEvent();
        Assert.Contains("Route=api/members", audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Method=GET", audit, StringComparison.Ordinal);
        Assert.Contains("Status=403", audit, StringComparison.Ordinal);
        Assert.Contains($"BusinessId={_factory.TradingBusinessId}", audit, StringComparison.Ordinal);
        Assert.Contains($"SupportSessionKey={await SessionKeyAsync()}", audit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same criterion for the other refusal the authorization middleware makes: a disallowed
    /// write. It is recorded as the write it was - the method is in the event - with the <c>403</c>
    /// the read-only rule answered.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/operating-expenses", "api/operating-expenses")]
    [InlineData("PUT", "/api/business/settings", "api/business/settings")]
    public async Task A_write_refused_as_read_only_is_audited_with_its_403(
        string method,
        string route,
        string template)
    {
        var client = await _factory.InSupportSessionAsync();
        _factory.ClearLog();

        var response = await client.SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SupportSessionErrorCodes.ReadOnly, await CodeAsync(response));
        var audit = SingleAuditEvent();
        Assert.Contains($"Route={template}", audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Method={method}", audit, StringComparison.Ordinal);
        Assert.Contains("Status=403", audit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The criterion that the recorded status is the <em>final mapped</em> one (issue #672): a Nayax
    /// read in a session throws, the exception handler maps it to <c>403</c>
    /// <c>unavailable_in_support_view</c>, and that is what the trail says. Recording the status
    /// before the mapping would write <c>Status=200</c> for a request the caller was refused.
    /// </summary>
    [Fact]
    public async Task A_refusal_raised_as_an_exception_is_audited_with_the_mapped_status()
    {
        await _factory.SeedNayaxConnectionAsync();
        var client = await _factory.InSupportSessionAsync();
        _factory.ClearLog();

        var response = await client.GetAsync("/api/machines");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SupportSessionErrorCodes.NayaxUnavailable, await CodeAsync(response));
        var audit = SingleAuditEvent();
        Assert.Contains("Status=403", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("Status=200", audit, StringComparison.Ordinal);
        Assert.Contains("Route=api/machines", audit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And a plain unsuccessful read, for the same reason: the status recorded is the one the
    /// endpoint answered, never the <c>200</c> the response started out holding.
    /// </summary>
    [Fact]
    public async Task A_read_that_found_nothing_is_audited_with_its_own_status()
    {
        var client = await _factory.InSupportSessionAsync();
        _factory.ClearLog();

        var response = await client.GetAsync("/api/products/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var audit = SingleAuditEvent();
        Assert.Contains("Status=404", audit, StringComparison.Ordinal);
        // The template carries the parameter's name, never the id the request asked for.
        Assert.Contains("Route=api/products/{id", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("999999", audit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The attribution criterion (issue #672): an unusable session is refused and recorded as no
    /// session at all. There is nothing verified to attribute the request to - the presented header
    /// matched no active session of this caller's - and attributing it to the business or the row the
    /// caller <em>named</em> would be trusting the header itself.
    /// </summary>
    [Fact]
    public async Task An_unusable_session_is_audited_as_no_session_at_all()
    {
        const string sessionId = "an-id-whose-session-has-run-out";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            sessionId,
            startedAtUtc: DateTime.UtcNow.AddHours(-3),
            expiresAtUtc: DateTime.UtcNow.AddHours(-2));
        var client = _factory.WithSupportSession(sessionId);
        _factory.ClearLog();

        var response = await client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SupportSessionErrorCodes.SessionEnded, await CodeAsync(response));
        Assert.Empty(AuditEvents());
    }

    /// <summary>
    /// The other half of the same criterion: the header is not attribution. A member who sets it is
    /// an ordinary member, and their request is recorded as nobody's support session - the trail
    /// would otherwise say a business was read from outside when it was read by its own Owner.
    /// </summary>
    [Fact]
    public async Task A_member_who_sets_the_header_is_audited_as_no_session_at_all()
    {
        const string sessionId = "a-session-id-a-member-got-hold-of";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId, SuperAdminBusinessesApiFactory.AdministratorObjectId, sessionId);
        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        client.DefaultRequestHeaders.Add(SupportSessionHeader.Name, sessionId);
        _factory.ClearLog();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);

        Assert.Empty(AuditEvents());
    }

    #endregion

    /// <summary>
    /// Every per-request support audit event the host has logged since the last <c>ClearLog</c>.
    /// Asserting over the whole list rather than "at least one" is what makes "exactly once per
    /// request" assertable - a second emitter would double every entry.
    /// </summary>
    private IReadOnlyList<string> AuditEvents() => _factory.LoggedEntries()
        .Where(entry => entry.Contains("Support session request.", StringComparison.Ordinal))
        .ToList();

    private string SingleAuditEvent() => Assert.Single(AuditEvents());

    /// <summary>The one seeded session's own row key - what the audit event ties a request to.</summary>
    private async Task<int> SessionKeyAsync() => (await _factory.SupportSessionsAsync()).Single().Id;

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>
    /// One JSON object's property names, ordered, so a response shape is asserted as the exact set
    /// it publishes rather than as the properties a test happened to look for.
    /// </summary>
    private static string[] Properties(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = await ReadJsonAsync(response);
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// A request whose body, where it needs one, is deliberately invalid: every row that uses this
    /// is about the access decision, which is taken before an action binds a body, so nothing here
    /// may be a request the endpoint would act on.
    /// </summary>
    private static HttpRequestMessage Request(string method, string route)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);

        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    /// <summary>
    /// The Super Admin host of issue #550, with two things added: tenant-owned rows in both
    /// businesses so isolation is assertable, and a Nayax client that resolves credentials before
    /// recording a call, so "no Nayax call was made" is a claim about the credential block rather
    /// than about a fake that never needed credentials.
    /// </summary>
    public sealed class ApiFactory : SuperAdminBusinessesApiFactory
    {
        public const string SupportedProductName = "Supported Business Cola";
        public const string OtherProductName = "Other Business Lemonade";
        public const string OwnerDisplayName = "The Business Owner";
        public const string OwnerEmail = "owner@vending.example.com";
        public const string SessionId = "the-support-session-id-these-tests-present";

        private int _nayaxCalls;

        public int OtherBusinessId { get; private set; }

        public int NayaxCalls => Volatile.Read(ref _nayaxCalls);

        public void ResetNayaxCalls() => Volatile.Write(ref _nayaxCalls, 0);

        /// <summary>A client carrying <paramref name="sessionId"/> as the support-session header.</summary>
        public HttpClient WithSupportSession(string sessionId)
        {
            var client = AsAdministrator();
            client.DefaultRequestHeaders.Add(SupportSessionHeader.Name, sessionId);
            return client;
        }

        /// <summary>
        /// A client inside a freshly started, usable session for <paramref name="businessId"/> (the
        /// trading business by default).
        /// </summary>
        public async Task<HttpClient> InSupportSessionAsync(int? businessId = null)
        {
            await SeedSupportSessionAsync(
                businessId ?? TradingBusinessId, AdministratorObjectId, SessionId);

            return WithSupportSession(SessionId);
        }

        /// <summary>
        /// A stored Nayax connection for the trading business in the one state that lets credentials
        /// resolve, so both halves of the Nayax criterion are about the same world: the support
        /// session is refused <em>although</em> the connection is perfectly usable, and the
        /// business's own Owner on the same endpoints really does reach the client.
        /// </summary>
        public async Task SeedNayaxConnectionAsync()
        {
            await using var db = Database();
            if (await db.BusinessNayaxConnections
                .AnyAsync(connection => connection.BusinessId == TradingBusinessId))
            {
                return;
            }

            db.BusinessNayaxConnections.Add(new BusinessNayaxConnection
            {
                BusinessId = TradingBusinessId,
                OperatorId = "2002736764",
                // Obviously fake, and never sent anywhere: this host's client talks to nothing.
                AccessTokenCiphertext = "fake-token-not-a-real-credential",
                EncryptionKeyId = PassThroughNayaxTokenProtector.KeyId,
                Status = NayaxConnectionStatus.Ready,
                CredentialRevision = 1,
                UpdatedAtUtc = CreatedAt,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Puts the world back, and adds this file's own fixtures: one product per business, and the
        /// Owner's stored display name and email so "a support session changed nothing about the
        /// member" is assertable.
        /// </summary>
        public new async Task ResetAsync()
        {
            await base.ResetAsync();

            OtherBusinessId = await SeedBusinessAsync("Second Vending Co", BusinessStatus.Active);

            await using var db = Database();
            db.Products.AddRange(
                NewProduct(TradingBusinessId, SupportedProductName),
                NewProduct(OtherBusinessId, OtherProductName));

            var owner = await db.BusinessMemberships
                .Where(membership => membership.Id == TradingOwnerMembershipId)
                .SingleAsync();
            owner.DisplayName = OwnerDisplayName;
            owner.Email = OwnerEmail;

            await db.SaveChangesAsync();
            ResetNayaxCalls();
            ClearLog();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<INayaxLynxClient>(sp => new RecordingNayaxLynxClient(
                    sp.GetRequiredService<INayaxRequestCredentialProvider>(),
                    () => Interlocked.Increment(ref _nayaxCalls)));

                // No encryption key is configured in a test host, so the shipped protector refuses
                // every token. Substituting a pass-through one is what lets the control test resolve
                // credentials at all - and it is substituted for both halves of the comparison, so
                // the difference between them is the support session and nothing else.
                services.AddSingleton<INayaxTokenProtector, PassThroughNayaxTokenProtector>();
            });
        }

        private static Product NewProduct(int businessId, string name) => new()
        {
            BusinessId = businessId,
            Name = name,
            Sku = $"{name}-SKU",
            UnitPrice = 2.50m,
            QuantityInStock = 5,
        };
    }

    /// <summary>
    /// Stores and returns the token unchanged, so a test host with no encryption key configured can
    /// still resolve a stored credential. It is a test double and nothing else: no real deployment
    /// registers it, and the value it carries here is an obvious fake.
    /// </summary>
    private sealed class PassThroughNayaxTokenProtector : INayaxTokenProtector
    {
        public const string KeyId = "test-pass-through";

        public ProtectedNayaxToken Protect(string accessToken) => new(KeyId, accessToken);

        public string Unprotect(ProtectedNayaxToken protectedToken) => protectedToken.Ciphertext;
    }

    /// <summary>
    /// A Nayax client that behaves like the real one in the single respect these tests depend on: it
    /// resolves the current business's credentials through
    /// <see cref="INayaxRequestCredentialProvider"/> <em>before</em> it answers, and only then
    /// records the call and returns nothing.
    ///
    /// <para>That ordering is deliberate and is what gives "zero calls" its meaning. A fake that
    /// answered without asking for credentials would never meet the support-session refusal, so this
    /// file would pass while a real deployment spent the business's own Nayax token on a supporter's
    /// request.</para>
    /// </summary>
    private sealed class RecordingNayaxLynxClient(
        INayaxRequestCredentialProvider credentials,
        Action record) : INayaxLynxClient
    {
        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => CallAsync<NayaxDevice>(ct);

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) => CallAsync<NayaxMachine>(ct);

        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default) =>
            CallAsync<NayaxMachineProduct>(ct);

        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
            long machineId,
            List<NayaxMachineProduct> products,
            CancellationToken ct = default) =>
            CallAsync<NayaxMachineProduct>(ct);

        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => CallAsync<NayaxProduct>(ct);

        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
            CallAsync<NayaxProductGroup>(ct);

        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) =>
            CallAsync<NayaxLastSalesReport>(ct);

        public async Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default)
        {
            _ = await CallAsync<NayaxMachine>(ct);
            return new NayaxMachine();
        }

        public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default) =>
            CallAsync<NayaxMachineAlert>(ct);

        private async Task<List<T>> CallAsync<T>(CancellationToken ct)
        {
            _ = await credentials.GetForOperationAsync(ct);
            record();
            return [];
        }
    }
}
