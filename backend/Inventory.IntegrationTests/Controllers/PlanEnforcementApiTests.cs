using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Adapters.Subscriptions;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Plan enforcement over real HTTP (issue #967), against a deployment that has switched the rollout
/// setting on - the only state in which a plan withholds anything.
///
/// <para>Everything goes through the complete pipeline: authentication, the required scope, the
/// capability policies, <c>BusinessScopeMiddleware</c>, the plan filters, the controllers, the
/// <strong>real</strong> <c>NayaxLynxClient</c> with its real credential provider, and relational
/// SQLite with the real migration history. Two things only are substituted: the Nayax HTTP transport,
/// by a handler that <em>counts every request and answers nothing useful</em>, and the token
/// protector, by the real AES-GCM one over a test key.</para>
///
/// <para><strong>The counting transport is what makes the central criterion assertable.</strong> The
/// business seeded here has valid, <c>Ready</c> Nayax credentials stored throughout, so a downgraded
/// business is not a business that forgot to connect: every "no Nayax request was made" assertion
/// below is a count of zero on a connection that would otherwise have worked, and the Pro cases show
/// the same transport really is reached when the plan allows it.</para>
/// </summary>
public class PlanEnforcementApiTests : IClassFixture<PlanEnforcementApiTests.ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;

    public PlanEnforcementApiTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region Stored-data endpoints: the report, commission and import requirements

    /// <summary>
    /// The stored-data criterion, endpoint by endpoint and plan by plan: Bookkeeping and GST need
    /// Starter, every other report needs Pro, and the plan a refusal names is the plan the business
    /// would have to be on.
    /// </summary>
    [Theory]
    [InlineData("/api/reports/bookkeeping", SubscriptionPlan.Free, false)]
    [InlineData("/api/reports/bookkeeping", SubscriptionPlan.Starter, true)]
    [InlineData("/api/reports/bookkeeping", SubscriptionPlan.Pro, true)]
    [InlineData("/api/reports/gst", SubscriptionPlan.Free, false)]
    [InlineData("/api/reports/gst", SubscriptionPlan.Starter, true)]
    [InlineData("/api/reports/gst", SubscriptionPlan.Pro, true)]
    [InlineData("/api/reports/daily", SubscriptionPlan.Free, false)]
    [InlineData("/api/reports/daily", SubscriptionPlan.Starter, false)]
    [InlineData("/api/reports/daily", SubscriptionPlan.Pro, true)]
    [InlineData("/api/reports/transactions", SubscriptionPlan.Starter, false)]
    [InlineData("/api/reports/transactions", SubscriptionPlan.Pro, true)]
    [InlineData("/api/reports/reconciliation", SubscriptionPlan.Starter, false)]
    [InlineData("/api/reports/reconciliation", SubscriptionPlan.Pro, true)]
    [InlineData("/api/reports/machine-profitability", SubscriptionPlan.Starter, false)]
    [InlineData("/api/reports/machine-profitability", SubscriptionPlan.Pro, true)]
    [InlineData("/api/reports/product-profitability", SubscriptionPlan.Starter, false)]
    [InlineData("/api/reports/product-profitability", SubscriptionPlan.Pro, true)]
    [InlineData("/api/reports/dashboard", SubscriptionPlan.Starter, false)]
    [InlineData("/api/reports/dashboard", SubscriptionPlan.Pro, true)]
    public async Task Each_report_is_served_or_refused_by_the_business_plan(
        string uri,
        SubscriptionPlan plan,
        bool served)
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, plan);

        var response = await Owner().GetAsync(uri);

        if (served)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return;
        }

        await AssertPlanRefusalAsync(response);
    }

    /// <summary>
    /// The refusal names the plan needed, and names the one the <em>feature</em> needs rather than
    /// a single plan for every refusal: Bookkeeping asks for Starter and the daily report for Pro.
    /// </summary>
    [Theory]
    [InlineData("/api/reports/bookkeeping", "Starter", "BookkeepingAndGstReports")]
    [InlineData("/api/reports/gst", "Starter", "BookkeepingAndGstReports")]
    [InlineData("/api/reports/daily", "Pro", "ProviderSalesReports")]
    [InlineData("/api/site-commissions", "Pro", "SiteCommissions")]
    [InlineData("/api/imports/nayax-sales", "Pro", "SalesFileImport")]
    public async Task A_refusal_names_the_plan_and_the_feature(
        string uri,
        string requiredPlan,
        string feature)
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);

        var response = uri.EndsWith("nayax-sales", StringComparison.Ordinal)
            ? await Owner().PostAsync(uri, SpreadsheetUpload())
            : await Owner().GetAsync(uri);
        var problem = await AssertPlanRefusalAsync(response);

        Assert.Equal(requiredPlan, problem.GetProperty("requiredPlan").GetString());
        Assert.Equal(feature, problem.GetProperty("planFeature").GetString());
        // A refusal is about the business's plan and says nothing about the caller: no role, no
        // capability and no business id.
        var body = problem.ToString();
        Assert.DoesNotContain("Owner", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Reports.View", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A download is the same figures in another container, so an export needs exactly the plan its
    /// own report needs - and no more. The Starter business downloads Bookkeeping and is refused
    /// the daily export.
    /// </summary>
    [Theory]
    [InlineData("/api/reports/bookkeeping/export", true)]
    [InlineData("/api/reports/gst/export", true)]
    [InlineData("/api/reports/daily/export", false)]
    [InlineData("/api/reports/transactions/export", false)]
    [InlineData("/api/reports/product-profitability/export", false)]
    public async Task A_starter_business_exports_its_own_reports_and_no_others(string uri, bool served)
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Starter);

        var response = await Owner().GetAsync(uri);

        if (served)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return;
        }

        var problem = await AssertPlanRefusalAsync(response);
        Assert.Equal("Pro", problem.GetProperty("requiredPlan").GetString());
    }

    /// <summary>
    /// The export's fail-closed direction: a report name nothing recognises falls through to
    /// product profitability in the row builder, so it is refused as the Pro feature rather than
    /// served as a Starter one. Misspelling a URL is not a way to download a Pro report.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_export_name_is_refused_as_the_pro_feature()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Starter);

        var problem = await AssertPlanRefusalAsync(await Owner().GetAsync("/api/reports/not-a-report/export"));

        Assert.Equal("Pro", problem.GetProperty("requiredPlan").GetString());
    }

    /// <summary>
    /// Site Commissions is Pro throughout - the report, the agreements and the payments - and
    /// nothing it has stored is removed by the refusal: the agreement seeded here is still there
    /// afterwards and is readable again on Pro.
    /// </summary>
    [Theory]
    [InlineData("/api/site-commissions")]
    [InlineData("/api/site-commissions/agreements")]
    public async Task Site_commissions_needs_pro_and_keeps_its_stored_agreements(string uri)
    {
        await _factory.SeedCommissionAgreementAsync();
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Starter);

        await AssertPlanRefusalAsync(await Owner().GetAsync(uri));
        Assert.Equal(1, await _factory.CommissionAgreementCountAsync());

        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);

        Assert.Equal(HttpStatusCode.OK, (await Owner().GetAsync(uri)).StatusCode);
        Assert.Equal(1, await _factory.CommissionAgreementCountAsync());
    }

    /// <summary>
    /// The commission <em>writes</em> are refused on the same plan as the read, before anything is
    /// stored.
    /// </summary>
    [Fact]
    public async Task The_commission_writes_are_refused_on_a_plan_without_pro()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Starter);

        await AssertPlanRefusalAsync(
            await Owner().PostAsync("/api/site-commissions/agreements", Json("{}")));
        await AssertPlanRefusalAsync(
            await Owner().PostAsync("/api/site-commissions/1/payments", Json("{}")));

        Assert.Equal(0, await _factory.CommissionAgreementCountAsync());
    }

    /// <summary>
    /// The supplier price data of issue #977: the one endpoint that serves it is Starter, and a Free
    /// business is refused it. The ids are this business's own, so a served answer really is the
    /// summary rather than a validation failure.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free, false)]
    [InlineData(SubscriptionPlan.Starter, true)]
    [InlineData(SubscriptionPlan.Pro, true)]
    public async Task The_cheapest_supplier_summary_is_served_or_refused_by_the_business_plan(
        SubscriptionPlan plan,
        bool served)
    {
        var productId = await _factory.SeedTrackableProductAsync();
        await _factory.GrantAsync(_factory.TradingBusinessId, plan);

        var response = await Owner().PostAsync(
            "/api/cheapest-suppliers/summary",
            Json($"{{\"productIds\":[{productId}]}}"));

        if (!served)
        {
            var problem = await AssertPlanRefusalAsync(response);
            Assert.Equal("Starter", problem.GetProperty("requiredPlan").GetString());
            Assert.Equal(
                PlanFeatures.Name(PlanFeature.ObservedSupplierPrices),
                problem.GetProperty("planFeature").GetString());
            return;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await ReadJsonAsync(response);
        Assert.Equal(1, summary.GetProperty("products").GetArrayLength());
    }

    /// <summary>
    /// The issue's fourth criterion: a Free business still switches "Track supplier prices" on and
    /// off, and reads where its own products stand. The mapping is its own record rather than the
    /// price data, so the plan withholds none of the three endpoints - and the request that turns it
    /// on really does store the mapping, which is what makes "the setting is kept and takes effect
    /// when the business upgrades" possible at all.
    /// </summary>
    [Fact]
    public async Task A_free_business_still_switches_price_tracking_on_and_off()
    {
        var productId = await _factory.SeedTrackableProductAsync();
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Owner().PutAsync(
                $"/api/products/{productId}/price-tracking",
                Json("{\"tracked\":true}"))).StatusCode);
        Assert.Equal(1, await _factory.PriceTrackingCountAsync());

        var read = await Owner().GetAsync($"/api/products/{productId}/price-tracking");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True((await ReadJsonAsync(read)).GetProperty("tracked").GetBoolean());
        Assert.Equal(
            HttpStatusCode.OK,
            (await Owner().GetAsync("/api/products/price-tracking")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Owner().PutAsync(
                $"/api/products/{productId}/price-tracking",
                Json("{\"tracked\":false}"))).StatusCode);
        Assert.Equal(0, await _factory.PriceTrackingCountAsync());
    }

    /// <summary>
    /// And a downgrade never clears the setting: the mapping a Starter business asked for is still
    /// there after the business is refused the prices, and the prices come back with the plan.
    /// </summary>
    [Fact]
    public async Task A_downgrade_keeps_the_tracking_setting_and_an_upgrade_restores_the_prices()
    {
        var productId = await _factory.SeedTrackableProductAsync();
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Starter);
        await Owner().PutAsync(
            $"/api/products/{productId}/price-tracking",
            Json("{\"tracked\":true}"));

        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);
        await AssertPlanRefusalAsync(await Owner().PostAsync(
            "/api/cheapest-suppliers/summary",
            Json($"{{\"productIds\":[{productId}]}}")));

        Assert.Equal(1, await _factory.PriceTrackingCountAsync());
        var read = await ReadJsonAsync(
            await Owner().GetAsync($"/api/products/{productId}/price-tracking"));
        Assert.True(read.GetProperty("tracked").GetBoolean());

        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Starter);

        Assert.Equal(
            HttpStatusCode.OK,
            (await Owner().PostAsync(
                "/api/cheapest-suppliers/summary",
                Json($"{{\"productIds\":[{productId}]}}"))).StatusCode);
        Assert.Equal(1, await _factory.PriceTrackingCountAsync());
    }

    /// <summary>
    /// The Nayax sales file import is Pro, and a refused upload imports nothing: the plan is
    /// evaluated before the multipart body is read at all.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free)]
    [InlineData(SubscriptionPlan.Starter)]
    public async Task The_nayax_sales_file_import_needs_pro_and_a_refused_upload_imports_nothing(
        SubscriptionPlan plan)
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, plan);

        await AssertPlanRefusalAsync(
            await Owner().PostAsync("/api/imports/nayax-sales", SpreadsheetUpload()));

        Assert.Equal(0, await _factory.StoredSaleCountAsync());
        Assert.Equal(0, await _factory.ImportedFileCountAsync());
    }

    /// <summary>
    /// The settlement XML import beside it is not the Nayax sales file and carries no plan
    /// requirement: its figures feed the two reports a Starter business includes.
    /// </summary>
    [Fact]
    public async Task The_reimbursement_xml_import_is_included_in_every_plan()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);

        var response = await Owner().PostAsync("/api/imports/pending-xml", Json("{}"));

        await AssertNotAPlanRefusalAsync(response);
    }

    #endregion

    #region A role refusal stays as it is, and a plan never grants a role more access

    /// <summary>
    /// The ordering criterion: a plan is evaluated <em>after</em> the role requirement, so an
    /// Operator - who holds no <c>Reports.View</c> - is refused as the role refusal they have
    /// always been, with the bare <c>403</c> that tells them nothing about the role matrix. That
    /// holds on Pro, where the plan would have allowed it, and on Free, where both would refuse.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free)]
    [InlineData(SubscriptionPlan.Pro)]
    public async Task A_role_refusal_stays_a_bare_forbidden_on_every_plan(SubscriptionPlan plan)
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, plan);

        var response = await _factory.AsOperator().GetAsync("/api/reports/bookkeeping");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// And the other direction: a plan does not grant a role anything. A Manager holds
    /// <c>Reports.View</c> but not <c>Integration.Manage</c>, so the Pro business's Manager is still
    /// refused the credential actions - by their role, with no plan refusal in sight.
    /// </summary>
    [Fact]
    public async Task A_plan_never_grants_a_role_more_access()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);

        var response = await _factory.AsManager()
            .PostAsync("/api/settings/nayax-connection/retest", Json("{}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _factory.NayaxRequests);
    }

    #endregion

    #region The credential actions, gated explicitly

    /// <summary>
    /// The credential actions are the Nayax calls the ordinary credential provider never sees, so
    /// each is refused at its own endpoint: no Nayax request is made, and the stored credentials are
    /// byte-for-byte what they were.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free, "test")]
    [InlineData(SubscriptionPlan.Free, "save")]
    [InlineData(SubscriptionPlan.Free, "retest")]
    [InlineData(SubscriptionPlan.Starter, "test")]
    [InlineData(SubscriptionPlan.Starter, "save")]
    [InlineData(SubscriptionPlan.Starter, "retest")]
    public async Task A_credential_action_is_refused_without_pro_and_changes_nothing(
        SubscriptionPlan plan,
        string action)
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, plan);
        var before = await _factory.StoredConnectionAsync();

        var response = await Owner().PostAsync(
            $"/api/settings/nayax-connection/{action}",
            Json($$"""{"operatorId":"2002736764","token":"{{ApiFactory.AccessToken}}","draftId":"any"}"""));

        await AssertPlanRefusalAsync(response);
        Assert.Equal(0, _factory.NayaxRequests);
        Assert.Equal(before, await _factory.StoredConnectionAsync());
        Assert.Equal(0, await _factory.TestDraftCountAsync());
    }

    /// <summary>
    /// On Pro the same action is not refused by the plan and does reach Nayax, which is what makes
    /// the zero counts above a measurement rather than a transport that was never wired up.
    /// </summary>
    [Fact]
    public async Task On_pro_a_credential_action_is_not_refused_and_does_reach_nayax()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);

        var response = await Owner().PostAsync(
            "/api/settings/nayax-connection/test",
            Json($$"""{"operatorId":"2002736764","token":"{{ApiFactory.AccessToken}}"}"""));

        await AssertNotAPlanRefusalAsync(response);
        Assert.True(_factory.NayaxRequests > 0, "The connection test made no Nayax request.");
    }

    /// <summary>
    /// The status read stays available on every plan, to every role that sees it today, and makes no
    /// Nayax call. It is what tells a downgraded business that its saved credentials and their status
    /// are still there - which the response proves by still reporting <c>Ready</c>.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free)]
    [InlineData(SubscriptionPlan.Starter)]
    [InlineData(SubscriptionPlan.Pro)]
    public async Task The_connection_status_is_readable_on_every_plan_by_every_role(SubscriptionPlan plan)
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, plan);

        foreach (var client in new[] { Owner(), _factory.AsManager(), _factory.AsOperator() })
        {
            var response = await client.GetAsync("/api/settings/nayax-connection/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadJsonAsync(response);
            Assert.Equal(
                nameof(NayaxConnectionStatus.Ready),
                body.GetProperty("status").GetString());
        }

        Assert.Equal(0, _factory.NayaxRequests);
    }

    #endregion

    #region The mixed surfaces: today's no-provider behaviour, under the plan reason

    /// <summary>
    /// The central criterion. A downgraded business with valid saved credentials gets the dashboard
    /// it already gets without a usable connection - the local sections, and the fleet card withheld
    /// - and the reason is the distinct plan one rather than "connect Nayax".
    /// </summary>
    [Fact]
    public async Task The_dashboard_withholds_the_fleet_card_with_the_plan_reason_and_makes_no_nayax_request()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);

        var response = await Owner().GetAsync("/api/dashboard/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await ReadJsonAsync(response);
        var needsRefill = summary.GetProperty("needsRefill");
        Assert.False(needsRefill.GetProperty("available").GetBoolean());
        Assert.Equal(
            MachineNeedUnavailableReasons.PlanRequired,
            needsRefill.GetProperty("reason").GetString());
        // The local sections are untouched, which is "Free keeps everything else unchanged".
        Assert.Equal("StorageOnly", summary.GetProperty("needsOrdering").GetProperty("mode").GetString());
        Assert.True(summary.TryGetProperty("inventory", out _));
        Assert.Equal(0, _factory.NayaxRequests);
    }

    /// <summary>
    /// The same page on Pro reads the fleet, so the counting transport really is the one the client
    /// uses and the zero above is a measurement.
    /// </summary>
    [Fact]
    public async Task The_dashboard_on_pro_reads_the_fleet()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);

        Assert.Equal(HttpStatusCode.OK, (await Owner().GetAsync("/api/dashboard/summary")).StatusCode);
        Assert.True(_factory.NayaxRequests > 0, "The dashboard made no Nayax request on Pro.");
    }

    /// <summary>
    /// The reorder listing is storage-only with the plan reason, published through the same response
    /// headers it already publishes an unusable connection through.
    /// </summary>
    [Fact]
    public async Task The_reorder_listing_is_storage_only_with_the_plan_reason()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);

        var response = await Owner().GetAsync("/api/products/alerts/low-stock");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ReorderModes.StorageOnly,
            Assert.Single(response.Headers.GetValues(ReorderListingMetadataHeaders.Mode)));
        Assert.Equal(
            MachineNeedUnavailableReasons.PlanRequired,
            Assert.Single(response.Headers.GetValues(
                ReorderListingMetadataHeaders.MachineNeedUnavailableReason)));
        Assert.Equal(0, _factory.NayaxRequests);
    }

    /// <summary>
    /// The surfaces that answer the stable "Nayax is not usable" refusal keep answering exactly
    /// that - the same <c>409</c> and the same code, so a client that has not learnt about plans
    /// degrades as it does today - and carry the distinct plan reason and the plan beside it.
    /// </summary>
    [Theory]
    [InlineData("/api/pick-list?machineIds=55")]
    [InlineData("/api/Machines")]
    [InlineData("/api/Sites")]
    [InlineData("/api/nayax-sales-sync")]
    public async Task A_provider_backed_surface_still_answers_its_own_refusal_with_the_plan_reason(string uri)
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);

        var response = uri.EndsWith("sales-sync", StringComparison.Ordinal)
            ? await Owner().PostAsync(uri, Json("{}"))
            : await Owner().GetAsync(uri);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal(NayaxNotConnectedException.ErrorCode, problem.GetProperty("code").GetString());
        Assert.Equal(
            NayaxNotConnectedException.PlanRequiredReason,
            problem.GetProperty("nayaxUnavailableReason").GetString());
        Assert.Equal("Pro", problem.GetProperty("requiredPlan").GetString());
        // A plan refusal reads no connection, so there is no status beside it to misread as "this
        // business has nothing stored" - and it really does have something stored.
        Assert.False(problem.TryGetProperty("nayaxConnectionStatus", out _));
        Assert.Equal(0, _factory.NayaxRequests);
    }

    /// <summary>
    /// A Pro business whose connection is unusable keeps today's connection reason, which is the
    /// other half of "the two refusals stay distinguishable": the status is reported and the plan is
    /// not.
    /// </summary>
    [Fact]
    public async Task A_pro_business_with_an_unusable_connection_keeps_the_connection_reason()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        await _factory.SetConnectionStatusAsync(NayaxConnectionStatus.NeedsAttention);

        var problem = await ReadJsonAsync(await Owner().GetAsync("/api/pick-list?machineIds=55"));

        Assert.Equal(NayaxNotConnectedException.ErrorCode, problem.GetProperty("code").GetString());
        Assert.Equal(
            NayaxNotConnectedException.ConnectionUnavailableReason,
            problem.GetProperty("nayaxUnavailableReason").GetString());
        Assert.Equal(
            nameof(NayaxConnectionStatus.NeedsAttention),
            problem.GetProperty("nayaxConnectionStatus").GetString());
        Assert.False(problem.TryGetProperty("requiredPlan", out _));
    }

    /// <summary>
    /// The Sync Restock reminder makes no Nayax call, so it asks the plan question itself: it is
    /// withheld with its own reason rather than pointing an operator at a sync the application will
    /// refuse.
    /// </summary>
    [Fact]
    public async Task The_restock_sync_reminder_is_withheld_with_the_plan_reason()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);

        var response = await Owner().GetAsync("/api/machine-restock-sync/reminder");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.False(body.GetProperty("applicable").GetBoolean());
        Assert.Equal("PlanRequired", body.GetProperty("reason").GetString());
        Assert.Equal(0, _factory.NayaxRequests);
    }

    /// <summary>
    /// And the manual paths a Free business runs its stock on are not refused: recording a pick, and
    /// recording one across several machines, get past the plan and fail only on their own
    /// validation. They carry no plan requirement at all, which is what "Free keeps everything else
    /// unchanged, including manual pick-list refills" means.
    /// </summary>
    [Theory]
    [InlineData("/api/pick-list/refills")]
    [InlineData("/api/pick-list/multi-machine-refills")]
    [InlineData("/api/products/1/stock")]
    [InlineData("/api/purchases")]
    public async Task A_manual_path_is_never_refused_by_a_plan(string uri)
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);

        await AssertNotAPlanRefusalAsync(await Owner().PostAsync(uri, Json("{}")));
    }

    #endregion

    #region Enforcement, grandfathering and isolation

    /// <summary>
    /// "Until enforcement is switched on, nothing changes for any business", from the other
    /// direction: with the rollout setting on but the grandfathering step not completed, every
    /// business is unrestricted whatever its records say.
    /// </summary>
    [Fact]
    public async Task With_the_grandfathering_step_unfinished_nothing_is_withheld()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);
        await _factory.UncompleteGrandfatheringAsync();

        Assert.Equal(HttpStatusCode.OK, (await Owner().GetAsync("/api/reports/daily")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Owner().GetAsync("/api/site-commissions")).StatusCode);
    }

    /// <summary>
    /// The grandfathered grant the rollout writes is Pro with no end date, so a business that was
    /// already trading when enforcement was switched on loses nothing.
    /// </summary>
    [Fact]
    public async Task A_grandfathered_business_keeps_everything()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);

        Assert.Equal(HttpStatusCode.OK, (await Owner().GetAsync("/api/reports/daily")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Owner().GetAsync("/api/reports/bookkeeping")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Owner().GetAsync("/api/site-commissions")).StatusCode);
    }

    /// <summary>
    /// Two businesses, each answered from its own plan. There is no business id anywhere in these
    /// requests, so an answer could only have come from the caller's own resolved business.
    /// </summary>
    [Fact]
    public async Task Each_business_is_answered_from_its_own_plan()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        await _factory.GrantAsync(_factory.OtherBusinessId, SubscriptionPlan.Free);

        Assert.Equal(HttpStatusCode.OK, (await Owner().GetAsync("/api/reports/daily")).StatusCode);
        await AssertPlanRefusalAsync(
            await _factory.AsOtherBusinessOwner().GetAsync("/api/reports/daily"));
    }

    /// <summary>
    /// A plan change deletes and alters nothing. The business's sale, its stored credentials and its
    /// commission agreement are all still exactly as they were after a downgrade and a round of
    /// refusals - and they come back with the plan.
    /// </summary>
    [Fact]
    public async Task A_plan_change_never_removes_stored_data()
    {
        await _factory.SeedCommissionAgreementAsync();
        await _factory.SeedCompletedSaleAsync();
        var connection = await _factory.StoredConnectionAsync();

        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);
        await AssertPlanRefusalAsync(await Owner().GetAsync("/api/reports/daily"));
        await AssertPlanRefusalAsync(await Owner().GetAsync("/api/site-commissions"));
        await AssertPlanRefusalAsync(
            await Owner().PostAsync("/api/settings/nayax-connection/retest", Json("{}")));

        Assert.Equal(1, await _factory.StoredSaleCountAsync());
        Assert.Equal(1, await _factory.CommissionAgreementCountAsync());
        Assert.Equal(connection, await _factory.StoredConnectionAsync());

        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        Assert.Equal(HttpStatusCode.OK, (await Owner().GetAsync("/api/reports/daily")).StatusCode);
    }

    #endregion

    #region Helpers

    private HttpClient Owner() => _factory.AsOwner();

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    /// <summary>
    /// A multipart upload that is shaped like the import's own contract. Its bytes are not a real
    /// workbook, deliberately: a refused request must never read them, and an allowed one failing on
    /// the format is a different answer from being refused.
    /// </summary>
    private static MultipartFormDataContent SpreadsheetUpload()
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("not-a-real-workbook"));
        file.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        return new MultipartFormDataContent { { file, "file", "sales.xlsx" } };
    }

    private static async Task<JsonElement> AssertPlanRefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await ReadJsonAsync(response);
        Assert.Equal(PlanRequirement.ErrorCode, problem.GetProperty("code").GetString());
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("message").GetString()));

        return problem;
    }

    /// <summary>
    /// The endpoint got past the plan. What it then answered is its own business - a validation
    /// failure for a body this test did not fill in is a different answer from a refusal, and
    /// asserting the first would be asserting somebody else's contract.
    /// </summary>
    private static async Task AssertNotAPlanRefusalAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PlanRequirement.ErrorCode, body, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    #endregion

    /// <summary>
    /// The Super Admin fixture's world, plus plan enforcement switched on, a second business with an
    /// Owner, an Operator in the trading business, stored Nayax credentials that really work, and a
    /// Nayax transport that counts every request.
    /// </summary>
    public class ApiFactory : SuperAdminBusinessesApiFactory
    {
        public const string OperatorObjectId = "77777777-7777-7777-7777-777777777777";
        public const string OtherOwnerObjectId = "88888888-8888-8888-8888-888888888888";

        /// <summary>Obviously fake. No real token belongs in a test.</summary>
        public const string AccessToken = "fake-token-not-a-real-credential";

        /// <summary>
        /// The same world with the rollout setting left off - the shipped state - so "until
        /// enforcement is switched on, nothing changes for any business" is asserted over a business
        /// whose records would otherwise put it on Free.
        /// </summary>
        public sealed class WithEnforcementOff : ApiFactory
        {
            protected override bool EnforcePlans => false;
        }

        private const string EncryptionKeyId = "2026-10";

        /// <summary>
        /// The recorded grandfathering cutoff these tests grant from, deliberately in the past: this
        /// host answers against the real clock, and a grant applies from its start instant inclusive.
        /// </summary>
        private static readonly DateTime Cutoff = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly NayaxTokenProtectionOptions _protection = Protection();
        private readonly NayaxRequestCounter _requests = new();

        /// <summary>A second trading business, so no answer about a plan can be the only one possible.</summary>
        public int OtherBusinessId { get; private set; }

        /// <summary>How many requests the Nayax transport has been asked to make since the reset.</summary>
        public int NayaxRequests => _requests.Count;

        /// <summary>
        /// Whether this host switches plan enforcement on. Every other test host in the repository
        /// leaves it off, which is the shipped state and what
        /// <see cref="WithEnforcementOff"/> asserts here.
        /// </summary>
        protected virtual bool EnforcePlans => true;

        public HttpClient AsOwner() => Authenticated(TradingOwnerObjectId);

        public HttpClient AsManager() => Authenticated(TradingManagerObjectId);

        public HttpClient AsOperator() => Authenticated(OperatorObjectId);

        public HttpClient AsOtherBusinessOwner() => Authenticated(OtherOwnerObjectId);

        /// <summary>
        /// Puts the world back: this file's own businesses and memberships, a stored connection in
        /// the one status that lets credentials resolve, a completed grandfathering record, no grant,
        /// no sale, no agreement and a request counter at zero.
        /// </summary>
        public new async Task ResetAsync()
        {
            await ClearAsync();
            await base.ResetAsync();

            await SeedMembershipAsync(TradingBusinessId, OperatorObjectId, BusinessRole.Operator, isActive: true);
            OtherBusinessId = await SeedBusinessAsync("Second Vending Co", BusinessStatus.Active);
            await SeedMembershipAsync(OtherBusinessId, OtherOwnerObjectId, BusinessRole.Owner, isActive: true);

            await SeedConnectionAsync(TradingBusinessId);
            await SeedConnectionAsync(OtherBusinessId);
            await WriteGrandfatheringAsync(completed: true);

            _requests.Reset();
        }

        /// <summary>Puts one business on <paramref name="plan"/>, replacing whatever it held.</summary>
        public async Task GrantAsync(int businessId, SubscriptionPlan plan)
        {
            await using var db = Database();
            db.BusinessPlanGrants.RemoveRange(
                await db.BusinessPlanGrants.Where(grant => grant.BusinessId == businessId).ToListAsync());
            await db.SaveChangesAsync();

            // Free is the absence of a record, which is how a business really arrives there: the
            // policy's fail-closed answer for a paid feature.
            if (plan != SubscriptionPlan.Free)
            {
                db.BusinessPlanGrants.Add(new BusinessPlanGrant
                {
                    BusinessId = businessId,
                    Plan = plan,
                    Source = ManualPlanGrantSource.Grandfathered,
                    StartsAtUtc = Cutoff,
                    EndsAtUtc = null,
                    GrantedAtUtc = Cutoff,
                });
                await db.SaveChangesAsync();
            }

            _requests.Reset();
        }

        /// <summary>Removes the completion marker, leaving the rollout unable to start.</summary>
        public Task UncompleteGrandfatheringAsync() => WriteGrandfatheringAsync(completed: false);

        public async Task SetConnectionStatusAsync(NayaxConnectionStatus status)
        {
            await using var db = Database();
            var connection = await db.BusinessNayaxConnections
                .SingleAsync(row => row.BusinessId == TradingBusinessId);
            connection.Status = status;
            await db.SaveChangesAsync();
        }

        /// <summary>The trading business's connection row as text, so "untouched" is a comparison.</summary>
        public async Task<string> StoredConnectionAsync()
        {
            await using var db = Database();
            var row = await db.BusinessNayaxConnections.AsNoTracking()
                .SingleAsync(connection => connection.BusinessId == TradingBusinessId);

            return string.Join(
                '|',
                row.BusinessId,
                row.OperatorId,
                row.AccessTokenCiphertext,
                row.EncryptionKeyId,
                row.Status,
                row.CredentialRevision,
                row.MachineChecksPending,
                row.LastTestedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        }

        public async Task<int> TestDraftCountAsync()
        {
            await using var db = Database();
            return await db.NayaxConnectionTestDrafts.CountAsync();
        }

        public async Task<int> StoredSaleCountAsync()
        {
            await using var db = Database();
            return await db.NayaxSales.CountAsync();
        }

        public async Task<int> ImportedFileCountAsync()
        {
            await using var db = Database();
            return await db.ImportedFiles.CountAsync();
        }

        public async Task<int> CommissionAgreementCountAsync()
        {
            await using var db = Database();
            return await db.SiteCommissionAgreements.CountAsync();
        }

        /// <summary>One stored commission agreement, so "nothing stored was removed" has a subject.</summary>
        public async Task SeedCommissionAgreementAsync()
        {
            await using var db = Database();
            db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
            {
                BusinessId = TradingBusinessId,
                SiteId = 1,
                CommissionRate = 10m,
                Basis = CommissionBasis.GrossSales,
                EffectiveFrom = Cutoff,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// One product of the trading business that may be tracked (issue #977): a real name and a
        /// real unit label, which is what <c>ProductPriceTrackingPolicy</c> requires before the
        /// "Track supplier prices" switch may be turned on at all.
        /// </summary>
        public async Task<long> SeedTrackableProductAsync()
        {
            await using var db = Database();
            var product = new Product
            {
                BusinessId = TradingBusinessId,
                Name = "Coca-Cola Classic",
                Unit = "375mL",
                CreatedAt = Cutoff,
                UpdatedAt = Cutoff,
            };

            db.Products.Add(product);
            await db.SaveChangesAsync();

            return product.Id;
        }

        /// <summary>
        /// How many price-tracking mappings exist, so "a downgrade never clears the setting" is a
        /// count rather than an inference.
        /// </summary>
        public async Task<int> PriceTrackingCountAsync()
        {
            await using var db = Database();
            return await db.ProductPriceTrackings.CountAsync();
        }

        /// <summary>One completed sale, so "stored sales are never altered" has a subject.</summary>
        public async Task SeedCompletedSaleAsync()
        {
            await using var db = Database();
            db.NayaxSales.Add(new NayaxSales
            {
                BusinessId = TradingBusinessId,
                TransactionID = 5001,
                MachineID = 55,
                SettlementValue = 3.50m,
                PaymentMethod = "Credit",
                MachineAuthorizationTime = DateTime.UtcNow.AddDays(-1),
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
            });
            await db.SaveChangesAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            // The one setting that makes a plan withhold anything. Every other test host in the
            // repository leaves it off, which is the shipped state.
            if (EnforcePlans)
            {
                builder.UseSetting(ConfiguredSubscriptionEnforcementFlag.EnabledKey, "true");
            }

            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                // The real AES-GCM protector over a test key, so the seeded credentials really are
                // encrypted at rest here rather than refused by the fail-closed unconfigured one.
                services.AddSingleton<INayaxTokenProtector>(new AesGcmNayaxTokenProtector(_protection));

                // The transport, and only the transport. The Nayax client, its credential provider
                // and the status gate are all the production registrations, which is what makes
                // "no Nayax request was made" a statement about the real refusal path.
                services.AddHttpClient(nameof(INayaxLynxClient))
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(_requests));
                services.AddHttpClient(nameof(INayaxConnectionCheckRunner))
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(_requests));
            });
        }

        private static NayaxTokenProtectionOptions Protection()
        {
            var protection = new NayaxTokenProtectionOptions { ActiveKeyId = EncryptionKeyId };
            protection.Keys[EncryptionKeyId] = Convert.ToBase64String(
                Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

            return protection;
        }

        /// <summary>
        /// A stored connection in <see cref="NayaxConnectionStatus.Ready"/> with a genuinely
        /// encrypted token, bound to the business it belongs to. It is what makes every refusal here
        /// a refusal of a connection that would otherwise have worked.
        /// </summary>
        private async Task SeedConnectionAsync(int businessId)
        {
            var encrypted = new AesGcmNayaxTokenProtector(_protection)
                .Protect(AccessToken, Inventory.Domain.Tenancy.BusinessId.From(businessId));

            await using var db = Database();
            db.BusinessNayaxConnections.Add(new BusinessNayaxConnection
            {
                BusinessId = businessId,
                OperatorId = "2002736764",
                AccessTokenCiphertext = encrypted.Ciphertext,
                EncryptionKeyId = encrypted.KeyId,
                Status = NayaxConnectionStatus.Ready,
                CredentialRevision = 1,
                MachineChecksPending = false,
                LastTestedAtUtc = Cutoff,
                UpdatedAtUtc = Cutoff,
            });
            await db.SaveChangesAsync();
        }

        private async Task WriteGrandfatheringAsync(bool completed)
        {
            await using var db = Database();
            db.SubscriptionGrandfatherings.RemoveRange(
                await db.SubscriptionGrandfatherings.ToListAsync());
            await db.SaveChangesAsync();

            db.SubscriptionGrandfatherings.Add(new SubscriptionGrandfathering
            {
                Id = SubscriptionGrandfathering.SingletonId,
                CutoffUtc = Cutoff,
                CompletedAtUtc = completed ? Cutoff.AddMinutes(5) : null,
                GrantedBusinessCount = completed ? 1 : 0,
            });
            await db.SaveChangesAsync();
        }

        private async Task ClearAsync()
        {
            await using var db = Database();
            db.BusinessPlanGrants.RemoveRange(await db.BusinessPlanGrants.ToListAsync());
            db.BusinessBillingSubscriptions.RemoveRange(await db.BusinessBillingSubscriptions.ToListAsync());
            db.SubscriptionGrandfatherings.RemoveRange(await db.SubscriptionGrandfatherings.ToListAsync());
            db.BusinessNayaxConnections.RemoveRange(await db.BusinessNayaxConnections.ToListAsync());
            db.NayaxConnectionTestDrafts.RemoveRange(await db.NayaxConnectionTestDrafts.ToListAsync());
            db.NayaxSales.RemoveRange(await db.NayaxSales.ToListAsync());
            db.SiteCommissionAgreements.RemoveRange(await db.SiteCommissionAgreements.ToListAsync());
            // The mappings before the products they belong to: the foreign key is restricted on
            // delete, as every tenant-owned relationship here is.
            db.ProductPriceTrackings.RemoveRange(await db.ProductPriceTrackings.ToListAsync());
            db.Products.RemoveRange(await db.Products.ToListAsync());
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// The shipped state, over the same world: the rollout setting is off, so a business whose
    /// records would put it on Free is unrestricted and the application behaves exactly as it did
    /// before plans existed (issue #967's "until enforcement is switched on, nothing changes").
    /// </summary>
    public class WithEnforcementOff
        : IClassFixture<ApiFactory.WithEnforcementOff>, IAsyncLifetime
    {
        private readonly ApiFactory.WithEnforcementOff _factory;

        public WithEnforcementOff(ApiFactory.WithEnforcementOff factory) => _factory = factory;

        public Task InitializeAsync() => _factory.ResetAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>
        /// Every endpoint a plan would withhold is served, including the ones a Free business is
        /// refused in the file above - and the business here holds no grant and no subscription at
        /// all, which is exactly the state that would otherwise be Free.
        /// </summary>
        [Theory]
        [InlineData("/api/reports/bookkeeping")]
        [InlineData("/api/reports/gst")]
        [InlineData("/api/reports/daily")]
        [InlineData("/api/reports/transactions")]
        [InlineData("/api/reports/reconciliation")]
        [InlineData("/api/reports/machine-profitability")]
        [InlineData("/api/reports/product-profitability")]
        [InlineData("/api/reports/dashboard")]
        [InlineData("/api/reports/daily/export")]
        [InlineData("/api/site-commissions")]
        [InlineData("/api/site-commissions/agreements")]
        public async Task Nothing_is_withheld_from_a_business_with_no_record_at_all(string uri) =>
            Assert.Equal(
                HttpStatusCode.OK,
                (await _factory.AsOwner().GetAsync(uri)).StatusCode);

        /// <summary>
        /// Including the supplier price data of issue #977, whose own criterion is that nothing
        /// changes for any business until enforcement is switched on: the business here holds no
        /// record at all and is served the summary.
        /// </summary>
        [Fact]
        public async Task The_cheapest_supplier_summary_is_not_withheld_either()
        {
            var productId = await _factory.SeedTrackableProductAsync();

            var response = await _factory.AsOwner().PostAsync(
                "/api/cheapest-suppliers/summary",
                new StringContent(
                    $"{{\"productIds\":[{productId}]}}",
                    Encoding.UTF8,
                    "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        /// <summary>
        /// And the ordinary Nayax path is untouched: the stored credentials resolve and the fleet is
        /// read, rather than being refused by a plan nothing is enforcing.
        /// </summary>
        [Fact]
        public async Task The_provider_gate_still_resolves_credentials_and_reads_the_fleet()
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await _factory.AsOwner().GetAsync("/api/dashboard/summary")).StatusCode);
            Assert.True(_factory.NayaxRequests > 0, "The fleet was not read with enforcement off.");
        }

        /// <summary>
        /// The Owner's credential actions are not refused either, which is the surface a mistyped
        /// setting would otherwise take away from every business at once.
        /// </summary>
        [Fact]
        public async Task The_credential_actions_are_not_refused()
        {
            var response = await _factory.AsOwner().PostAsync(
                "/api/settings/nayax-connection/test",
                new StringContent(
                    $$"""{"operatorId":"2002736764","token":"{{ApiFactory.AccessToken}}"}""",
                    Encoding.UTF8,
                    "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>The shared count of Nayax requests the transport was asked to make.</summary>
    public sealed class NayaxRequestCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Record() => Interlocked.Increment(ref _count);

        public void Reset() => Volatile.Write(ref _count, 0);
    }

    /// <summary>
    /// The Nayax transport under these tests: it counts every request and answers an empty JSON
    /// array. Answering rather than throwing is deliberate - a request that leaked past a plan
    /// refusal must show up as a count, not as an exception a test could mistake for the refusal it
    /// was expecting.
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly NayaxRequestCounter _counter;

        public CountingHandler(NayaxRequestCounter counter) => _counter = counter;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _counter.Record();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }
}
