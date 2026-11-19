using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.Subscriptions;
using Inventory.Infrastructure.Models;
using InventoryApi.Adapters.Subscriptions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The beta Pro grant an approval writes (issue #1258), over real HTTP against a deployment that has
/// switched <c>Subscriptions:BetaProGrantOnApproval</c> <strong>on</strong> - and, so that a plan is
/// reported at all, <c>Subscriptions:EnforcementEnabled</c> with it.
///
/// <para>Everything here goes through the complete pipeline: the platform-admin policy and its
/// marker, <c>BusinessScopeMiddleware</c>, the denied business scope a platform request carries, the
/// write lease, the tenant query filters and relational SQLite with the real migration history. The
/// shipped state - the setting off - is asserted by <c>SuperAdminBusinessesApiTests</c>, whose host
/// sets neither setting.</para>
///
/// <para>The acceptance criteria this file covers end to end are the one-transaction write, the new
/// Owner reading <c>Pro</c> from <c>GET /api/me/access</c>, the grant appearing on the Super Admin
/// plan view with its source and being endable there, and the three transitions that must never
/// write a grant.</para>
/// </summary>
public class SuperAdminBusinessApprovalPlanGrantApiTests
    : IClassFixture<SuperAdminBusinessApprovalPlanGrantApiTests.ApiFactory>, IAsyncLifetime
{
    /// <summary>
    /// The recorded grandfathering cutoff these tests complete the rollout with. It is deliberately
    /// in the past, because this host answers against the real clock and a grant entitles from its
    /// start instant inclusive.
    /// </summary>
    private static readonly DateTime Cutoff = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ApiFactory _factory;

    public SuperAdminBusinessApprovalPlanGrantApiTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The headline criterion: approving an application while the setting is on puts the business on
    /// Pro, and its new Owner reads that from the one endpoint that publishes a plan - with the
    /// manual grant named as the source, because that is the record it came from.
    /// </summary>
    [Fact]
    public async Task Approving_grants_pro_and_the_new_owner_reads_it_from_the_access_endpoint()
    {
        using var administrator = _factory.AsAdministrator();

        var approval = await administrator.PostAsync(ApproveUri(_factory.PendingBusinessId), null);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);

        await using (var db = _factory.Database())
        {
            var grant = await db.BusinessPlanGrants.AsNoTracking().SingleAsync();
            Assert.Equal(_factory.PendingBusinessId, grant.BusinessId);
            Assert.Equal(SubscriptionPlan.Pro, grant.Plan);
            Assert.Equal(ManualPlanGrantSource.Beta, grant.Source);
            Assert.Null(grant.EndsAtUtc);
        }

        await _factory.CompleteGrandfatheringAsync(Cutoff);

        var entitlement = await ReadEntitlementAsync(SuperAdminBusinessesApiFactory.ApplicantObjectId);
        Assert.True(entitlement.GetProperty("enforced").GetBoolean());
        Assert.Equal("Pro", entitlement.GetProperty("plan").GetString());
        Assert.Equal("ManualGrant", entitlement.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, entitlement.GetProperty("endsAtUtc").ValueKind);
    }

    /// <summary>
    /// The grant is the #982 surface's own record from the moment it is written: that surface reports
    /// it with its source, and a Super Admin can end it there. Ending it leaves the business on Free,
    /// which is what "grants already written stay until a Super Admin ends them" means from the other
    /// side.
    /// </summary>
    [Fact]
    public async Task The_grant_is_reported_with_its_source_and_can_be_ended_by_a_super_admin()
    {
        using var administrator = _factory.AsAdministrator();
        await administrator.PostAsync(ApproveUri(_factory.PendingBusinessId), null);
        await _factory.CompleteGrandfatheringAsync(Cutoff);

        var view = await ReadJsonAsync(
            administrator,
            $"/api/super-admin/businesses/{_factory.PendingBusinessId}/plan-grant");

        Assert.True(view.GetProperty("hasGrant").GetBoolean());
        Assert.Equal("Pro", view.GetProperty("plan").GetString());
        Assert.Equal("Beta", view.GetProperty("source").GetString());
        Assert.True(view.GetProperty("active").GetBoolean());

        var ended = await administrator.PostAsJsonAsync(
            $"/api/super-admin/businesses/{_factory.PendingBusinessId}/plan-grant/end",
            new { reason = "Beta over for this business.", revision = view.GetProperty("revision").GetInt32() });
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);

        var entitlement = await ReadEntitlementAsync(SuperAdminBusinessesApiFactory.ApplicantObjectId);
        Assert.Equal("Free", entitlement.GetProperty("plan").GetString());
        Assert.Equal("NoActiveRecord", entitlement.GetProperty("source").GetString());

        // Nothing was deleted: the row keeps the plan and the source it was granted with.
        await using var db = _factory.Database();
        var grant = await db.BusinessPlanGrants.AsNoTracking().SingleAsync();
        Assert.Equal(ManualPlanGrantSource.Beta, grant.Source);
        Assert.NotNull(grant.EndsAtUtc);
    }

    /// <summary>
    /// A refused application is not a business, so there is nothing to put on a plan. The setting is
    /// on throughout this file, which is what makes this assertion about the rejection rather than
    /// about the deployment.
    /// </summary>
    [Fact]
    public async Task Rejecting_an_application_writes_no_grant()
    {
        using var administrator = _factory.AsAdministrator();

        var rejection = await administrator.PostAsJsonAsync(
            $"/api/super-admin/businesses/{_factory.PendingBusinessId}/reject",
            new { reason = "Needs a registered ABN." });
        Assert.Equal(HttpStatusCode.OK, rejection.StatusCode);

        await using var db = _factory.Database();
        Assert.Empty(await db.BusinessPlanGrants.AsNoTracking().ToListAsync());
        Assert.Empty(await db.BusinessPlanGrantAudits.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Switching a business off and on again is not an approval and grants nothing - not even the
    /// reactivation, which is the transition that most looks like one. A business that was never
    /// granted Pro is not granted it by being switched back on.
    /// </summary>
    [Fact]
    public async Task Deactivating_and_reactivating_a_business_writes_no_grant()
    {
        using var administrator = _factory.AsAdministrator();

        var off = await administrator.PostAsync(
            $"/api/super-admin/businesses/{_factory.TradingBusinessId}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var on = await administrator.PostAsync(
            $"/api/super-admin/businesses/{_factory.TradingBusinessId}/reactivate", null);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);

        await using var db = _factory.Database();
        Assert.Empty(await db.BusinessPlanGrants.AsNoTracking().ToListAsync());
        Assert.Empty(await db.BusinessPlanGrantAudits.AsNoTracking().ToListAsync());
    }

    private static string ApproveUri(int businessId) =>
        $"/api/super-admin/businesses/{businessId}/approve";

    private async Task<JsonElement> ReadEntitlementAsync(string objectId)
    {
        using var client = _factory.Authenticated(objectId);
        var access = await ReadJsonAsync(client, "/api/me/access");
        return access.GetProperty("entitlement");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpClient client, string uri)
    {
        var response = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The Super Admin business host with both subscription settings on: the beta grant of this
    /// issue, and the enforcement of issue #966 without which no plan is reported at all.
    /// </summary>
    public sealed class ApiFactory : SuperAdminBusinessesApiFactory
    {
        /// <summary>The grandfathering record completed, which is what lets enforcement take effect.</summary>
        public async Task CompleteGrandfatheringAsync(DateTime cutoffUtc)
        {
            await using var db = Database();
            db.SubscriptionGrandfatherings.RemoveRange(await db.SubscriptionGrandfatherings.ToListAsync());
            await db.SaveChangesAsync();

            db.SubscriptionGrandfatherings.Add(new SubscriptionGrandfathering
            {
                Id = SubscriptionGrandfathering.SingletonId,
                CutoffUtc = cutoffUtc,
                CompletedAtUtc = cutoffUtc.AddMinutes(5),
                GrantedBusinessCount = 1,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Puts the world back, and the subscription state with it, so each test starts from
        /// "nothing has ever been granted or grandfathered" rather than from what its predecessor
        /// approved.
        /// </summary>
        public new async Task ResetAsync()
        {
            await ClearSubscriptionStateAsync();
            await base.ResetAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseSetting(ConfiguredSubscriptionEnforcementFlag.EnabledKey, "true");
            builder.UseSetting(ConfiguredBetaProGrantOnApprovalFlag.EnabledKey, "true");

            base.ConfigureWebHost(builder);
        }

        private async Task ClearSubscriptionStateAsync()
        {
            await using var db = Database();
            db.BusinessPlanGrantAudits.RemoveRange(await db.BusinessPlanGrantAudits.ToListAsync());
            db.BusinessPlanGrants.RemoveRange(await db.BusinessPlanGrants.ToListAsync());
            db.SubscriptionGrandfatherings.RemoveRange(await db.SubscriptionGrandfatherings.ToListAsync());
            await db.SaveChangesAsync();
        }
    }
}
