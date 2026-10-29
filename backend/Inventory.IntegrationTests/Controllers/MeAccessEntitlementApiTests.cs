using System.Net;
using System.Text.Json;
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
    /// The attachment storage figures of issue #968, over HTTP: this business's own usage, its
    /// plan's allowance read from the catalogue, and the warning state those two produce. The other
    /// business's attachments are invisible here, which is the same isolation the plan itself has.
    /// </summary>
    [Fact]
    public async Task The_attachment_storage_reported_is_this_businesss_own_usage_and_its_plans_allowance()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.SeedExpenseAttachmentAsync(_factory.TradingBusinessId, 400);
        await _factory.SeedExpenseAttachmentAsync(_factory.OtherBusinessId, 900_000_000);

        var storage = (await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId))
            .GetProperty("attachmentStorage");

        Assert.Equal(400, storage.GetProperty("usedBytes").GetInt64());
        Assert.Equal(
            PlanCatalogue.For(SubscriptionPlan.Free).AttachmentStorageLimitBytes,
            storage.GetProperty("limitBytes").GetInt64());
        Assert.Equal("Normal", storage.GetProperty("warningLevel").GetString());

        var other = (await ReadAsync(ApiFactory.OtherBusinessOwnerObjectId))
            .GetProperty("attachmentStorage");
        Assert.Equal(900_000_000, other.GetProperty("usedBytes").GetInt64());
        Assert.Equal("Warning", other.GetProperty("warningLevel").GetString());
    }

    /// <summary>
    /// The three states the warning has, over HTTP, including the one a downgrade produces: a
    /// business above its allowance is reported as full with the usage it actually has, and nothing
    /// about its attachments has been deleted to make the figure fit.
    /// </summary>
    [Theory]
    [InlineData(500_000_000L, "Normal")]
    [InlineData(800_000_000L, "Warning")]
    [InlineData(1_000_000_000L, "Full")]
    [InlineData(1_500_000_000L, "Full")]
    public async Task The_warning_state_turns_at_eighty_percent_and_again_at_the_allowance(
        long usedBytes, string expected)
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.SeedExpenseAttachmentAsync(_factory.TradingBusinessId, usedBytes);

        var storage = (await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId))
            .GetProperty("attachmentStorage");

        Assert.Equal(usedBytes, storage.GetProperty("usedBytes").GetInt64());
        Assert.Equal(expected, storage.GetProperty("warningLevel").GetString());
    }

    /// <summary>
    /// A business on a paid plan is measured against that plan's larger allowance, read from the
    /// same catalogue the price page reads.
    /// </summary>
    [Fact]
    public async Task A_paid_plan_reports_its_own_larger_allowance()
    {
        await _factory.CompleteGrandfatheringAsync(Cutoff);
        await _factory.SubscribeAsync(
            _factory.TradingBusinessId,
            SubscriptionPlan.Pro,
            BillingSubscriptionStatus.Active,
            Cutoff,
            Cutoff.AddYears(5));
        await _factory.SeedExpenseAttachmentAsync(_factory.TradingBusinessId, 1_500_000_000);

        var storage = (await ReadAsync(SuperAdminBusinessesApiFactory.TradingOwnerObjectId))
            .GetProperty("attachmentStorage");

        Assert.Equal(
            PlanCatalogue.For(SubscriptionPlan.Pro).AttachmentStorageLimitBytes,
            storage.GetProperty("limitBytes").GetInt64());
        Assert.Equal("Normal", storage.GetProperty("warningLevel").GetString());
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
            // And the attachments the storage figures of issue #968 are summed from, so a usage
            // assertion is about what its own test stored.
            db.OperatingExpenses.RemoveRange(await db.OperatingExpenses.ToListAsync());
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Records an expense with an attachment of <paramref name="sizeBytes"/> for one business
        /// (issue #968), so the storage figures the access read reports have something to be about.
        /// No file is written: usage is the <em>recorded</em> sizes of the attachments a business's
        /// records refer to, which is exactly what makes a quota readable without touching storage.
        /// </summary>
        public async Task SeedExpenseAttachmentAsync(int businessId, long sizeBytes)
        {
            await using var db = Database();
            db.OperatingExpenses.Add(new OperatingExpense
            {
                BusinessId = businessId,
                ExpenseDate = Cutoff,
                Category = OperatingExpenseCategory.Insurance,
                Description = "Cover",
                AmountExGst = 10m,
                GstAmount = 1m,
                TotalAmount = 11m,
                AttachmentFileName = "invoice.pdf",
                AttachmentStoredFileName = $"{Guid.NewGuid()}.pdf",
                AttachmentContentType = "application/pdf",
                AttachmentFileSizeBytes = sizeBytes,
            });
            await db.SaveChangesAsync();
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
