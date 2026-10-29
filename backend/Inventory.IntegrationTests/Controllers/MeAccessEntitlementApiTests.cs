using System.Net;
using System.Text.Json;
using Inventory.Domain.ConnectedMachines;
using Inventory.Domain.Nayax;
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
/// The entitlement <c>GET /api/me/access</c> publishes (issue #966), over real HTTP against a
/// deployment that <strong>has</strong> switched the rollout setting on - which is the only state
/// in which a plan is reported at all.
///
/// <para>Everything here goes through the complete pipeline: authentication, <c>[Authorize]</c>,
/// the required scope, <c>RolePreviewMiddleware</c>, <c>BusinessScopeMiddleware</c>, the support
/// session accessor, the tenant query filters and relational SQLite with the real migration history.
/// The shipped state - the setting off - is asserted by <c>MeAccessApiTests</c>, whose host sets
/// nothing.</para>
///
/// <para>Two businesses exist throughout, so every answer about a plan is one that could only have
/// come from the caller's own resolved business: there is no read endpoint for an entitlement and no
/// business id anywhere in the request.</para>
/// </summary>
public class MeAccessEntitlementApiTests : IClassFixture<MeAccessEntitlementApiTests.ApiFactory>, IAsyncLifetime
{
    private const string Uri = "/api/me/access";

    /// <summary>
    /// The recorded grandfathering cutoff these tests grant from. It is deliberately in the
    /// <em>past</em>: this host answers against the real clock, and a grant starts at its start
    /// instant inclusive, so a cutoff in the future would be a grant that has not begun - which is a
    /// boundary <c>EffectiveEntitlementPolicyTests</c> asserts at the tick rather than something to
    /// discover here.
    /// </summary>
    private static readonly DateTime Cutoff = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ApiFactory _factory;

    public MeAccessEntitlementApiTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The criterion "enforcement cannot start early", over HTTP: the setting is on but the
    /// grandfathering step has not completed, so the member is told nothing is enforced - not that
    /// they are on Free.
    /// </summary>
    [Fact]
    public async Task With_the_setting_on_and_no_completion_marker_nothing_is_enforced()
    {
        var entitlement = await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        Assert.False(entitlement.GetProperty("enforced").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entitlement.GetProperty("plan").ValueKind);
        Assert.Equal("GrandfatheringNotCompleted", entitlement.GetProperty("source").GetString());
    }

    /// <summary>And a step that started but never finished is the same answer: the marker is what counts.</summary>
    [Fact]
    public async Task An_interrupted_grandfathering_step_still_enforces_nothing()
    {
        await _factory.StartGrandfatheringAsync(Cutoff);

        var entitlement = await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        Assert.False(entitlement.GetProperty("enforced").GetBoolean());
        Assert.Equal("GrandfatheringNotCompleted", entitlement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Once_the_step_has_completed_a_business_with_no_record_is_on_free()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);

        var entitlement = await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        Assert.True(entitlement.GetProperty("enforced").GetBoolean());
        Assert.Equal("Free", entitlement.GetProperty("plan").GetString());
        Assert.Equal("NoActiveRecord", entitlement.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, entitlement.GetProperty("endsAtUtc").ValueKind);
    }

    /// <summary>
    /// The grandfathered grant, as the step writes it: Pro, from the cutoff, with no end date - and
    /// reported as the manual grant it is rather than as a paid plan.
    /// </summary>
    [Fact]
    public async Task A_grandfathered_grant_reports_pro_from_the_grant_with_no_end()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.GrantAsync(
            _factory.TradingBusinessId,
            SubscriptionPlan.Pro,
            ManualPlanGrantSource.Grandfathered,
            Cutoff,
            endsAtUtc: null);

        var entitlement = await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        Assert.True(entitlement.GetProperty("enforced").GetBoolean());
        Assert.Equal("Pro", entitlement.GetProperty("plan").GetString());
        Assert.Equal("ManualGrant", entitlement.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, entitlement.GetProperty("endsAtUtc").ValueKind);
    }

    [Fact]
    public async Task An_active_paid_plan_reports_the_subscription_and_its_period_end()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.SubscribeAsync(
            _factory.TradingBusinessId,
            SubscriptionPlan.Starter,
            BillingSubscriptionStatus.Active,
            Cutoff,
            Cutoff.AddYears(5));

        var entitlement = await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        Assert.Equal("Starter", entitlement.GetProperty("plan").GetString());
        Assert.Equal("BillingSubscription", entitlement.GetProperty("source").GetString());
        Assert.Equal(Cutoff.AddYears(5), entitlement.GetProperty("endsAtUtc").GetDateTime());
    }

    /// <summary>The higher of the two records wins, and neither record changed the other.</summary>
    [Fact]
    public async Task The_higher_of_the_grant_and_the_paid_plan_is_reported()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.GrantAsync(
            _factory.TradingBusinessId,
            SubscriptionPlan.Starter,
            ManualPlanGrantSource.Complimentary,
            Cutoff,
            Cutoff.AddYears(5));
        await _factory.SubscribeAsync(
            _factory.TradingBusinessId,
            SubscriptionPlan.Pro,
            BillingSubscriptionStatus.Active,
            Cutoff,
            Cutoff.AddYears(5));

        var entitlement = await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        Assert.Equal("Pro", entitlement.GetProperty("plan").GetString());
        Assert.Equal("BillingSubscription", entitlement.GetProperty("source").GetString());

        // Both rows are still exactly as they were stored: reading an entitlement writes nothing.
        await using var db = _factory.Database();
        Assert.Equal(
            SubscriptionPlan.Starter,
            (await db.BusinessPlanGrants.AsNoTracking().SingleAsync()).Plan);
        Assert.Equal(
            SubscriptionPlan.Pro,
            (await db.BusinessBillingSubscriptions.AsNoTracking().SingleAsync()).Plan);
    }

    /// <summary>
    /// Expired and cancelled records leave the business on Free for paid features, which is the
    /// fail-closed direction of the decision of 29 October 2026.
    /// </summary>
    [Fact]
    public async Task An_expired_grant_and_a_cancelled_subscription_leave_the_business_on_free()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.GrantAsync(
            _factory.TradingBusinessId,
            SubscriptionPlan.Pro,
            ManualPlanGrantSource.Complimentary,
            Cutoff,
            Cutoff.AddDays(1));
        await _factory.SubscribeAsync(
            _factory.TradingBusinessId,
            SubscriptionPlan.Pro,
            BillingSubscriptionStatus.Cancelled,
            Cutoff,
            Cutoff.AddYears(5));

        var entitlement = await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        Assert.Equal("Free", entitlement.GetProperty("plan").GetString());
        Assert.Equal("NoActiveRecord", entitlement.GetProperty("source").GetString());
    }

    /// <summary>
    /// The cross-tenant case: the other business's Pro grant is invisible to this business's Owner,
    /// and each is told their own plan. Nothing in either request names a business.
    /// </summary>
    [Fact]
    public async Task One_businesss_plan_never_reaches_the_other()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.GrantAsync(
            _factory.OtherBusinessId,
            SubscriptionPlan.Pro,
            ManualPlanGrantSource.Grandfathered,
            Cutoff,
            endsAtUtc: null);

        var trading = await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        var other = await ReadAsync(ApiFactory.OtherBusinessOwnerObjectId);

        Assert.Equal("Free", trading.GetProperty("plan").GetString());
        Assert.Equal("Pro", other.GetProperty("plan").GetString());
    }

    /// <summary>
    /// The criterion decided 29 October 2026: inside a support session the entitlement read reports
    /// the <em>viewed</em> business's own effective plan. The administrator's own business is on Free
    /// here and the viewed one on Pro, so the answer could not have come from the administrator's
    /// own membership.
    /// </summary>
    [Fact]
    public async Task A_support_session_reports_the_viewed_businesss_own_plan()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.GrantAsync(
            _factory.TradingBusinessId,
            SubscriptionPlan.Pro,
            ManualPlanGrantSource.Grandfathered,
            Cutoff,
            endsAtUtc: null);

        // The administrator's own business, which has no plan at all.
        var ownBusiness = await ReadAsync(SuperAdminBusinessesApiFactory.AdministratorObjectId);
        Assert.Equal("Free", ownBusiness.GetProperty("plan").GetString());

        var inSession = await ReadBodyAsync(await _factory.InSupportSession());
        var entitlement = inSession.GetProperty("entitlement");

        Assert.Equal("Pro", entitlement.GetProperty("plan").GetString());
        Assert.Equal("ManualGrant", entitlement.GetProperty("source").GetString());
        // And it is unmistakably the support view: the session block is reported beside it.
        Assert.NotEqual(JsonValueKind.Null, inSession.GetProperty("supportSession").ValueKind);
    }

    /// <summary>
    /// And the other half of that decision: a role preview narrows role capabilities only and never
    /// changes the plan. The previewed Operator is told the same Pro plan with a smaller capability
    /// set.
    /// </summary>
    [Fact]
    public async Task A_role_preview_narrows_capabilities_and_never_the_plan()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.GrantAsync(
            _factory.AdminBusinessId,
            SubscriptionPlan.Pro,
            ManualPlanGrantSource.Grandfathered,
            Cutoff,
            endsAtUtc: null);

        var withoutPreview = await ReadBodyAsync(
            _factory.Authenticated(SuperAdminBusinessesApiFactory.AdministratorObjectId));
        var previewing = await ReadBodyAsync(_factory.Previewing("Operator"));

        Assert.Equal("Pro", withoutPreview.GetProperty("entitlement").GetProperty("plan").GetString());
        Assert.Equal("Pro", previewing.GetProperty("entitlement").GetProperty("plan").GetString());
        Assert.Equal(
            withoutPreview.GetProperty("entitlement").ToString(),
            previewing.GetProperty("entitlement").ToString());

        // The capabilities did narrow, so the preview was honoured rather than ignored.
        Assert.DoesNotContain(
            "Reports.View",
            previewing.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains(
            "Reports.View",
            withoutPreview.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>
    /// A plan a newer deployment wrote and this one does not declare is a record that cannot be
    /// read, so the business is on Free for paid features rather than on the plan nearest to it.
    /// </summary>
    [Fact]
    public async Task A_stored_plan_this_deployment_does_not_declare_leaves_the_business_on_free()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.GrantAsync(
            _factory.TradingBusinessId,
            (SubscriptionPlan)99,
            ManualPlanGrantSource.Grandfathered,
            Cutoff,
            endsAtUtc: null);

        var entitlement = await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);

        Assert.Equal("Free", entitlement.GetProperty("plan").GetString());
        Assert.Equal("NoActiveRecord", entitlement.GetProperty("source").GetString());
    }

    /// <summary>
    /// A grant that ends applies until its end instant and not past it, and the answer is recomputed
    /// per request with no cache: the same client is told Pro and then Free as the stored end date
    /// moves into the past.
    /// </summary>
    [Fact]
    public async Task The_answer_is_recomputed_per_request_with_no_cache()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        var grantId = await _factory.GrantAsync(
            _factory.TradingBusinessId,
            SubscriptionPlan.Pro,
            ManualPlanGrantSource.Complimentary,
            Cutoff,
            Cutoff.AddYears(5));

        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        Assert.Equal(
            "Pro",
            (await ReadBodyAsync(client)).GetProperty("entitlement").GetProperty("plan").GetString());

        await _factory.EndGrantAsync(grantId, Cutoff.AddDays(1));

        Assert.Equal(
            "Free",
            (await ReadBodyAsync(client)).GetProperty("entitlement").GetProperty("plan").GetString());
    }

    /// <summary>
    /// The connected machines of issue #978, over HTTP: a Pro business's allowance, the count its
    /// current Nayax account was last counted at, and how many machines are over the allowance.
    ///
    /// <para>Being over the allowance changes nothing else in the response - it is billed, never
    /// blocked - and the count is recorded evidence, so answering it makes no Nayax call.</para>
    /// </summary>
    [Fact]
    public async Task A_pro_business_reports_its_allowance_and_its_recorded_count()
    {
        await EnforcedProAsync();
        await _factory.ConnectAsync(_factory.TradingBusinessId, accountSeries: 1);
        await _factory.SnapshotAsync(_factory.TradingBusinessId, Today, accountSeries: 1, machineCount: 7);

        var entitlement = await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        var connectedMachines = entitlement.GetProperty("connectedMachines");

        Assert.Equal("Pro", entitlement.GetProperty("plan").GetString());
        Assert.Equal(5, connectedMachines.GetProperty("includedByPlan").GetInt32());
        Assert.Equal(0, connectedMachines.GetProperty("purchasedAdditional").GetInt32());
        Assert.Equal(5, connectedMachines.GetProperty("allowance").GetInt32());
        Assert.Equal(7, connectedMachines.GetProperty("count").GetInt32());
        Assert.Equal(Today, connectedMachines.GetProperty("asOfDateUtc").GetDateTime());
        Assert.Equal(0, connectedMachines.GetProperty("ageInDays").GetInt32());
        Assert.Equal("Current", connectedMachines.GetProperty("state").GetString());
        Assert.Equal("None", connectedMachines.GetProperty("unavailableReason").GetString());
        Assert.Equal(2, connectedMachines.GetProperty("machinesOverAllowance").GetInt32());
    }

    /// <summary>
    /// An older snapshot remains the count and is reported stale with its age - the answer a failed,
    /// partial or paginated-incomplete read leaves in place, because such a read stores nothing.
    /// </summary>
    [Fact]
    public async Task An_older_count_is_reported_stale_with_its_age()
    {
        await EnforcedProAsync();
        await _factory.ConnectAsync(_factory.TradingBusinessId, accountSeries: 1);
        await _factory.SnapshotAsync(
            _factory.TradingBusinessId,
            Today.AddDays(-3),
            accountSeries: 1,
            machineCount: 4);

        var connectedMachines = (await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId))
            .GetProperty("connectedMachines");

        Assert.Equal("Stale", connectedMachines.GetProperty("state").GetString());
        Assert.Equal(4, connectedMachines.GetProperty("count").GetInt32());
        Assert.Equal(3, connectedMachines.GetProperty("ageInDays").GetInt32());
        Assert.Equal(Today.AddDays(-3), connectedMachines.GetProperty("asOfDateUtc").GetDateTime());
    }

    /// <summary>
    /// Disconnected credentials report "count unavailable" with their reason, and a null count rather
    /// than a zero: a fleet nobody could read is not a fleet with no machines in it.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_connection_reports_the_count_unavailable()
    {
        await EnforcedProAsync();

        var connectedMachines = (await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId))
            .GetProperty("connectedMachines");

        Assert.Equal("Unavailable", connectedMachines.GetProperty("state").GetString());
        Assert.Equal("NotConnected", connectedMachines.GetProperty("unavailableReason").GetString());
        Assert.Equal(JsonValueKind.Null, connectedMachines.GetProperty("count").ValueKind);
        Assert.Equal(JsonValueKind.Null, connectedMachines.GetProperty("asOfDateUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, connectedMachines.GetProperty("machinesOverAllowance").ValueKind);
        Assert.Equal(5, connectedMachines.GetProperty("allowance").GetInt32());
    }

    /// <summary>
    /// Credentials that need attention make no call and report the count unavailable too, with the
    /// reason that tells them apart from a business that was never connected.
    /// </summary>
    [Fact]
    public async Task Credentials_that_need_attention_report_the_count_unavailable()
    {
        await EnforcedProAsync();
        await _factory.ConnectAsync(
            _factory.TradingBusinessId,
            accountSeries: 1,
            status: NayaxConnectionStatus.NeedsAttention);
        await _factory.SnapshotAsync(_factory.TradingBusinessId, Today, accountSeries: 1, machineCount: 7);

        var connectedMachines = (await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId))
            .GetProperty("connectedMachines");

        Assert.Equal("Unavailable", connectedMachines.GetProperty("state").GetString());
        Assert.Equal(
            "ConnectionNeedsAttention",
            connectedMachines.GetProperty("unavailableReason").GetString());
        Assert.Equal(JsonValueKind.Null, connectedMachines.GetProperty("count").ValueKind);
    }

    /// <summary>
    /// A snapshot of an account the business has left is not this account's count: the previous
    /// series' rows stay exactly as they are and the new account is reported as not counted yet.
    /// </summary>
    [Fact]
    public async Task A_snapshot_of_a_previous_account_is_not_this_accounts_count()
    {
        await EnforcedProAsync();
        await _factory.ConnectAsync(_factory.TradingBusinessId, accountSeries: 2);
        await _factory.SnapshotAsync(_factory.TradingBusinessId, Today, accountSeries: 1, machineCount: 9);

        var connectedMachines = (await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId))
            .GetProperty("connectedMachines");

        Assert.Equal("Unavailable", connectedMachines.GetProperty("state").GetString());
        Assert.Equal(
            "AccountNotYetCounted",
            connectedMachines.GetProperty("unavailableReason").GetString());
        Assert.Equal(JsonValueKind.Null, connectedMachines.GetProperty("count").ValueKind);
    }

    /// <summary>
    /// And one business's count is never another's: the caller's own resolved business is the only one
    /// this answer can come from, because no request carries a business id.
    /// </summary>
    [Fact]
    public async Task Another_businesss_count_is_never_reported()
    {
        await EnforcedProAsync();
        await _factory.ConnectAsync(_factory.TradingBusinessId, accountSeries: 1);
        await _factory.ConnectAsync(_factory.OtherBusinessId, accountSeries: 1);
        await _factory.SnapshotAsync(_factory.OtherBusinessId, Today, accountSeries: 1, machineCount: 9);

        var connectedMachines = (await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId))
            .GetProperty("connectedMachines");

        Assert.Equal("Unavailable", connectedMachines.GetProperty("state").GetString());
        Assert.Equal("NeverCounted", connectedMachines.GetProperty("unavailableReason").GetString());
    }

    /// <summary>
    /// The UTC day this host's own clock is on. The host answers against the real clock, so a count
    /// "taken today" has to be taken on today's UTC day rather than on a pinned one.
    /// </summary>
    private static DateTime Today => DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

    /// <summary>
    /// A deployment that is enforcing plans, with the trading business on a grandfathered Pro grant -
    /// the state every connected-machine answer is reported in.
    /// </summary>
    private async Task EnforcedProAsync()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.GrantAsync(
            _factory.TradingBusinessId,
            SubscriptionPlan.Pro,
            ManualPlanGrantSource.Grandfathered,
            Cutoff,
            endsAtUtc: null);
    }

    private Task<JsonElement> ReadAsync(string objectId) =>
        ReadEntitlementAsync(_factory.Authenticated(objectId));

    private static Task<JsonElement> ReadAsync(HttpClient client) => ReadEntitlementAsync(client);

    private static async Task<JsonElement> ReadEntitlementAsync(HttpClient client) =>
        (await ReadBodyAsync(client)).GetProperty("entitlement");

    private static async Task<JsonElement> ReadBodyAsync(HttpClient client)
    {
        var response = await client.GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>
    /// The Super Admin fixture's world, plus plan enforcement switched on, the administrator's own
    /// business, and a second business with an Owner of its own.
    /// </summary>
    public sealed class ApiFactory : SuperAdminBusinessesApiFactory
    {
        public const string OtherBusinessOwnerObjectId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        public const string SessionId = "the-support-session-id-this-file-presents";

        /// <summary>The business the administrator owns in their own right.</summary>
        public int AdminBusinessId { get; private set; }

        /// <summary>A second trading business, so no answer about a plan can be the only one possible.</summary>
        public int OtherBusinessId { get; private set; }

        /// <summary>A client previewing <paramref name="role"/> as the administrator.</summary>
        public HttpClient Previewing(string role)
        {
            var client = Authenticated(AdministratorObjectId);
            client.DefaultRequestHeaders.TryAddWithoutValidation(RolePreviewHeader.Name, role);
            return client;
        }

        /// <summary>
        /// A client inside a freshly started, usable support session for the <em>trading</em>
        /// business - somebody else's.
        /// </summary>
        public async Task<HttpClient> InSupportSession()
        {
            await SeedSupportSessionAsync(TradingBusinessId, AdministratorObjectId, SessionId);
            var client = Authenticated(AdministratorObjectId);
            client.DefaultRequestHeaders.Add(SupportSessionHeader.Name, SessionId);
            return client;
        }

        /// <summary>The grandfathering record with its cutoff fixed and no completion marker.</summary>
        public Task StartGrandfatheringAsync(DateTime cutoffUtc) =>
            WriteGrandfatheringAsync(cutoffUtc, completedAtUtc: null);

        /// <summary>The grandfathering record completed, which is what lets enforcement take effect.</summary>
        public Task CompleteGrandfatheringAsync(DateTime cutoffUtc) =>
            WriteGrandfatheringAsync(cutoffUtc, cutoffUtc.AddMinutes(5));

        public async Task<int> GrantAsync(
            int businessId,
            SubscriptionPlan plan,
            ManualPlanGrantSource source,
            DateTime startsAtUtc,
            DateTime? endsAtUtc)
        {
            await using var db = Database();
            var grant = new BusinessPlanGrant
            {
                BusinessId = businessId,
                Plan = plan,
                Source = source,
                StartsAtUtc = startsAtUtc,
                EndsAtUtc = endsAtUtc,
                GrantedAtUtc = startsAtUtc,
            };
            db.BusinessPlanGrants.Add(grant);
            await db.SaveChangesAsync();
            return grant.Id;
        }

        /// <summary>
        /// One business's stored Nayax connection, on a named account series (issue #978). The token
        /// is an obvious fake: nothing here reaches Nayax, and the connected-machine count is read
        /// from recorded snapshots rather than from a call.
        /// </summary>
        public async Task ConnectAsync(
            int businessId,
            int accountSeries,
            NayaxConnectionStatus status = NayaxConnectionStatus.Ready)
        {
            await using var db = Database();
            db.BusinessNayaxConnections.Add(new BusinessNayaxConnection
            {
                BusinessId = businessId,
                OperatorId = "2002736764",
                AccessTokenCiphertext = "not-a-real-ciphertext",
                EncryptionKeyId = "test-key",
                Status = status,
                CredentialRevision = 1,
                AccountSeries = accountSeries,
                AccountSeriesChangedAtUtc = Cutoff,
                UpdatedAtUtc = Cutoff,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// One recorded connected-machine snapshot, as a complete fleet read would have left it
        /// (issue #978).
        /// </summary>
        public async Task SnapshotAsync(
            int businessId,
            DateTime dayUtc,
            int accountSeries,
            int machineCount)
        {
            await using var db = Database();
            db.BusinessConnectedMachineSnapshots.Add(new BusinessConnectedMachineSnapshot
            {
                BusinessId = businessId,
                SnapshotDateUtc = dayUtc,
                AccountSeries = accountSeries,
                MachineCount = machineCount,
                ProviderMachineIds = ConnectedMachineFleetRead.FormatIds(
                    Enumerable.Range(1, machineCount).Select(id => (long)id)),
                TakenAtUtc = dayUtc.AddHours(6),
                ReadWasComplete = true,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>Ends a grant outside the boundary, the way an owner setting an end date would.</summary>
        public async Task EndGrantAsync(int grantId, DateTime endsAtUtc)
        {
            await using var db = Database();
            var grant = await db.BusinessPlanGrants.SingleAsync(candidate => candidate.Id == grantId);
            grant.EndsAtUtc = endsAtUtc;
            await db.SaveChangesAsync();
        }

        public async Task SubscribeAsync(
            int businessId,
            SubscriptionPlan plan,
            BillingSubscriptionStatus status,
            DateTime startsAtUtc,
            DateTime? endsAtUtc)
        {
            await using var db = Database();
            db.BusinessBillingSubscriptions.Add(new BusinessBillingSubscription
            {
                BusinessId = businessId,
                Plan = plan,
                Status = status,
                CurrentPeriodStartUtc = startsAtUtc,
                CurrentPeriodEndUtc = endsAtUtc,
                UpdatedAtUtc = startsAtUtc,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Puts the world back, and adds this file's own fixtures: the administrator's own business,
        /// a second trading business with an Owner, and no subscription state at all.
        /// </summary>
        public new async Task ResetAsync()
        {
            await ClearSubscriptionStateAsync();

            await base.ResetAsync();

            AdminBusinessId = await SeedBusinessAsync("Administrator's Own Vending", BusinessStatus.Active);
            await SeedMembershipAsync(AdminBusinessId, AdministratorObjectId, BusinessRole.Owner, isActive: true);

            OtherBusinessId = await SeedBusinessAsync("Second Vending Co", BusinessStatus.Active);
            await SeedMembershipAsync(
                OtherBusinessId, OtherBusinessOwnerObjectId, BusinessRole.Owner, isActive: true);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            // The one thing this host does differently: plan enforcement is switched on, which is
            // the only state in which a plan is reported at all. Every other deployment - and every
            // other test host - leaves it off, which is the shipped state.
            builder.UseSetting(ConfiguredSubscriptionEnforcementFlag.EnabledKey, "true");

            base.ConfigureWebHost(builder);
        }

        /// <summary>
        /// Removes the grants, the subscriptions and the grandfathering record, so each test starts
        /// from "nothing has ever been granted, paid for or grandfathered" rather than from whatever
        /// its predecessor decided.
        /// </summary>
        private async Task ClearSubscriptionStateAsync()
        {
            await using var db = Database();
            db.BusinessPlanGrants.RemoveRange(await db.BusinessPlanGrants.ToListAsync());
            db.BusinessBillingSubscriptions.RemoveRange(await db.BusinessBillingSubscriptions.ToListAsync());
            db.SubscriptionGrandfatherings.RemoveRange(await db.SubscriptionGrandfatherings.ToListAsync());
            db.BusinessNayaxConnections.RemoveRange(await db.BusinessNayaxConnections.ToListAsync());
            await db.SaveChangesAsync();

            // The snapshots are append-only, so the enforcer refuses to delete them through the
            // context (issue #978). A fixture putting the world back is outside the application's
            // write paths altogether, so it removes the rows with a statement rather than by pretending
            // an application could.
            await db.Database.ExecuteSqlRawAsync(@"DELETE FROM ""BusinessConnectedMachineSnapshots"";");
        }

        private async Task WriteGrandfatheringAsync(DateTime cutoffUtc, DateTime? completedAtUtc)
        {
            await using var db = Database();
            db.SubscriptionGrandfatherings.RemoveRange(await db.SubscriptionGrandfatherings.ToListAsync());
            await db.SaveChangesAsync();

            db.SubscriptionGrandfatherings.Add(new SubscriptionGrandfathering
            {
                Id = SubscriptionGrandfathering.SingletonId,
                CutoffUtc = cutoffUtc,
                CompletedAtUtc = completedAtUtc,
                GrantedBusinessCount = completedAtUtc is null ? 0 : 1,
            });
            await db.SaveChangesAsync();
        }
    }
}
