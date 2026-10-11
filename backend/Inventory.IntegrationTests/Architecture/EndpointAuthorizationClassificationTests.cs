using System.Reflection;
using Inventory.Application.Access;
using InventoryApi.Auth;
using InventoryApi.Auth.PlatformAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Identity.Web.Resource;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Every endpoint declares how it is authorized, and there are exactly four ways to do it
/// (issues #525 and #526, the two slices of #502 that enforce capabilities).
///
/// <para>The reason this test exists is the failure mode it removes. Before capability policies, an
/// endpoint that declared nothing beyond <c>[Authorize]</c> was reachable by every active member of
/// a business, and a <em>new</em> endpoint that declared nothing was reachable the same way - open
/// by default, and invisibly so, because nothing in the diff said "this is open". This test turns
/// that into a build failure: an action that is in none of the four classes below fails here until
/// the same pull request classifies it.</para>
///
/// <para>The four classes, which are the complete set:</para>
/// <list type="number">
///   <item><b>A capability policy.</b> <c>[RequireCapability(...)]</c> on the action or its
///   controller. This is what a business endpoint normally is.</item>
///   <item><b>The platform diagnostics surface.</b> <c>PlatformAdminPolicy</c> <em>and</em>
///   <see cref="PlatformDiagnosticsEndpointAttribute"/> together (issue #336). Both are required:
///   the marker alone grants nothing and the policy alone would not be eligible for the membership
///   bypass.</item>
///   <item><b>The membership-not-required family.</b>
///   <see cref="MembershipNotRequiredEndpointAttribute"/> on an endpoint named in
///   <see cref="MembershipNotRequiredAllowlist"/> below (issue #523). The allowlist lives here so
///   that taking the exemption is a reviewed change.</item>
///   <item><b><c>GET /api/me/access</c>.</b> One endpoint, named explicitly: a member must be able
///   to ask what they may do whatever role they hold, so it can carry no capability of its own
///   without becoming unanswerable for somebody. It still requires authentication, the delegated
///   scope and a membership.</item>
/// </list>
///
/// <para>The classes are mutually exclusive, which is asserted rather than assumed: an endpoint
/// that answered to two of them would have two authorization stories and no single reviewable
/// one.</para>
///
/// <para>There is no longer a pending list. Issue #525's temporary <c>PendingClassification</c>
/// entry per unreached controller recorded work that was still open; issue #526 classified the last
/// of those controllers and deleted the list, so "every endpoint" below means every endpoint with
/// no exception at all. Nothing may reintroduce it: an endpoint that needs a different answer takes
/// one of the four classes, and a fifth class is a decision made here and in docs/architecture.md
/// together.</para>
/// </summary>
public class EndpointAuthorizationClassificationTests
{
    /// <summary>
    /// Every endpoint allowed to skip the membership requirement, as its route template - class 3,
    /// and the reviewed allowlist issue #523 introduced. Adding a line here is the exemption
    /// itself: the marker is what makes an authenticated caller stop being the caller of
    /// <em>one</em> business's API, so it may only ever be given to an endpoint that answers a fact
    /// about the caller's own identity and no business data at all. Issue #504's invite redemption
    /// and issue #507's application endpoints are the ones expected to join it.
    /// </summary>
    internal static readonly string[] MembershipNotRequiredAllowlist = ["api/me/account-state"];

    /// <summary>
    /// Class 4: the one endpoint that is its own class. It is named rather than marked, because a
    /// marker would be a mechanism somebody could reuse.
    /// </summary>
    private const string OwnAccessRoute = "api/me/access";

    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    private const BindingFlags DeclaredActions = BindingFlags.Public
        | BindingFlags.Instance
        | BindingFlags.DeclaredOnly;

    /// <summary>How an endpoint declares its authorization. Exactly four, and no "other".</summary>
    private enum EndpointClass
    {
        CapabilityPolicy,
        PlatformDiagnostics,
        MembershipNotRequired,
        OwnAccess,
    }

    #region Every endpoint is classified

    /// <summary>
    /// The rule itself: every action on every controller is in one of the four classes, with no
    /// exception and no pending list. A new endpoint therefore cannot default to "every member may
    /// call it" - the build fails until somebody decides what it requires.
    /// </summary>
    [Fact]
    public void Every_endpoint_declares_one_of_the_four_authorization_classes()
    {
        var unclassified = Endpoints()
            .Where(endpoint => ClassesOf(endpoint).Count == 0)
            .Select(endpoint => $"{endpoint.Controller.Name}.{endpoint.Method.Name} ({endpoint.Route})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unclassified.Length == 0,
            "Every endpoint must declare how it is authorized: a capability policy "
                + "([RequireCapability]), the platform diagnostics policy plus its marker, the "
                + "reviewed membership-not-required marker, or GET api/me/access. An endpoint that "
                + "declares none of them is reachable by every active member of its business, which "
                + "is what issue #502 exists to end (AGENTS.md § Architecture rules, "
                + "docs/architecture.md § Capability authorization). Unclassified endpoint(s): "
                + $"{string.Join(", ", unclassified)}.");
    }

    /// <summary>
    /// The classes do not overlap. Two authorization stories on one endpoint would mean no single
    /// reviewable answer to "who may call this", and in the diagnostics case specifically it is how
    /// a business capability could become a second way into a cross-business surface.
    /// </summary>
    [Fact]
    public void No_endpoint_is_in_more_than_one_class()
    {
        var ambiguous = Endpoints()
            .Select(endpoint => (endpoint, Classes: ClassesOf(endpoint)))
            .Where(classified => classified.Classes.Count > 1)
            .Select(classified =>
                $"{classified.endpoint.Controller.Name}.{classified.endpoint.Method.Name} "
                    + $"({string.Join(" + ", classified.Classes.Order())})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            ambiguous.Length == 0,
            "An endpoint must be in exactly one authorization class. Offending endpoint(s): "
                + $"{string.Join(", ", ambiguous)}.");
    }

    /// <summary>
    /// The recognised classes are exactly four. A fifth would be a new way for an endpoint to be
    /// authorized, which is a boundary decision to be made here and in docs/architecture.md
    /// together rather than a kind that quietly appears.
    /// </summary>
    [Fact]
    public void Exactly_four_classes_are_recognised()
    {
        Assert.Equal(
            [
                EndpointClass.CapabilityPolicy,
                EndpointClass.PlatformDiagnostics,
                EndpointClass.MembershipNotRequired,
                EndpointClass.OwnAccess,
            ],
            Enum.GetValues<EndpointClass>());
    }

    #endregion

    #region Class 1: a capability policy

    /// <summary>
    /// A capability endpoint declares a capability and nothing weaker. It keeps <c>[Authorize]</c>
    /// and the delegated scope - a capability is an additional requirement, never a replacement for
    /// authentication - and it may not carry <c>[AllowAnonymous]</c>.
    /// </summary>
    [Fact]
    public void A_capability_endpoint_still_requires_authentication_and_the_delegated_scope()
    {
        foreach (var endpoint in Endpoints().Where(endpoint => ClassesOf(endpoint).Contains(EndpointClass.CapabilityPolicy)))
        {
            Assert.True(
                HasAttribute<AuthorizeAttribute>(endpoint),
                $"{endpoint.Route} declares a capability without requiring authentication.");
            Assert.True(
                HasAttribute<RequiredScopeAttribute>(endpoint),
                $"{endpoint.Route} declares a capability without requiring the delegated scope.");
            Assert.False(
                HasAttribute<AllowAnonymousAttribute>(endpoint),
                $"{endpoint.Route} declares a capability and allows anonymous callers.");
        }
    }

    /// <summary>
    /// Controllers reference capabilities, not roles. A <c>BusinessRole</c> named at the HTTP
    /// boundary would be a second, local answer to "may this member do that?" beside the one
    /// <c>RoleCapabilities</c> table - and the one that drifts, because it is the one
    /// <c>GET /api/me/access</c> does not read.
    /// </summary>
    [Fact]
    public void No_controller_names_a_business_role()
    {
        var offenders = ApiAssembly.GetTypes()
            .Where(type => type.Name.EndsWith("Controller", StringComparison.Ordinal))
            .SelectMany(controller => controller
                .GetCustomAttributes()
                .Concat(controller.GetMethods(DeclaredActions).SelectMany(method => method.GetCustomAttributes()))
                .SelectMany(attribute => attribute.GetType().GetProperties()
                    .Where(property => property.PropertyType == typeof(Inventory.Domain.Tenancy.BusinessRole))
                    .Select(property => $"{controller.Name} -> {attribute.GetType().Name}.{property.Name}")))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A controller must declare a capability, never a role: which roles hold a capability is "
                + "Inventory.Application.Access.RoleCapabilities's single answer, and the same one "
                + "GET /api/me/access reports (docs/architecture.md § Capability authorization). "
                + $"Offending declaration(s): {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Every capability an endpoint declares is a declared capability with a registered policy
    /// name. The attribute refuses an undeclared value at construction, so this is the standing
    /// proof that the refusal is in force rather than a rule only its own test exercises.
    /// </summary>
    [Fact]
    public void Every_declared_capability_is_part_of_the_published_vocabulary()
    {
        var declared = Endpoints()
            .SelectMany(CapabilitiesOf)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.All(declared, capability => Assert.Contains(capability, Capabilities.All));
        Assert.All(
            declared,
            capability => Assert.StartsWith(
                CapabilityPolicy.NamePrefix,
                CapabilityPolicy.Name(capability),
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The capabilities issue #525 applies, as the issue lists them, read back from the endpoints
    /// themselves. It is the acceptance criterion in one place: a later change that moved an
    /// endpoint to another capability, or dropped one, fails here rather than only in the per-role
    /// API tests.
    /// </summary>
    [Theory]
    [InlineData("api/dashboard/summary", "GET", Capability.DashboardView)]
    [InlineData("api/business/current", "GET", Capability.DashboardView)]
    [InlineData("api/pick-list", "GET", Capability.PickListView)]
    [InlineData("api/Products", "GET", Capability.InventoryOperate)]
    [InlineData("api/Products/{id:long}", "PUT", Capability.InventoryOperate)]
    [InlineData("api/Products/{id:long}/gst-rule", "PUT", Capability.InventoryOperate)]
    [InlineData("api/products/{productId:long}/inventory-count/apply", "POST", Capability.InventoryOperate)]
    [InlineData("api/Categories", "GET", Capability.InventoryOperate)]
    [InlineData("api/products/{productId:long}/stock", "POST", Capability.InventoryOperate)]
    [InlineData("api/stock-history", "GET", Capability.InventoryOperate)]
    [InlineData("api/Machines", "GET", Capability.InventoryOperate)]
    [InlineData("api/Machines/{id:long}/sync-restock", "POST", Capability.InventoryOperate)]
    [InlineData("api/Sites", "GET", Capability.InventoryOperate)]
    [InlineData("api/purchases", "POST", Capability.PurchasingOperate)]
    [InlineData("api/purchases/{id:int}/file", "GET", Capability.PurchasingOperate)]
    [InlineData("api/SupplierOrders", "POST", Capability.PurchasingOperate)]
    [InlineData("api/Suppliers", "GET", Capability.PurchasingOperate)]
    [InlineData("api/Suppliers/{id:int}/gst-defaults", "PUT", Capability.SuppliersManageGstDefaults)]
    public void The_listed_endpoints_declare_the_capability_the_issue_assigns_them(
        string route,
        string httpMethod,
        Capability capability) =>
        Assert.Contains(capability, CapabilitiesOf(Endpoint(route, httpMethod)));

    /// <summary>
    /// The capabilities issue #526 applies: the Manager-only surfaces, the Owner-only Nayax
    /// settings, and the Owner-only data repairs. The two endpoints the issue said follow the page
    /// that calls them are here with the mapping that was reviewed - <c>POST api/nayax-sales-sync</c>
    /// is called by the home Dashboard on load, so it takes the Dashboard's own <c>Dashboard.View</c>
    /// and an Operator's dashboard keeps working; <c>GET
    /// api/data-quality/nayax-catalog-reconciliation</c> is called by no page at all, and reports
    /// how the business's Nayax catalogue has drifted from its local records, so it takes the
    /// Owner-only <c>Integration.Manage</c> rather than being left open.
    /// </summary>
    [Theory]
    [InlineData("api/operating-expenses", "GET", Capability.ExpensesManage)]
    [InlineData("api/operating-expenses/{id:int}", "GET", Capability.ExpensesManage)]
    [InlineData("api/operating-expenses/{id:int}/attachment", "GET", Capability.ExpensesManage)]
    [InlineData("api/operating-expenses/{id:int}", "DELETE", Capability.ExpensesManage)]
    [InlineData("api/reports/daily", "GET", Capability.ReportsView)]
    [InlineData("api/reports/gst", "GET", Capability.ReportsView)]
    [InlineData("api/reports/{report}/export", "GET", Capability.ReportsView)]
    [InlineData("api/imports/products", "POST", Capability.ImportsRun)]
    [InlineData("api/imports/nayax-sales", "POST", Capability.ImportsRun)]
    [InlineData("api/imports/pending-xml", "POST", Capability.ImportsRun)]
    [InlineData("api/site-commissions", "GET", Capability.SiteCommissionsManage)]
    [InlineData("api/site-commissions/agreements", "POST", Capability.SiteCommissionsManage)]
    [InlineData("api/site-commissions/{siteId:long}/payments", "POST", Capability.SiteCommissionsManage)]
    [InlineData("api/settings/nayax-processing-fee-rates", "GET", Capability.IntegrationManage)]
    [InlineData("api/settings/nayax-processing-fee-rates", "POST", Capability.IntegrationManage)]
    [InlineData("api/data-quality/nayax-catalog-reconciliation", "GET", Capability.IntegrationManage)]
    [InlineData("api/admin/inventory-cost-repair/preview", "POST", Capability.DataRepair)]
    [InlineData("api/admin/inventory-cost-repair/apply", "POST", Capability.DataRepair)]
    [InlineData("api/admin/inventory-cost-repair/{productId:long}", "GET", Capability.DataRepair)]
    [InlineData("api/admin/inventory-cost-transition/preview", "POST", Capability.DataRepair)]
    [InlineData("api/admin/inventory-cost-transition/apply-all", "POST", Capability.DataRepair)]
    [InlineData("api/admin/historical-gst-classification/preview", "POST", Capability.DataRepair)]
    [InlineData("api/admin/historical-gst-classification/apply", "POST", Capability.DataRepair)]
    [InlineData("api/admin/nayax-sale-timestamp-repair/preview", "POST", Capability.DataRepair)]
    [InlineData("api/admin/nayax-sale-timestamp-repair/apply", "POST", Capability.DataRepair)]
    [InlineData("api/sale-costing/backfill", "POST", Capability.DataRepair)]
    [InlineData("api/sale-costing/nayax-cost-backfill/apply", "POST", Capability.DataRepair)]
    [InlineData("api/nayax-sales-sync", "POST", Capability.DashboardView)]
    public void The_manager_and_owner_endpoints_declare_the_capability_issue_526_assigns_them(
        string route,
        string httpMethod,
        Capability capability) =>
        Assert.Contains(capability, CapabilitiesOf(Endpoint(route, httpMethod)));

    /// <summary>
    /// The repair surface is Owner-only as one surface, not endpoint by endpoint. Every action of
    /// every maintenance-repair controller requires <c>Data.Repair</c>, so a later endpoint added
    /// to one of them inherits the capability instead of arriving open - and a preview is as
    /// restricted as the apply it precedes, because a preview of a costing repair reads the same
    /// financial history the apply rewrites.
    /// </summary>
    [Theory]
    [InlineData("HistoricalGstClassificationController")]
    [InlineData("InventoryCostRepairsController")]
    [InlineData("InventoryCostTransitionsController")]
    [InlineData("NayaxSaleTimestampRepairsController")]
    [InlineData("SaleCostingController")]
    public void Every_action_of_a_repair_controller_requires_the_data_repair_capability(string controller)
    {
        var actions = Endpoints()
            .Where(endpoint => string.Equals(endpoint.Controller.Name, controller, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(actions);
        Assert.All(actions, endpoint => Assert.Equal([Capability.DataRepair], CapabilitiesOf(endpoint)));
    }

    /// <summary>
    /// The supplier GST-default read and edit are deliberately different: reading stays on
    /// <c>Purchasing.Operate</c>, so an Operator recording a purchase can see which classification
    /// it will be given, while the edit needs <c>Suppliers.ManageGstDefaults</c>. A later change
    /// that put the whole resource on one capability would quietly move one of them.
    /// </summary>
    [Fact]
    public void Reading_a_suppliers_gst_defaults_is_not_the_capability_that_edits_them()
    {
        var read = Endpoint("api/Suppliers/{id:int}/gst-defaults", "GET");
        var edit = Endpoint("api/Suppliers/{id:int}/gst-defaults", "PUT");

        Assert.Equal([Capability.PurchasingOperate], CapabilitiesOf(read));
        Assert.Equal(
            [Capability.PurchasingOperate, Capability.SuppliersManageGstDefaults],
            CapabilitiesOf(edit).Order().ToArray());
    }

    #endregion

    #region Class 3: the membership-not-required allowlist

    /// <summary>
    /// The marker appears on exactly the allowlisted endpoints - the reviewed exemption issue #523
    /// introduced, now folded into this test as class 3. Marking a new endpoint fails the build
    /// until the same pull request adds it here and a human sees it.
    /// </summary>
    [Fact]
    public void Only_allowlisted_endpoints_may_skip_the_membership_requirement()
    {
        var marked = Endpoints()
            .Where(HasMembershipNotRequiredMarker)
            .Select(endpoint => endpoint.Route)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(MembershipNotRequiredAllowlist.Order(StringComparer.Ordinal), marked);
    }

    #endregion

    #region Class 2 and 4 keep their boundaries

    /// <summary>
    /// The platform diagnostics surface is both halves together, and it declares no business
    /// capability. <c>PlatformAdminPolicy</c> is satisfied by a configured Entra <c>(tid, oid)</c>
    /// pair that is deliberately not a membership row - so a capability on that surface would be
    /// unsatisfiable for the only caller it exists for, and a diagnostics endpoint that also
    /// declared one would be inviting a business role into a cross-business path.
    /// </summary>
    [Fact]
    public void The_diagnostics_surface_declares_both_halves_and_no_capability()
    {
        var diagnostics = Endpoints().Where(HasPlatformDiagnosticsMarker).ToArray();

        Assert.NotEmpty(diagnostics);
        foreach (var endpoint in diagnostics)
        {
            Assert.True(
                HasPlatformAdminPolicy(endpoint),
                $"{endpoint.Route} carries the diagnostics marker without the platform-admin policy.");
            Assert.Empty(CapabilitiesOf(endpoint));
        }
    }

    /// <summary>
    /// <c>GET api/me/access</c> is its own class and must stay that way: it carries no capability,
    /// no marker and no named policy, only the requirements every business endpoint carries. A
    /// member who may not call it cannot be told what they may call.
    /// </summary>
    [Fact]
    public void The_own_access_endpoint_carries_no_capability_and_no_marker()
    {
        var endpoint = Endpoint(OwnAccessRoute, "GET");

        Assert.Empty(CapabilitiesOf(endpoint));
        Assert.False(HasMembershipNotRequiredMarker(endpoint));
        Assert.False(HasPlatformDiagnosticsMarker(endpoint));
        Assert.True(HasAttribute<AuthorizeAttribute>(endpoint));
        Assert.True(HasAttribute<RequiredScopeAttribute>(endpoint));
        Assert.False(HasAttribute<AllowAnonymousAttribute>(endpoint));
    }

    #endregion

    #region The pending list is gone

    /// <summary>
    /// Every controller in the API has at least one classified endpoint, which is the whole-surface
    /// form of the rule above: issue #525's <c>PendingClassification</c> list is deleted, so there
    /// is no longer anywhere for a controller to sit while nobody has decided what it requires.
    /// Asserted per controller rather than only per endpoint so that the failure message names the
    /// controller a reviewer has to go and look at.
    /// </summary>
    [Fact]
    public void Every_controller_in_the_api_is_classified()
    {
        var unclassified = Endpoints()
            .GroupBy(endpoint => endpoint.Controller.Name, StringComparer.Ordinal)
            .Where(controller => controller.All(endpoint => ClassesOf(endpoint).Count == 0))
            .Select(controller => controller.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unclassified.Length == 0,
            "Issue #526 removed the temporary pending list, so every controller declares how its "
                + "endpoints are authorized. Nothing may reintroduce the list. Unclassified "
                + $"controller(s): {string.Join(", ", unclassified)}.");
    }

    #endregion

    #region Reading the endpoints

    private sealed record ApiEndpoint(Type Controller, MethodInfo Method, string Route, string HttpMethod);

    /// <summary>
    /// Every action MVC would route: a public, declared instance method on a non-abstract
    /// controller that carries an HTTP method attribute. <c>[ApiController]</c> requires attribute
    /// routing, so an action without one is not reachable - and
    /// <see cref="No_public_controller_method_is_routed_without_an_http_method_attribute"/> proves
    /// no action escapes this list that way.
    /// </summary>
    private static List<ApiEndpoint> Endpoints() =>
        ApiAssembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetMethods(DeclaredActions)
                .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>()
                    .Select(http => new ApiEndpoint(
                        type,
                        method,
                        RouteOf(type, http),
                        http.HttpMethods.FirstOrDefault() ?? "GET"))))
            .ToList();

    /// <summary>
    /// The guard under <see cref="Endpoints"/>: no public action is left out of the classification
    /// because it has no HTTP method attribute. Such a method is unreachable under
    /// <c>[ApiController]</c>'s attribute routing, so finding one means either a dead public method
    /// or a routing convention this test does not model - and the second would be an endpoint
    /// nobody classified.
    /// </summary>
    [Fact]
    public void No_public_controller_method_is_routed_without_an_http_method_attribute()
    {
        var unrouted = ApiAssembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetMethods(DeclaredActions)
                .Where(method => !method.IsSpecialName)
                .Where(method => !method.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(method => $"{type.Name}.{method.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unrouted.Length == 0,
            "A public method on a controller must declare its HTTP method, so the "
                + "endpoint-classification rule above sees it. Offending method(s): "
                + $"{string.Join(", ", unrouted)}.");
    }

    private static ApiEndpoint Endpoint(string route, string httpMethod) =>
        Assert.Single(Endpoints().Where(endpoint =>
            string.Equals(endpoint.Route, route, StringComparison.Ordinal)
            && string.Equals(endpoint.HttpMethod, httpMethod, StringComparison.Ordinal)));

    /// <summary>
    /// The endpoint's route as a reviewer reads it in the controller: the controller's own
    /// <c>[Route]</c> template with the <c>[controller]</c> token expanded, joined to the HTTP
    /// method attribute's template.
    /// </summary>
    private static string RouteOf(Type controller, HttpMethodAttribute http)
    {
        var controllerTemplate = (controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty)
            .Replace(
                "[controller]",
                controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                    ? controller.Name[..^"Controller".Length]
                    : controller.Name,
                StringComparison.Ordinal);

        return string.Join('/', new[] { controllerTemplate, http.Template }
            .Where(segment => !string.IsNullOrEmpty(segment))
            .Select(segment => segment!.Trim('/')));
    }

    /// <summary>
    /// Which of the four classes an endpoint is in. A set rather than a single value, so
    /// <see cref="No_endpoint_is_in_more_than_one_class"/> can assert the exclusivity instead of
    /// this helper hiding it behind a first match.
    /// </summary>
    private static List<EndpointClass> ClassesOf(ApiEndpoint endpoint)
    {
        List<EndpointClass> classes = [];

        if (CapabilitiesOf(endpoint).Count > 0)
        {
            classes.Add(EndpointClass.CapabilityPolicy);
        }

        if (HasPlatformDiagnosticsMarker(endpoint) && HasPlatformAdminPolicy(endpoint))
        {
            classes.Add(EndpointClass.PlatformDiagnostics);
        }

        if (HasMembershipNotRequiredMarker(endpoint)
            && MembershipNotRequiredAllowlist.Contains(endpoint.Route, StringComparer.Ordinal))
        {
            classes.Add(EndpointClass.MembershipNotRequired);
        }

        if (string.Equals(endpoint.Route, OwnAccessRoute, StringComparison.Ordinal))
        {
            classes.Add(EndpointClass.OwnAccess);
        }

        return classes;
    }

    /// <summary>
    /// Every capability the endpoint requires: the action's own and its controller's, because
    /// authorization metadata accumulates down the endpoint and both are enforced.
    /// </summary>
    private static List<Capability> CapabilitiesOf(ApiEndpoint endpoint) =>
    [
        .. new[]
        {
            endpoint.Method.GetCustomAttribute<RequireCapabilityAttribute>(),
            endpoint.Controller.GetCustomAttribute<RequireCapabilityAttribute>(inherit: true),
        }
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Capability)
            .Distinct(),
    ];

    private static bool HasPlatformDiagnosticsMarker(ApiEndpoint endpoint) =>
        HasAttribute<PlatformDiagnosticsEndpointAttribute>(endpoint);

    private static bool HasMembershipNotRequiredMarker(ApiEndpoint endpoint) =>
        HasAttribute<MembershipNotRequiredEndpointAttribute>(endpoint);

    private static bool HasPlatformAdminPolicy(ApiEndpoint endpoint) =>
        endpoint.Method.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(endpoint.Controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Any(authorize => string.Equals(authorize.Policy, PlatformAdminPolicy.Name, StringComparison.Ordinal));

    /// <summary>
    /// Whether the action or its controller carries <typeparamref name="TAttribute"/>. Asked over
    /// the attribute <em>collection</em> rather than a single attribute, because
    /// <see cref="RequireCapabilityAttribute"/> is itself an <see cref="AuthorizeAttribute"/>: a
    /// controller with both <c>[Authorize]</c> and <c>[RequireCapability]</c> carries two of them,
    /// which the single-attribute lookup refuses to answer at all.
    /// </summary>
    private static bool HasAttribute<TAttribute>(ApiEndpoint endpoint)
        where TAttribute : Attribute =>
        endpoint.Method.GetCustomAttributes<TAttribute>().Any()
        || endpoint.Controller.GetCustomAttributes<TAttribute>(inherit: true).Any();

    #endregion
}
