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
/// The capability policies of issues #525 and #526, exercised through the real request pipeline:
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
/// <para>A second business with its own Owner is seeded alongside the first, so the matrix is not
/// the only question asked: a capability is what a member may do, and the business boundary is
/// whose data they may do it to. The two are independent, which
/// <see cref="An_owner_cannot_reach_another_businesss_data_through_a_capability_they_hold"/> is
/// what proves - the Owner of A holds every capability and still reaches none of B's rows.</para>
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
    /// Every endpoint these slices put behind a capability, with the roles the agreed table says
    /// may call it. Reading down the last column is the role matrix a human reviews.
    ///
    /// <para>Where a capability has both a read and a write, the row is the read or a write that
    /// changes nothing - a preview, a dry run, or a delete of an id that does not exist. The
    /// question here is who is refused, and a row that also wrote a record would be arranging state
    /// for whichever test in this class happened to run next.</para>
    /// </summary>
    public static TheoryData<string, string, string, string> CapabilityEndpoints => new()
    {
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

        // Issue #526: the Manager-only surfaces.
        { "Expenses.Manage", "GET", "/api/operating-expenses", "Manager,Owner" },
        { "Expenses.Manage", "GET", "/api/operating-expenses/1", "Manager,Owner" },
        { "Expenses.Manage", "DELETE", "/api/operating-expenses/1", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/daily", "Manager,Owner" },
        { "Reports.View", "GET", "/api/reports/gst", "Manager,Owner" },
        { "Imports.Run", "POST", "/api/imports/products", "Manager,Owner" },
        { "Imports.Run", "POST", "/api/imports/nayax-sales", "Manager,Owner" },
        { "SiteCommissions.Manage", "GET", "/api/site-commissions", "Manager,Owner" },
        { "SiteCommissions.Manage", "GET", "/api/site-commissions/agreements", "Manager,Owner" },

        // Issue #526: the Owner-only surfaces.
        { "Integration.Manage", "GET", "/api/settings/nayax-processing-fee-rates", "Owner" },
        { "Integration.Manage", "GET", "/api/data-quality/nayax-catalog-reconciliation", "Owner" },
        { "Data.Repair", "GET", "/api/admin/inventory-cost-repair/1", "Owner" },
        { "Data.Repair", "POST", "/api/admin/inventory-cost-repair/preview", "Owner" },
        { "Data.Repair", "POST", "/api/admin/inventory-cost-transition/preview", "Owner" },
        { "Data.Repair", "POST", "/api/admin/historical-gst-classification/preview", "Owner" },
        { "Data.Repair", "POST", "/api/admin/nayax-sale-timestamp-repair/preview", "Owner" },
        { "Data.Repair", "POST", "/api/sale-costing/backfill", "Owner" },

        // Issue #526: the home Dashboard calls this on load, so it follows the Dashboard's own
        // capability and an Operator's dashboard keeps working.
        { "Dashboard.View", "POST", "/api/nayax-sales-sync", "Operator,Manager,Owner" },
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
            var response = await _factory.Authenticated(objectId).SendAsync(Request(method, route));
            var mayCallIt = allowed.Contains(role, StringComparer.Ordinal);

            Assert.True(
                mayCallIt == (response.StatusCode != HttpStatusCode.Forbidden),
                $"{method} {route} ({capability}) answered {(int)response.StatusCode} for a {role}, "
                    + $"which {(mayCallIt ? "holds" : "does not hold")} that capability.");
        }
    }

    /// <summary>
    /// A role that holds a capability really reaches the endpoint rather than merely avoiding
    /// <c>403</c>: the plain reads answer <c>200</c> for every role, including the least-privileged
    /// one.
    /// </summary>
    [Theory]
    [InlineData("/api/products")]
    [InlineData("/api/categories")]
    [InlineData("/api/suppliers")]
    [InlineData("/api/stock-history")]
    [InlineData("/api/business/current")]
    public async Task A_role_that_holds_the_capability_is_answered_normally(string route)
    {
        await _factory.SeedAsync();

        foreach (var (role, objectId) in ApiFactory.Members)
        {
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
            .SendAsync(Request(method, route));

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

    #region A capability is not a key to another business

    /// <summary>
    /// Holding a capability says what a member may do, never whose data they may do it to. The
    /// Owner of business A holds every capability there is, and still reaches none of business B's
    /// rows: B's operating expense is absent from A's list and <c>404</c> by its own id, and B's
    /// Nayax fee rate is absent from A's. Both endpoints are ones this slice has just opened to a
    /// role for the first time, which is exactly when "may do it" is easiest to confuse with "may
    /// do it anywhere".
    /// </summary>
    [Fact]
    public async Task An_owner_cannot_reach_another_businesss_data_through_a_capability_they_hold()
    {
        await _factory.SeedAsync();
        var ownerOfA = _factory.Authenticated(ApiFactory.OwnerObjectId);

        var expenses = await ownerOfA.GetAsync("/api/operating-expenses");
        var otherExpense = await ownerOfA.GetAsync($"/api/operating-expenses/{ApiFactory.OtherBusinessExpenseId}");
        var feeRates = await ownerOfA.GetAsync("/api/settings/nayax-processing-fee-rates");

        Assert.Equal(HttpStatusCode.OK, expenses.StatusCode);
        Assert.Contains(ApiFactory.OwnExpenseDescription, await expenses.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            ApiFactory.OtherBusinessExpenseDescription,
            await expenses.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, otherExpense.StatusCode);

        Assert.Equal(HttpStatusCode.OK, feeRates.StatusCode);
        Assert.DoesNotContain(
            ApiFactory.OtherBusinessFeeRate,
            await feeRates.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction, so the row above cannot pass because the endpoints are broken for
    /// everybody: business B's own Owner reads B's expense and B's fee rate perfectly well, by the
    /// same ids A was refused.
    /// </summary>
    [Fact]
    public async Task The_other_businesss_owner_reaches_their_own_data_by_the_same_ids()
    {
        await _factory.SeedAsync();
        var ownerOfB = _factory.Authenticated(ApiFactory.OtherBusinessOwnerObjectId);

        var ownExpense = await ownerOfB.GetAsync($"/api/operating-expenses/{ApiFactory.OtherBusinessExpenseId}");
        var feeRates = await ownerOfB.GetAsync("/api/settings/nayax-processing-fee-rates");

        Assert.Equal(HttpStatusCode.OK, ownExpense.StatusCode);
        Assert.Contains(
            ApiFactory.OtherBusinessExpenseDescription,
            await ownExpense.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, feeRates.StatusCode);
        var body = await feeRates.Content.ReadAsStringAsync();
        Assert.Contains(ApiFactory.OtherBusinessFeeRate, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.OwnFeeRate, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A member of A whose role does not grant the capability is refused before the business
    /// boundary is ever reached, so the two checks are independent rather than one standing in for
    /// the other: an Operator of A asking for B's expense gets the capability's bare <c>403</c>,
    /// not the <c>404</c> the boundary would have given them.
    /// </summary>
    [Fact]
    public async Task An_operator_is_refused_by_the_capability_before_the_business_boundary_answers()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId)
            .GetAsync($"/api/operating-expenses/{ApiFactory.OtherBusinessExpenseId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

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

    /// <summary>
    /// The routes whose action declares <c>[Consumes("multipart/form-data")]</c> because it takes an
    /// uploaded file. They need a multipart body here for a reason worth recording: a declared
    /// content type is matched during <em>endpoint selection</em>, which happens before the
    /// authorization middleware runs, so one of these routes answers <c>415</c> to a JSON body
    /// whatever the caller's role is - including a role that holds the capability. The <c>415</c>
    /// leaks nothing (it is decided from the request's own header, with no membership read), but a
    /// matrix row sent as JSON would be asserting nothing about authorization at all.
    /// </summary>
    private static readonly string[] MultipartRoutes =
    [
        "/api/imports/nayax-sales",
        "/api/admin/nayax-sale-timestamp-repair/preview",
    ];

    private static HttpRequestMessage Request(string method, string route)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);

        if (MultipartRoutes.Contains(route, StringComparer.Ordinal))
        {
            request.Content = new MultipartFormDataContent("boundary-" + Guid.NewGuid().ToString("N"));
        }
        else if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", Encoding.UTF8);
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

        /// <summary>The Owner of the second business, who is a member of no other business.</summary>
        public const string OtherBusinessOwnerObjectId = "88888888-8888-8888-8888-888888888888";

        /// <summary>Business B's own operating expense, by the id business A will ask for.</summary>
        public const int OtherBusinessExpenseId = 20;

        public const string OwnExpenseDescription = "Business A warehouse rent";
        public const string OtherBusinessExpenseDescription = "Business B warehouse rent";

        /// <summary>
        /// The two fee rates, as they are spelled in a JSON response body. Distinct values, so
        /// "A cannot see B's" is a fact about the rate rather than about the number zero.
        /// </summary>
        public const string OwnFeeRate = "0.11";

        public const string OtherBusinessFeeRate = "0.22";

        private const int SeededBusinessId = 1;
        private const int OtherBusinessId = 2;
        private const int OwnExpenseId = 10;

        private static readonly DateTime CreatedAt = new(2026, 10, 11, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private int _membershipLookups;
        private bool _seeded;

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
                    CreatedAtUtc = CreatedAt,
                },
                new Business
                {
                    Id = OtherBusinessId,
                    Name = "Other Vending Co",
                    TimeZoneId = "Australia/Sydney",
                    CreatedAtUtc = CreatedAt,
                });

            db.BusinessMemberships.AddRange(
                Membership(SeededBusinessId, OwnerObjectId, BusinessRole.Owner),
                Membership(SeededBusinessId, ManagerObjectId, BusinessRole.Manager),
                Membership(SeededBusinessId, OperatorObjectId, BusinessRole.Operator),
                Membership(SeededBusinessId, RoleChangeObjectId, BusinessRole.Operator),
                // A stored role no BusinessRole declares. In production it could only arrive from a
                // hand-written UPDATE or a newer deployment; the cast is how a test writes it.
                Membership(SeededBusinessId, UndeclaredRoleObjectId, (BusinessRole)0),
                Membership(OtherBusinessId, OtherBusinessOwnerObjectId, BusinessRole.Owner));

            // One record per business on two of the surfaces issue #526 protects, written through
            // the unrestricted context because arranging another business's state is exactly what
            // that opt-in exists for. A request never gets one.
            db.OperatingExpenses.AddRange(
                Expense(SeededBusinessId, OwnExpenseId, OwnExpenseDescription),
                Expense(OtherBusinessId, OtherBusinessExpenseId, OtherBusinessExpenseDescription));

            // No explicit id: the AddNayaxProcessingFeeRates migration already inserted the legacy
            // default rate, so the table's keys are the database's to hand out.
            db.NayaxProcessingFeeRates.AddRange(
                FeeRate(SeededBusinessId, 0.11m),
                FeeRate(OtherBusinessId, 0.22m));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static BusinessMembership Membership(int businessId, string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                CreatedAtUtc = CreatedAt,
            };

        private static OperatingExpense Expense(int businessId, int id, string description) =>
            new()
            {
                BusinessId = businessId,
                Id = id,
                ExpenseDate = CreatedAt.Date,
                Category = OperatingExpenseCategory.Other,
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
                EffectiveFrom = CreatedAt.Date,
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
