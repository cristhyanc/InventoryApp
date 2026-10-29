using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Application.PlatformPlans;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using InventoryApi.Adapters.Subscriptions;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Super Admin's manual plan grant over real HTTP (issue #982): who may reach it, what each
/// decision writes, what it refuses, and what the business's own members then read.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>, the
/// platform-admin policy, <c>[RequiredScope]</c>, <c>RolePreviewMiddleware</c>,
/// <c>BusinessScopeMiddleware</c>'s independent re-check of the same policy, the tenant query filters,
/// <c>BusinessOwnershipEnforcer</c> and relational SQLite with the real migration history - so the
/// authorization and the tenancy boundary under test are the real ones. Only the authentication is
/// synthetic.</para>
///
/// <para><strong>Two businesses throughout</strong>, and plan enforcement switched on, because a
/// plan is reported to a member only in that state (issue #966).</para>
/// </summary>
public class SuperAdminPlanGrantsApiTests : IClassFixture<SuperAdminPlanGrantsApiTests.ApiFactory>, IAsyncLifetime
{
    private const string AccessUri = "/api/me/access";

    private readonly ApiFactory _factory;

    public SuperAdminPlanGrantsApiTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region Who may reach it

    /// <summary>
    /// The acceptance criterion "a business Owner, or any business role, is refused". An Owner holds
    /// the strongest business role there is and reaches none of these three endpoints: a business
    /// role is not platform authority, and this controller declares no capability any role could
    /// satisfy.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(ApiFactory.TradingOperatorObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task Every_endpoint_refuses_a_caller_who_is_not_the_configured_administrator(string objectId)
    {
        var client = _factory.Authenticated(objectId);

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertNothingWasGrantedAsync();
    }

    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized_and_grants_nothing()
    {
        foreach (var response in await AllEndpointsAsync(_factory.CreateClient()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await AssertNothingWasGrantedAsync();
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

        await AssertNothingWasGrantedAsync();
    }

    /// <summary>
    /// The criterion "a support session cannot change a grant". A member who presents a session id
    /// is still an ordinary member and is refused by the platform-admin policy, and the
    /// administrator inside their own session reaches the control surface as themselves - a session
    /// grants read-only access to somebody else's business and never platform authority over it, so
    /// the grant it could change is nobody's.
    /// </summary>
    [Fact]
    public async Task A_member_presenting_a_support_session_id_is_still_refused()
    {
        var client = await _factory.MemberPresentingASupportSession();

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertNothingWasGrantedAsync();
    }

    /// <summary>
    /// The criterion "a role preview cannot change a grant": the administrator previewing a role is
    /// refused both writes with <c>read_only_role_preview</c>, by the same middleware that refuses
    /// every other write in the application.
    /// </summary>
    [Fact]
    public async Task The_administrator_in_a_role_preview_cannot_change_a_grant()
    {
        var client = _factory.Previewing("Owner");

        var granted = await PutAsync(client, _factory.OtherBusinessId, "Pro", reason: "Pilot", revision: 0);
        var ended = await EndAsync(client, _factory.OtherBusinessId, reason: "Over", revision: 1);

        Assert.Equal(HttpStatusCode.Forbidden, granted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ended.StatusCode);
        Assert.Equal("read_only_role_preview", await ErrorCodeAsync(granted));

        await AssertNothingWasGrantedAsync();
    }

    #endregion

    #region Granting, changing and ending

    /// <summary>
    /// The first criterion, end to end: the administrator grants Pro with a reason, and the stored
    /// grant is a complimentary one with revision 1 - with no payment, billing status or subscription
    /// row anywhere, because this surface cannot write one.
    /// </summary>
    [Fact]
    public async Task The_administrator_can_grant_a_plan_with_a_reason()
    {
        var response = await PutAsync(
            _factory.AsAdministrator(),
            _factory.OtherBusinessId,
            "Pro",
            reason: "Complimentary pilot",
            revision: 0);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(_factory.OtherBusinessId, body.GetProperty("businessId").GetInt32());
        Assert.True(body.GetProperty("hasGrant").GetBoolean());
        Assert.Equal("Pro", body.GetProperty("plan").GetString());
        Assert.Equal("Complimentary pilot", body.GetProperty("reason").GetString());
        Assert.Equal(1, body.GetProperty("revision").GetInt32());
        Assert.True(body.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("endsAtUtc").ValueKind);

        var grant = Assert.Single(await _factory.GrantsOfAsync(_factory.OtherBusinessId));
        Assert.Equal(SubscriptionPlan.Pro, grant.Plan);
        Assert.Equal(ManualPlanGrantSource.Complimentary, grant.Source);
        Assert.Equal("Complimentary pilot", grant.Reason);
        Assert.Equal(1, grant.Revision);

        Assert.Empty(await _factory.SubscriptionsOfAsync(_factory.OtherBusinessId));
    }

    /// <summary>
    /// The audit criterion: the grant and its audit row are committed together, the row names the
    /// acting administrator, and the log event beside it carries no reason text at all.
    /// </summary>
    [Fact]
    public async Task A_grant_and_its_audit_row_are_committed_together_and_the_log_omits_the_reason()
    {
        _factory.ClearLog();

        await PutAsync(
            _factory.AsAdministrator(),
            _factory.OtherBusinessId,
            "Starter",
            reason: "Grandfathered pilot site",
            revision: 0);

        var audit = Assert.Single(await _factory.AuditsOfAsync(_factory.OtherBusinessId));
        Assert.Equal(PlatformPlanGrantChange.Created, audit.Change);
        Assert.Null(audit.FromPlan);
        Assert.Equal(SubscriptionPlan.Starter, audit.ToPlan);
        Assert.Equal("Grandfathered pilot site", audit.Reason);
        Assert.Equal(1, audit.Revision);
        Assert.Equal(
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            audit.ActorObjectId,
            ignoreCase: true);

        var logged = Assert.Single(_factory.LoggedEntries(), entry => entry.Contains("Platform plan grant audit", StringComparison.Ordinal));
        Assert.Contains($"business {_factory.OtherBusinessId}", logged, StringComparison.Ordinal);
        Assert.Contains("Created", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Grandfathered pilot site", logged, StringComparison.Ordinal);
    }

    /// <summary>
    /// The end date criterion and its boundary: the administrator chooses the last day, the grant is
    /// stored as the exclusive start of the next one, and both the instant and the date come back.
    /// </summary>
    [Fact]
    public async Task A_grant_with_an_end_date_covers_that_whole_day()
    {
        var response = await PutAsync(
            _factory.AsAdministrator(),
            _factory.OtherBusinessId,
            "Starter",
            reason: "Trial",
            revision: 0,
            endsOnUtc: "2099-12-31");

        var body = await ReadAsync(response);
        Assert.Equal("2100-01-01T00:00:00Z", body.GetProperty("endsAtUtc").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("2099-12-31", body.GetProperty("endsOnUtc").GetString());
        Assert.True(body.GetProperty("active").GetBoolean());

        var grant = Assert.Single(await _factory.GrantsOfAsync(_factory.OtherBusinessId));
        Assert.Equal(new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc), grant.EndsAtUtc);
    }

    [Fact]
    public async Task A_change_records_the_plan_it_moved_from_and_the_next_revision()
    {
        var administrator = _factory.AsAdministrator();
        await PutAsync(administrator, _factory.OtherBusinessId, "Starter", reason: "Pilot", revision: 0);

        var response = await PutAsync(
            administrator, _factory.OtherBusinessId, "Pro", reason: "Upgraded", revision: 1);

        var body = await ReadAsync(response);
        Assert.Equal("Pro", body.GetProperty("plan").GetString());
        Assert.Equal(2, body.GetProperty("revision").GetInt32());

        var grant = Assert.Single(await _factory.GrantsOfAsync(_factory.OtherBusinessId));
        Assert.Equal(SubscriptionPlan.Pro, grant.Plan);
        Assert.Equal(2, grant.Revision);

        var trail = await _factory.AuditsOfAsync(_factory.OtherBusinessId);
        Assert.Equal(2, trail.Count);
        Assert.Equal(SubscriptionPlan.Starter, trail[1].FromPlan);
        Assert.Equal(SubscriptionPlan.Pro, trail[1].ToPlan);
    }

    /// <summary>
    /// Ending a grant keeps the row - its plan, its start and its granting reason - and stops it now.
    /// Nothing is deleted, which is what makes the trail readable afterwards.
    /// </summary>
    [Fact]
    public async Task Ending_a_grant_stops_it_and_keeps_the_row()
    {
        var administrator = _factory.AsAdministrator();
        await PutAsync(administrator, _factory.OtherBusinessId, "Pro", reason: "Pilot", revision: 0);

        var response = await EndAsync(administrator, _factory.OtherBusinessId, "Pilot finished", revision: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("hasGrant").GetBoolean());
        Assert.Equal("Pro", body.GetProperty("plan").GetString());
        Assert.False(body.GetProperty("active").GetBoolean());
        Assert.Equal(2, body.GetProperty("revision").GetInt32());

        var grant = Assert.Single(await _factory.GrantsOfAsync(_factory.OtherBusinessId));
        Assert.Equal(SubscriptionPlan.Pro, grant.Plan);
        Assert.Equal("Pilot", grant.Reason);
        Assert.NotNull(grant.EndsAtUtc);

        var ended = (await _factory.AuditsOfAsync(_factory.OtherBusinessId))[1];
        Assert.Equal(PlatformPlanGrantChange.Ended, ended.Change);
        Assert.Equal(SubscriptionPlan.Pro, ended.FromPlan);
        Assert.Null(ended.ToPlan);
        Assert.Equal("Pilot finished", ended.Reason);
    }

    [Fact]
    public async Task Ending_a_grant_a_business_does_not_hold_is_a_conflict()
    {
        var response = await EndAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, "Over", revision: 0);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertNothingWasGrantedAsync();
    }

    #endregion

    #region Refusals

    /// <summary>
    /// The concurrency criterion over HTTP: the second of two administrators - or the second of one
    /// administrator's two tabs - presents the revision they read, and is refused with <c>409</c>
    /// rather than overwriting the first decision.
    /// </summary>
    [Fact]
    public async Task A_stale_revision_is_refused_with_a_conflict_and_changes_nothing()
    {
        var administrator = _factory.AsAdministrator();
        await PutAsync(administrator, _factory.OtherBusinessId, "Starter", reason: "Pilot", revision: 0);

        // The other tab still believes the business holds no grant at all.
        var stale = await PutAsync(
            administrator, _factory.OtherBusinessId, "Pro", reason: "From a stale tab", revision: 0);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains(
            ManualPlanGrantPolicy.StaleRevisionMessage,
            await stale.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var grant = Assert.Single(await _factory.GrantsOfAsync(_factory.OtherBusinessId));
        Assert.Equal(SubscriptionPlan.Starter, grant.Plan);
        Assert.Equal(1, grant.Revision);
        Assert.Single(await _factory.AuditsOfAsync(_factory.OtherBusinessId));
    }

    /// <summary>
    /// Nothing but Starter and Pro is a grant: Free, a name this code does not declare and a numeric
    /// string are each <c>400</c>, and none of them writes anything.
    /// </summary>
    [Theory]
    [InlineData("Free")]
    [InlineData("Platinum")]
    [InlineData("30")]
    [InlineData("")]
    public async Task A_plan_that_is_not_grantable_is_refused(string plan)
    {
        var response = await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, plan, reason: "Why not", revision: 0);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingWasGrantedAsync();
    }

    [Fact]
    public async Task A_grant_without_a_reason_is_refused()
    {
        var response = await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, "Pro", reason: "   ", revision: 0);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingWasGrantedAsync();
    }

    [Fact]
    public async Task An_end_date_already_over_is_refused()
    {
        var response = await PutAsync(
            _factory.AsAdministrator(),
            _factory.OtherBusinessId,
            "Pro",
            reason: "Backdated",
            revision: 0,
            endsOnUtc: "2020-01-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingWasGrantedAsync();
    }

    /// <summary>
    /// The revision is required rather than defaulted: a body that omits it would otherwise bind to
    /// <c>0</c> and turn every under-posted request into an attempt to create a first grant, which is
    /// exactly what the concurrency check exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_request_without_a_revision_is_refused()
    {
        var response = await _factory
            .AsAdministrator()
            .PutAsJsonAsync(GrantUrl(_factory.OtherBusinessId), new { plan = "Pro", reason = "Pilot" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingWasGrantedAsync();
    }

    [Fact]
    public async Task An_unknown_business_is_not_found_on_every_endpoint()
    {
        foreach (var response in await AllEndpointsAsync(_factory.AsAdministrator(), businessId: 987654))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        await AssertNothingWasGrantedAsync();
    }

    #endregion

    #region What the business's own members read

    /// <summary>
    /// The criterion "the business's members see the new effective plan through
    /// <c>GET /api/me/access</c> exactly as defined by #966 on their next read" - and the
    /// two-business criterion beside it: the other business's Owner still reads Free.
    /// </summary>
    [Fact]
    public async Task The_granted_business_reads_the_new_plan_and_the_other_business_does_not()
    {
        await _factory.CompleteGrandfatheringAsync();

        Assert.Equal("Free", await PlanOfAsync(ApiFactory.OtherBusinessOwnerObjectId));

        await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, "Pro", reason: "Pilot", revision: 0);

        Assert.Equal("Pro", await PlanOfAsync(ApiFactory.OtherBusinessOwnerObjectId));
        Assert.Equal("ManualGrant", await SourceOfAsync(ApiFactory.OtherBusinessOwnerObjectId));

        // The trading business was never named by the decision and is unaffected.
        Assert.Equal("Free", await PlanOfAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId));
    }

    /// <summary>And ending the grant returns that business to Free on its members' next read.</summary>
    [Fact]
    public async Task Ending_the_grant_returns_the_business_to_free_on_the_next_read()
    {
        await _factory.CompleteGrandfatheringAsync();
        var administrator = _factory.AsAdministrator();

        await PutAsync(administrator, _factory.OtherBusinessId, "Pro", reason: "Pilot", revision: 0);
        Assert.Equal("Pro", await PlanOfAsync(ApiFactory.OtherBusinessOwnerObjectId));

        await EndAsync(administrator, _factory.OtherBusinessId, "Pilot finished", revision: 1);

        Assert.Equal("Free", await PlanOfAsync(ApiFactory.OtherBusinessOwnerObjectId));
        Assert.Equal("NoActiveRecord", await SourceOfAsync(ApiFactory.OtherBusinessOwnerObjectId));
    }

    #endregion

    private static string GrantUrl(int businessId) =>
        $"/api/super-admin/businesses/{businessId}/plan-grant";

    private async Task<string?> PlanOfAsync(string objectId)
    {
        var entitlement = await EntitlementOfAsync(objectId);
        return entitlement.GetProperty("plan").ValueKind == JsonValueKind.Null
            ? null
            : entitlement.GetProperty("plan").GetString();
    }

    private async Task<string?> SourceOfAsync(string objectId) =>
        (await EntitlementOfAsync(objectId)).GetProperty("source").GetString();

    private async Task<JsonElement> EntitlementOfAsync(string objectId)
    {
        var response = await _factory.Authenticated(objectId).GetAsync(AccessUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("entitlement");
    }

    private Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        int businessId,
        string? plan,
        string? reason,
        int revision,
        string? endsOnUtc = null) =>
        client.PutAsJsonAsync(
            GrantUrl(businessId),
            new { plan, endsOnUtc, reason, revision });

    private Task<HttpResponseMessage> EndAsync(
        HttpClient client,
        int businessId,
        string? reason,
        int revision) =>
        client.PostAsJsonAsync($"{GrantUrl(businessId)}/end", new { reason, revision });

    /// <summary>
    /// Every endpoint of the surface, called once - so a claim about who may reach it is made about
    /// all three rather than about the one a test happened to pick.
    /// </summary>
    private async Task<IReadOnlyList<HttpResponseMessage>> AllEndpointsAsync(
        HttpClient client,
        int? businessId = null)
    {
        var target = businessId ?? _factory.OtherBusinessId;

        return
        [
            await client.GetAsync(GrantUrl(target)),
            await PutAsync(client, target, "Pro", reason: "Pilot", revision: 0),
            await EndAsync(client, target, reason: "Over", revision: 1),
        ];
    }

    private async Task AssertNothingWasGrantedAsync()
    {
        Assert.Empty(await _factory.GrantsOfAsync(_factory.OtherBusinessId));
        Assert.Empty(await _factory.GrantsOfAsync(_factory.TradingBusinessId));
        Assert.Empty(await _factory.AuditsOfAsync(_factory.OtherBusinessId));
        Assert.Empty(await _factory.AuditsOfAsync(_factory.TradingBusinessId));
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// The Super Admin fixture's world, plus plan enforcement switched on, a second trading business
    /// with an Owner of its own, and an Operator in the trading business - so every refusal below is
    /// asked of a real business role.
    /// </summary>
    public sealed class ApiFactory : SuperAdminBusinessesApiFactory
    {
        public const string OtherBusinessOwnerObjectId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        public const string TradingOperatorObjectId = "66666666-6666-6666-6666-666666666666";
        public const string SessionId = "the-support-session-id-this-file-presents";

        private static readonly DateTime Cutoff = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The business every decision in this file is about - never the administrator's own.</summary>
        public int OtherBusinessId { get; private set; }

        public HttpClient Previewing(string role)
        {
            var client = Authenticated(AdministratorObjectId);
            client.DefaultRequestHeaders.TryAddWithoutValidation(RolePreviewHeader.Name, role);
            return client;
        }

        /// <summary>
        /// An ordinary member who presents a seeded support-session id. The session belongs to the
        /// administrator, so this is the "a member who sets the header is still a member" case.
        /// </summary>
        public async Task<HttpClient> MemberPresentingASupportSession()
        {
            await SeedSupportSessionAsync(TradingBusinessId, AdministratorObjectId, SessionId);

            var client = Authenticated(TradingOwnerObjectId);
            client.DefaultRequestHeaders.Add(SupportSessionHeader.Name, SessionId);
            return client;
        }

        /// <summary>The grandfathering step recorded as complete, which is what lets enforcement take effect.</summary>
        public async Task CompleteGrandfatheringAsync()
        {
            await using var db = Database();
            db.SubscriptionGrandfatherings.RemoveRange(await db.SubscriptionGrandfatherings.ToListAsync());
            await db.SaveChangesAsync();

            db.SubscriptionGrandfatherings.Add(new SubscriptionGrandfathering
            {
                Id = SubscriptionGrandfathering.SingletonId,
                CutoffUtc = Cutoff,
                CompletedAtUtc = Cutoff.AddMinutes(5),
                GrantedBusinessCount = 0,
            });
            await db.SaveChangesAsync();
        }

        public async Task<IReadOnlyList<BusinessPlanGrant>> GrantsOfAsync(int businessId)
        {
            await using var db = Database();
            return await db.BusinessPlanGrants
                .AsNoTracking()
                .Where(row => row.BusinessId == businessId)
                .OrderBy(row => row.Id)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<BusinessPlanGrantAudit>> AuditsOfAsync(int businessId)
        {
            await using var db = Database();
            return await db.BusinessPlanGrantAudits
                .AsNoTracking()
                .Where(row => row.BusinessId == businessId)
                .OrderBy(row => row.Id)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<BusinessBillingSubscription>> SubscriptionsOfAsync(int businessId)
        {
            await using var db = Database();
            return await db.BusinessBillingSubscriptions
                .AsNoTracking()
                .Where(row => row.BusinessId == businessId)
                .ToListAsync();
        }

        public new async Task ResetAsync()
        {
            await ClearSubscriptionStateAsync();

            await base.ResetAsync();

            OtherBusinessId = await SeedBusinessAsync("Second Vending Co", BusinessStatus.Active);
            await SeedMembershipAsync(
                OtherBusinessId, OtherBusinessOwnerObjectId, BusinessRole.Owner, isActive: true);
            await SeedMembershipAsync(
                TradingBusinessId, TradingOperatorObjectId, BusinessRole.Operator, isActive: true);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            // Plan enforcement on, which is the only state in which a member is reported a plan at
            // all (issue #966). It changes nothing about who may decide a grant.
            builder.UseSetting(ConfiguredSubscriptionEnforcementFlag.EnabledKey, "true");

            base.ConfigureWebHost(builder);
        }

        private async Task ClearSubscriptionStateAsync()
        {
            await using var db = Database();
            db.BusinessPlanGrantAudits.RemoveRange(await db.BusinessPlanGrantAudits.ToListAsync());
            db.BusinessPlanGrants.RemoveRange(await db.BusinessPlanGrants.ToListAsync());
            db.BusinessBillingSubscriptions.RemoveRange(await db.BusinessBillingSubscriptions.ToListAsync());
            db.SubscriptionGrandfatherings.RemoveRange(await db.SubscriptionGrandfatherings.ToListAsync());
            await db.SaveChangesAsync();
        }
    }
}
