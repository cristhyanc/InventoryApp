using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The capability policies of issues #525, #526 and #548, exercised through the real request pipeline:
/// authentication, the required scope, the authorization middleware's capability policy,
/// <c>BusinessScopeMiddleware</c>, the controller and relational SQLite with the real migration
/// history.
///
/// <para>Three members of one business hold the three different roles, so every row below is
/// answered by the caller's own stored role and nothing else. The roles each endpoint allows are
/// written out literally, from the agreed table of issue #328, rather than read back from
/// <c>RoleCapabilities</c>: a test that asked the capability table what it allows would pass
/// whatever that table happened to say.</para>
///
/// <para>A second business, with its own Owner and its own rows, exists throughout. Issue #526 is
/// where the first capability lands on a surface a Manager and an Operator may not reach at all,
/// so the isolation tests at the end state the other half of the boundary: holding a capability
/// decides <em>what kind of thing</em> a member may do, never <em>whose data</em> they may do it
/// to.</para>
///
/// <para>"Allowed" is asserted as "not <c>403</c>" rather than as a specific success status. What
/// these rows are about is the access decision; what each endpoint then answers for an empty
/// business - <c>200</c> with no rows, <c>400</c> for a deliberately empty body, <c>404</c> for a
/// supplier that does not exist - is its own contract, tested with that endpoint.
/// <see cref="A_role_that_holds_the_capability_is_answered_normally"/> pins the success side for
/// the reads that need no arrangement, so "not 403" cannot be satisfied by an endpoint that is
/// broken for everybody.</para>
/// </summary>
public sealed class CapabilityAuthorizationApiTests : IClassFixture<CapabilityAuthorizationApiTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public CapabilityAuthorizationApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// Every endpoint a capability policy protects, with the roles the agreed table says may call
    /// it. Reading down the last column is the role matrix a human reviews.
    ///
    /// <para>Each row is called as all three roles, so each is both an allowed and a denied call,
    /// and every row is a read or a call that changes nothing: a report, a listing, a lookup of an
    /// id that does not exist, a preview, a dry run, or a request the endpoint's own validation
    /// refuses. A row that also wrote a record would be arranging state for whichever test ran
    /// next. The write and apply actions that cannot be called that way are in
    /// <see cref="WritingEndpoints"/> below, where only the refusal direction is asserted, and
    /// their capability is pinned by <c>EndpointAuthorizationClassificationTests</c>.</para>
    /// </summary>
    public static TheoryData<string, string, string, string> CapabilityEndpoints => new()
    {
        // Issue #525.
        { "Dashboard.View", "GET", "/api/business/current", "Operator,Manager,Owner" },
        { "Dashboard.View", "GET", "/api/dashboard/summary", "Operator,Manager,Owner" },
        { "PickList.View", "GET", "/api/pick-list", "Operator,Manager,Owner" },
        { "Inventory.Operate", "GET", "/api/products", "Operator,Manager,Owner" },
        { "Inventory.Operate", "GET", "/api/categories", "Operator,Manager,Owner" },
        { "Inventory.Operate", "GET", "/api/sites", "Operator,Manager,Owner" },
        { "Inventory.Operate", "GET", "/api/machines", "Operator,Manager,Owner" },
        { "Inventory.Operate", "GET", "/api/stock-history", "Operator,Manager,Owner" },
        { "Inventory.Operate", "GET", "/api/products/1/stock", "Operator,Manager,Owner" },
        { "Inventory.Operate", "POST", "/api/products/1/inventory-count/apply", "Operator,Manager,Owner" },
        { "Purchasing.Operate", "GET", "/api/purchases", "Operator,Manager,Owner" },
        { "Purchasing.Operate", "GET", "/api/suppliers", "Operator,Manager,Owner" },
        { "Purchasing.Operate", "GET", "/api/suppliers/1/gst-defaults", "Operator,Manager,Owner" },
        { "Purchasing.Operate", "GET", "/api/supplierorders", "Operator,Manager,Owner" },
        { "Purchasing.Operate", "POST", "/api/supplierorders", "Operator,Manager,Owner" },
        { "Suppliers.ManageGstDefaults", "PUT", "/api/suppliers/1/gst-defaults", "Manager,Owner" },

        // Issue #526: the Manager surfaces.
        { "Expenses.Manage", "GET", "/api/operating-expenses", "Manager,Owner" },
        { "Expenses.Manage", "GET", $"/api/operating-expenses/{MissingId}", "Manager,Owner" },
        { "Expenses.Manage", "GET", $"/api/operating-expenses/{MissingId}/attachment", "Manager,Owner" },
        { "Expenses.Manage", "PUT", $"/api/operating-expenses/{MissingId}", "Manager,Owner" },
        { "Expenses.Manage", "DELETE", $"/api/operating-expenses/{MissingId}", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/bookkeeping", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/daily", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/reconciliation", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/machine-profitability", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/product-profitability", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/gst", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/dashboard", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/transactions", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/gst/export", "Manager,Owner" },
        { "Imports.Run", "POST", "/api/imports/products", "Manager,Owner" },
        { "Imports.Run", "POST", "/api/imports/nayax-sales", "Manager,Owner" },
        { "SiteCommissions.Manage", "GET", "/api/site-commissions?from=2026-07-01&to=2026-07-31", "Manager,Owner" },
        { "SiteCommissions.Manage", "GET", "/api/site-commissions/agreements", "Manager,Owner" },
        { "SiteCommissions.Manage", "POST", "/api/site-commissions/agreements", "Manager,Owner" },
        { "SiteCommissions.Manage", "POST", "/api/site-commissions/1/payments", "Manager,Owner" },

        // Issue #526: the Owner-only surfaces.
        { "Integration.Manage", "GET", "/api/settings/nayax-processing-fee-rates", "Owner" },
        { "Integration.Manage", "POST", "/api/settings/nayax-processing-fee-rates", "Owner" },
        { "Integration.Manage", "GET", "/api/data-quality/nayax-catalog-reconciliation", "Owner" },
        // Issue #536. The empty body is refused by the use case's own validation before a rate-limit
        // allowance is spent or anything is sent to Nayax, so the allowed direction is safe to call
        // here: an Owner is answered 400, never 403.
        { "Integration.Manage", "POST", "/api/settings/nayax-connection/test", "Owner" },
        // Issue #537. The empty body carries no draft id, which the use case refuses with 400
        // before a draft is read or a transaction is begun, so an Owner is answered 400 and never
        // 403. The status read is the connection banner: every role may ask whether Nayax is
        // working for their business, because a Manager and an Operator see that banner and cannot
        // open the Owner-only settings page at all.
        { "Integration.Manage", "POST", "/api/settings/nayax-connection/save", "Owner" },
        { "Dashboard.View", "GET", "/api/settings/nayax-connection/status", "Operator,Manager,Owner" },
        { "Data.Repair", "GET", $"/api/admin/inventory-cost-repair/{MissingId}", "Owner" },
        { "Data.Repair", "POST", "/api/admin/historical-gst-classification/preview", "Owner" },
        { "Data.Repair", "POST", "/api/sale-costing/backfill", "Owner" },
        { "Data.Repair", "POST", "/api/sale-costing/nayax-cost-backfill/dry-run", "Owner" },

        // Issue #548: the Owner's own business settings. The read is on the write's capability
        // because it reports the business's contact address, ABN and GST registration.
        { "Business.Manage", "GET", "/api/business/settings", "Owner" },

        // Issue #526: the one endpoint that follows a page every role may open. The home Dashboard
        // calls it on load, before Sites and Machines, so both sections read one freshness
        // boundary (issue #187) - an Operator's Dashboard has to be able to refresh.
        { "Dashboard.View", "POST", "/api/nayax-sales-sync", "Operator,Manager,Owner" },
    };

    /// <summary>
    /// The actions that create, change or repair a record, with the roles that must be refused.
    /// Only the refusal direction is asserted, because calling the allowed direction would write a
    /// purchase classification, a commission agreement, an expense or a recosted sale into the
    /// shared fixture - and for the five repair surfaces it would run the very financial rewrite
    /// those endpoints exist for. Together with <see cref="CapabilityEndpoints"/> this covers every
    /// action of every controller issue #526 classified.
    /// </summary>
    public static TheoryData<string, string, string, bool, string> WritingEndpoints => new()
    {
        { "Expenses.Manage", "POST", "/api/operating-expenses", false, "Operator" },
        { "Expenses.Manage", "POST", "/api/operating-expenses", true, "Operator" },
        { "Expenses.Manage", "PUT", $"/api/operating-expenses/{MissingId}", true, "Operator" },
        { "Imports.Run", "POST", "/api/imports/pending-xml", false, "Operator" },
        { "Data.Repair", "POST", "/api/admin/inventory-cost-repair/preview", false, "Operator,Manager" },
        { "Data.Repair", "POST", "/api/admin/inventory-cost-repair/apply", false, "Operator,Manager" },
        { "Data.Repair", "POST", "/api/admin/inventory-cost-transition/preview", false, "Operator,Manager" },
        { "Data.Repair", "POST", "/api/admin/inventory-cost-transition/apply", false, "Operator,Manager" },
        { "Data.Repair", "POST", "/api/admin/inventory-cost-transition/preview-all", false, "Operator,Manager" },
        { "Data.Repair", "POST", "/api/admin/inventory-cost-transition/apply-all", false, "Operator,Manager" },
        { "Data.Repair", "POST", "/api/admin/historical-gst-classification/apply", false, "Operator,Manager" },
        { "Data.Repair", "POST", "/api/admin/nayax-sale-timestamp-repair/preview", true, "Operator,Manager" },
        { "Data.Repair", "POST", "/api/admin/nayax-sale-timestamp-repair/apply", false, "Operator,Manager" },
        { "Data.Repair", "POST", "/api/sale-costing/nayax-cost-backfill/apply", false, "Operator,Manager" },

        // Issue #548. Only the refusal direction: the allowed direction would rename the shared
        // fixture's business and move its time zone, which every other row here reads.
        { "Business.Manage", "PUT", "/api/business/settings", false, "Operator,Manager" },

        // Issue #537. Only the refusal direction: the re-test authenticates to Nayax with whatever
        // this host's connection holds, and this fixture substitutes no check runner, so the
        // allowed direction belongs in NayaxConnectionSaveRetestApiTests where it does.
        { "Integration.Manage", "POST", "/api/settings/nayax-connection/retest", false, "Operator,Manager" },
    };

    /// <summary>
    /// The matrix itself: for each capability endpoint, every one of the three roles is called and
    /// gets exactly the answer the table above states - reached for a role that holds the
    /// capability, <c>403</c> for one that does not.
    /// </summary>
    [Theory]
    [MemberData(nameof(CapabilityEndpoints))]
    public async Task Each_capability_endpoint_answers_every_role_as_the_agreed_table_says(
        string capability,
        string method,
        string route,
        string rolesThatMayCallIt)
    {
        await _factory.SeedAsync();
        var allowed = rolesThatMayCallIt.Split(',');

        foreach (var (role, objectId) in ApiFactory.Members)
        {
            var response = await _factory.Authenticated(objectId)
                .SendAsync(Request(method, route, MultipartRoutes.Contains(route, StringComparer.Ordinal)));
            var mayCallIt = allowed.Contains(role, StringComparer.Ordinal);

            Assert.True(
                mayCallIt == (response.StatusCode != HttpStatusCode.Forbidden),
                $"{method} {route} ({capability}) answered {(int)response.StatusCode} for a {role}, "
                    + $"which {(mayCallIt ? "holds" : "does not hold")} that capability.");
        }
    }

    /// <summary>
    /// The write and apply actions, in the one direction a test may assert without running them:
    /// a role that does not hold the capability is refused, before the action binds a body, reads a
    /// record or writes anything.
    /// </summary>
    [Theory]
    [MemberData(nameof(WritingEndpoints))]
    public async Task Each_writing_endpoint_refuses_every_role_that_does_not_hold_its_capability(
        string capability,
        string method,
        string route,
        bool multipart,
        string rolesThatMustBeRefused)
    {
        await _factory.SeedAsync();

        foreach (var role in rolesThatMustBeRefused.Split(','))
        {
            var objectId = ApiFactory.Members.Single(member => member.Role == role).ObjectId;
            var response = await _factory.Authenticated(objectId).SendAsync(Request(method, route, multipart));

            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{method} {route} ({capability}) answered {(int)response.StatusCode} for a {role}, "
                    + "which does not hold that capability.");
        }
    }

    /// <summary>
    /// A role that holds a capability really reaches the endpoint rather than merely avoiding
    /// <c>403</c>: the plain reads answer <c>200</c> for every role that holds them, including the
    /// least-privileged one where the capability allows it.
    ///
    /// <para>The reports are deliberately absent: with no Nayax connection configured in this host
    /// they answer the <c>409</c> of <c>NayaxConnectionExceptionHandler</c> for every role alike,
    /// which is their own contract and not an access decision. Their rows in the matrix above still
    /// separate <c>403</c> from <c>409</c>, which is the decision this file is about.</para>
    /// </summary>
    [Theory]
    [InlineData("/api/products", "Operator,Manager,Owner")]
    [InlineData("/api/categories", "Operator,Manager,Owner")]
    [InlineData("/api/suppliers", "Operator,Manager,Owner")]
    [InlineData("/api/stock-history", "Operator,Manager,Owner")]
    [InlineData("/api/business/current", "Operator,Manager,Owner")]
    [InlineData("/api/operating-expenses", "Manager,Owner")]
    [InlineData("/api/site-commissions/agreements", "Manager,Owner")]
    [InlineData("/api/settings/nayax-processing-fee-rates", "Owner")]
    [InlineData("/api/business/settings", "Owner")]
    public async Task A_role_that_holds_the_capability_is_answered_normally(string route, string rolesThatHoldIt)
    {
        await _factory.SeedAsync();

        foreach (var role in rolesThatHoldIt.Split(','))
        {
            var objectId = ApiFactory.Members.Single(member => member.Role == role).ObjectId;
            var response = await _factory.Authenticated(objectId).GetAsync(route);

            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"GET {route} answered {(int)response.StatusCode} for a {role}.");
        }
    }

    /// <summary>
    /// A refusal tells the caller nothing about the role matrix: not the capability it wanted, not
    /// the role it holds, not the roles that would have been allowed, and not that capabilities
    /// exist at all.
    /// </summary>
    [Fact]
    public async Task A_denied_call_says_nothing_about_the_role_matrix()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId)
            .SendAsync(Request("PUT", "/api/suppliers/1/gst-defaults"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        foreach (var leak in new[]
        {
            "Operator", "Manager", "Owner", "apability", "ManageGstDefaults", "role", "Role",
        })
        {
            Assert.DoesNotContain(leak, body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A caller with no membership at all is refused by every capability endpoint. The capability
    /// policy cannot pass for them, because there is no resolved role for it to read.
    /// </summary>
    [Theory]
    [MemberData(nameof(CapabilityEndpoints))]
    public async Task A_caller_with_no_membership_is_refused_by_every_capability_endpoint(
        string capability,
        string method,
        string route,
        string rolesThatMayCallIt)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.StrangerObjectId)
            .SendAsync(Request(method, route, MultipartRoutes.Contains(route, StringComparer.Ordinal)));

        Assert.True(
            response.StatusCode == HttpStatusCode.Forbidden,
            $"{method} {route} ({capability}, allowed for {rolesThatMayCallIt}) answered "
                + $"{(int)response.StatusCode} for a caller with no membership.");
    }

    /// <summary>
    /// The fail-closed direction for a stored role this deployment does not declare: no capability
    /// is granted for it. It is never read as the least-privileged role, which would hand it the
    /// whole operational surface.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/products")]
    [InlineData("GET", "/api/business/current")]
    [InlineData("GET", "/api/pick-list")]
    [InlineData("GET", "/api/purchases")]
    public async Task A_member_whose_stored_role_is_not_declared_is_refused(string method, string route)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.UndeclaredRoleObjectId)
            .SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A capability policy does not replace authentication. An unauthenticated request is still
    /// answered <c>401</c> by the authentication middleware, not <c>403</c>.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/products")]
    [InlineData("GET", "/api/dashboard/summary")]
    [InlineData("PUT", "/api/suppliers/1/gst-defaults")]
    public async Task An_unauthenticated_call_to_a_capability_endpoint_is_401(string method, string route)
    {
        await _factory.SeedAsync();

        var response = await _factory.CreateClient().SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A member whose role changes sees the change on their next request: the capability decision
    /// comes from the same per-request memoised membership resolution the business scope does, so
    /// there is no cache to wait for and no sign-out needed.
    /// </summary>
    [Fact]
    public async Task A_changed_role_changes_what_the_member_may_call_on_the_next_request()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.RoleChangeObjectId);

        var asOperator = await client.SendAsync(Request("PUT", "/api/suppliers/1/gst-defaults"));
        Assert.Equal(HttpStatusCode.Forbidden, asOperator.StatusCode);

        await _factory.SetRoleAsync(ApiFactory.RoleChangeObjectId, BusinessRole.Manager);

        var asManager = await client.SendAsync(Request("PUT", "/api/suppliers/1/gst-defaults"));
        Assert.NotEqual(HttpStatusCode.Forbidden, asManager.StatusCode);
    }

    #region The order with BusinessScopeMiddleware

    /// <summary>
    /// The ordering this slice depends on, made observable. The authorization middleware runs
    /// before <c>BusinessScopeMiddleware</c>, so on an endpoint that declares a capability the
    /// policy decides first and the caller gets the policy's bare <c>403</c> - never the
    /// middleware's membership sentence, which is written only further down the pipeline.
    ///
    /// The capability policy is nevertheless unable to pass before membership is resolved: it
    /// resolves the membership itself, through the same per-request memoised
    /// <c>ICurrentBusinessProvider</c>, instead of relying on the middleware having run. That is
    /// what this row proves together with
    /// <see cref="The_membership_is_resolved_once_for_a_request_that_both_checks"/> and the handler's
    /// own tests.
    /// </summary>
    [Fact]
    public async Task On_a_capability_endpoint_the_policy_answers_a_non_member_before_the_middleware_does()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.StrangerObjectId).GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(
            "not associated with a business",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The contrast that makes the row above an ordering claim rather than a coincidence: on an
    /// endpoint that declares no capability, the same caller still gets the business-scope
    /// middleware's own <c>403</c> body, unchanged.
    /// </summary>
    [Fact]
    public async Task On_an_endpoint_with_no_capability_the_middleware_still_answers_a_non_member()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.StrangerObjectId).GetAsync("/api/me/access");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "not associated with a business",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The capability policy and the business-scope middleware ask one question between them. Both
    /// read the resolution memoised in the request's own scope, so a request that passes a
    /// capability check and then resolves a business queries the membership table exactly once -
    /// which is also why the two can never disagree about the caller's role.
    /// </summary>
    [Fact]
    public async Task The_membership_is_resolved_once_for_a_request_that_both_checks()
    {
        await _factory.SeedAsync();
        _factory.ResetMembershipLookups();

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _factory.MembershipLookups);
    }

    #endregion

    #region A capability is not a way across the business boundary

    /// <summary>
    /// The Owner of one business holds every capability there is, and that buys them nothing in
    /// another business. Asked through the two surfaces issue #526 put behind a capability and that
    /// hold tenant-owned rows - the expense list and lookup, and the Nayax fee rates - the Owner of
    /// A sees only A's rows and gets <c>404</c> for B's expense by its own id. The two decisions are
    /// separate and both in force: the capability says which kind of thing a member may do, the
    /// query filters and the ownership enforcer say whose data they may do it to.
    /// </summary>
    [Fact]
    public async Task An_owner_cannot_reach_another_businesss_data_through_a_capability_they_hold()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.OwnerObjectId);

        var expenses = await client.GetAsync("/api/operating-expenses");
        var expensesBody = await expenses.Content.ReadAsStringAsync();
        var otherExpense = await client.GetAsync($"/api/operating-expenses/{_factory.OtherBusinessExpenseId}");
        var rates = await client.GetAsync("/api/settings/nayax-processing-fee-rates");
        var ratesBody = await rates.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, expenses.StatusCode);
        Assert.Contains(ApiFactory.OwnBusinessExpenseDescription, expensesBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.OtherBusinessExpenseDescription, expensesBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, otherExpense.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rates.StatusCode);
        Assert.Contains(ApiFactory.OwnBusinessFeeRate, ratesBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.OtherBusinessFeeRate, ratesBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction, which is what makes the test above an isolation claim rather than an
    /// endpoint that is broken for everybody: business B's own Owner reaches B's expense by exactly
    /// the id business A's Owner was refused, and sees B's fee rate and not A's.
    /// </summary>
    [Fact]
    public async Task The_other_businesss_owner_reaches_their_own_data_by_the_same_ids()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.OtherBusinessOwnerObjectId);

        var ownExpense = await client.GetAsync($"/api/operating-expenses/{_factory.OtherBusinessExpenseId}");
        var rates = await client.GetAsync("/api/settings/nayax-processing-fee-rates");
        var ratesBody = await rates.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, ownExpense.StatusCode);
        Assert.Contains(
            ApiFactory.OtherBusinessExpenseDescription,
            await ownExpense.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Contains(ApiFactory.OtherBusinessFeeRate, ratesBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.OwnBusinessFeeRate, ratesBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two checks are independent rather than one standing in for the other. An Operator of
    /// business A asking for business B's expense is refused by the capability policy - the bare
    /// <c>403</c>, decided before the action runs - and not by the business boundary's <c>404</c>:
    /// a role that may not use the surface at all never reaches the point where ownership is
    /// filtered, and must not be told whether the id exists somewhere.
    /// </summary>
    [Fact]
    public async Task An_operator_is_refused_by_the_capability_before_the_business_boundary_answers()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId)
            .GetAsync($"/api/operating-expenses/{_factory.OtherBusinessExpenseId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

    /// <summary>
    /// An id no seeded record holds, so a row that reads or deletes "one expense" or "one product's
    /// repair history" asserts the access decision without arranging or destroying state.
    /// </summary>
    private const int MissingId = 999999;

    /// <summary>
    /// The routes that declare <c>[Consumes("multipart/form-data")]</c>. A declared content type is
    /// matched during <em>endpoint selection</em>, which runs before the authorization middleware,
    /// so a JSON body on one of these answers <c>415</c> for every caller and would assert nothing
    /// at all about authorization. They are therefore sent as a multipart form.
    /// </summary>
    private static readonly string[] MultipartRoutes = ["/api/imports/nayax-sales"];

    /// <summary>
    /// The bodies a few rows need instead of the default empty object, because an empty object is a
    /// <em>valid</em> request there and the endpoint would record a financial configuration row. A
    /// deliberately invalid value keeps the row to the access decision it is about.
    /// </summary>
    private static readonly Dictionary<string, string> InvalidBodies = new(StringComparer.Ordinal)
    {
        ["/api/site-commissions/agreements"] = """{"siteId":1,"commissionRate":-1}""",
        ["/api/settings/nayax-processing-fee-rates"] = """{"feeExGst":-1,"effectiveFrom":"2026-07-01T00:00:00Z"}""",
    };

    private static HttpRequestMessage Request(string method, string route, bool multipart = false)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);

        if (multipart)
        {
            // An empty file: enough to select the multipart action, and refused by the endpoint's
            // own "a file is required" validation rather than importing anything.
            request.Content = new MultipartFormDataContent { { new ByteArrayContent([]), "file", "empty.xlsx" } };
        }
        else if (method is "POST" or "PUT")
        {
            request.Content = new StringContent(
                InvalidBodies.TryGetValue(route, out var body) ? body : "{}",
                Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";
        public const string StrangerObjectId = "55555555-5555-5555-5555-555555555555";
        public const string UndeclaredRoleObjectId = "66666666-6666-6666-6666-666666666666";
        public const string RoleChangeObjectId = "77777777-7777-7777-7777-777777777777";

        /// <summary>The Owner of the second business, who is no member of the first one.</summary>
        public const string OtherBusinessOwnerObjectId = "88888888-8888-8888-8888-888888888888";

        public const string OwnBusinessExpenseDescription = "Business A electricity";
        public const string OtherBusinessExpenseDescription = "Business B electricity";

        /// <summary>
        /// One Nayax processing fee rate per business, as the serialized decimal a response body
        /// carries. The two values are deliberately unlike each other and unlike every id, date and
        /// amount in the seed, so "A's rate is there and B's is not" is a claim about that field.
        /// </summary>
        public const string OwnBusinessFeeRate = "0.0191";

        public const string OtherBusinessFeeRate = "0.0777";

        private const int SeededBusinessId = 1;
        private const int OtherBusinessId = 2;

        private static readonly DateTime CreatedAt = new(2026, 10, 11, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private int _membershipLookups;
        private bool _seeded;
        private int _otherBusinessExpenseId;

        public ApiFactory() => _connection.Open();

        /// <summary>The three roles, as the matrix walks them.</summary>
        public static IReadOnlyList<(string Role, string ObjectId)> Members { get; } =
        [
            ("Operator", OperatorObjectId),
            ("Manager", ManagerObjectId),
            ("Owner", OwnerObjectId),
        ];

        /// <summary>How many times the membership table has been queried since the last reset.</summary>
        public int MembershipLookups => Volatile.Read(ref _membershipLookups);

        /// <summary>
        /// The database id of the second business's operating expense, as the database assigned it.
        /// The isolation tests ask business A's Owner for this exact id, so the refusal is about
        /// ownership and not about an id nothing holds.
        /// </summary>
        public int OtherBusinessExpenseId => _otherBusinessExpenseId;

        public void ResetMembershipLookups() => Volatile.Write(ref _membershipLookups, 0);

        /// <summary>An unrestricted context for arranging state outside the request boundary.</summary>
        public AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which is what applies the migrations
            // this context then reads and writes through.
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        public HttpClient Authenticated(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");
            return client;
        }

        /// <summary>
        /// Changes a member's stored role outside the request boundary, the way a future members
        /// page or an operator would.
        /// </summary>
        public async Task SetRoleAsync(string objectId, BusinessRole role)
        {
            await using var db = Database();
            var membership = await db.BusinessMemberships.SingleAsync(m => m.ObjectId == objectId);
            membership.Role = role;
            await db.SaveChangesAsync();
        }

        public async Task SeedAsync()
        {
            await using var db = Database();
            if (_seeded)
            {
                return;
            }

            db.Businesses.AddRange(
                new Business
                {
                    Id = SeededBusinessId,
                    Name = "Vending Co",
                    TimeZoneId = "Australia/Sydney",
                    Status = BusinessStatus.Active,
                    CreatedAtUtc = CreatedAt,
                },
                // The second business exists throughout, so every row of the matrix runs with
                // another business's data present rather than against an API that has only one.
                new Business
                {
                    Id = OtherBusinessId,
                    Name = "Other Vending Co",
                    TimeZoneId = "Australia/Sydney",
                    Status = BusinessStatus.Active,
                    CreatedAtUtc = CreatedAt,
                });

            db.BusinessMemberships.AddRange(
                Membership(OwnerObjectId, BusinessRole.Owner, SeededBusinessId),
                Membership(ManagerObjectId, BusinessRole.Manager, SeededBusinessId),
                Membership(OperatorObjectId, BusinessRole.Operator, SeededBusinessId),
                Membership(RoleChangeObjectId, BusinessRole.Operator, SeededBusinessId),
                // A stored role no BusinessRole declares. In production it could only arrive from a
                // hand-written UPDATE or a newer deployment; the cast is how a test writes it.
                Membership(UndeclaredRoleObjectId, (BusinessRole)0, SeededBusinessId),
                Membership(OtherBusinessOwnerObjectId, BusinessRole.Owner, OtherBusinessId));

            // One tenant-owned row of each kind per business, so the isolation tests can name what
            // each side must and must not see. BusinessId is stated rather than stamped: this is an
            // unrestricted setup context, deliberately outside the request boundary.
            var otherBusinessExpense = Expense(OtherBusinessId, OtherBusinessExpenseDescription);
            db.OperatingExpenses.AddRange(
                Expense(SeededBusinessId, OwnBusinessExpenseDescription),
                otherBusinessExpense);

            db.NayaxProcessingFeeRates.AddRange(
                FeeRate(SeededBusinessId, decimal.Parse(OwnBusinessFeeRate, CultureInfo.InvariantCulture)),
                FeeRate(OtherBusinessId, decimal.Parse(OtherBusinessFeeRate, CultureInfo.InvariantCulture)));

            await db.SaveChangesAsync();
            _otherBusinessExpenseId = otherBusinessExpense.Id;
            _seeded = true;
        }

        private static BusinessMembership Membership(string objectId, BusinessRole role, int businessId) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                CreatedAtUtc = CreatedAt,
            };

        private static OperatingExpense Expense(int businessId, string description) =>
            new()
            {
                BusinessId = businessId,
                ExpenseDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                Category = OperatingExpenseCategory.PhoneInternet,
                Description = description,
                AmountExGst = 100m,
                GstAmount = 10m,
                TotalAmount = 110m,
                CreatedAt = CreatedAt,
                UpdatedAt = CreatedAt,
            };

        private static NayaxProcessingFeeRate FeeRate(int businessId, decimal feeExGst) =>
            new()
            {
                BusinessId = businessId,
                EffectiveFrom = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeExGst = feeExGst,
                CreatedAt = CreatedAt,
            };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is the
            // migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { });

                // The real EF store, counted. Registered after the composition root's registration
                // so this one wins; it changes no behaviour and only records how often the
                // membership table is read.
                services.AddScoped<IBusinessMembershipStore>(sp => new CountingBusinessMembershipStore(
                    new EfBusinessMembershipStore(sp.GetRequiredService<AppDbContext>()),
                    () => Interlocked.Increment(ref _membershipLookups)));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }
    }

    /// <summary>
    /// Counts membership lookups without changing one, so "the resolution is memoised" can be
    /// asserted as a fact about the request rather than inferred from the provider's source.
    /// </summary>
    private sealed class CountingBusinessMembershipStore(IBusinessMembershipStore inner, Action count)
        : IBusinessMembershipStore
    {
        public Task<IReadOnlyList<ActorBusinessMembership>> FindMembershipsAsync(
            ActorIdentity actor,
            CancellationToken cancellationToken)
        {
            count();
            return inner.FindMembershipsAsync(actor, cancellationToken);
        }
    }

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from
    /// request headers, so membership resolution, the capability policy and the scope requirement
    /// are exercised exactly as they are in production without a real token.
    /// </summary>
    private sealed class TestActorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestActor";
        public const string DirectoryTenantIdHeader = "X-Test-Directory-Tenant-Id";
        public const string ObjectIdHeader = "X-Test-Object-Id";
        public const string ScopeHeader = "X-Test-Scope";

        public TestActorAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(ObjectIdHeader, out var objectId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new("tid", Request.Headers[DirectoryTenantIdHeader].ToString()),
                new("oid", objectId.ToString()),
            };
            if (Request.Headers.TryGetValue(ScopeHeader, out var scope))
            {
                claims.Add(new Claim("scp", scope.ToString()));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
}
