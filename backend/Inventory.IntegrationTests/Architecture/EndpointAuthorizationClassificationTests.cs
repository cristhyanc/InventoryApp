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
/// (issue #525, one slice of #502).
///
/// <para>The reason this test exists is the failure mode it removes. Before capability policies, an
/// endpoint that declared nothing beyond <c>[Authorize]</c> was reachable by every active member of
/// a business, and a <em>new</em> endpoint that declared nothing was reachable the same way - open
/// by default, and invisibly so, because nothing in the diff said "this is open". This test turns
/// that into a build failure: an action that is in none of the four classes below, and whose
/// controller is not on the pending list, fails here until the same pull request classifies it.</para>
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
/// <para>Issue #525 left the Manager- and Owner-only controllers on a temporary
/// <c>PendingClassification</c> list. Issue #526 classified all twelve of them and deleted that
/// list, so the rule below holds for every action on every controller with no exception. There is
/// no list to add a controller to, and no such list may be reintroduced: a new endpoint fails the
/// build until the same pull request decides what it requires.</para>
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

    /// <summary>
    /// The five maintenance data-repair controllers (issue #526). They are named here so the test
    /// below can state the rule they share: every action of a repair surface - preview, dry run and
    /// apply alike - requires <see cref="Capability.DataRepair"/> and nothing weaker. A preview
    /// reads exactly the financial history its apply rewrites, so a role that may not run the
    /// repair may not project it either.
    /// </summary>
    private static readonly string[] DataRepairControllers =
    [
        "HistoricalGstClassificationController",
        "InventoryCostRepairsController",
        "InventoryCostTransitionsController",
        "NayaxSaleTimestampRepairsController",
        "SaleCostingController",
    ];

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
    /// exception and no pending list (issue #526 removed the last one). A new endpoint therefore
    /// cannot default to "every member may call it" - the build fails until somebody decides what
    /// it requires.
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
    /// The capabilities issue #526 applies to the Manager- and Owner-only surfaces, as that issue
    /// lists them, read back from the endpoints themselves. Like the row set above it is the
    /// acceptance criterion in one place, so a later change that moved a repair onto a weaker
    /// capability, or put a report on the import capability, fails here and not only in the
    /// per-role API tests.
    ///
    /// <para><c>api/nayax-sales-sync</c> and <c>api/data-quality/...</c> are the two endpoints the
    /// issue says follow the page that calls them. The sync is called by the home Dashboard before
    /// it loads Sites and Machines (issue #187), so it takes the Dashboard's own capability. No page
    /// calls the catalogue reconciliation at all, so it takes the Owner-only capability for the
    /// Nayax integration it reports on - see docs/architecture.md § Capability authorization on the
    /// Manager and Owner endpoints.</para>
    /// </summary>
    [Theory]
    [InlineData("api/operating-expenses", "GET", Capability.ExpensesManage)]
    [InlineData("api/operating-expenses", "POST", Capability.ExpensesManage)]
    [InlineData("api/operating-expenses/{id:int}", "GET", Capability.ExpensesManage)]
    [InlineData("api/operating-expenses/{id:int}", "PUT", Capability.ExpensesManage)]
    [InlineData("api/operating-expenses/{id:int}", "DELETE", Capability.ExpensesManage)]
    [InlineData("api/operating-expenses/{id:int}/attachment", "GET", Capability.ExpensesManage)]
    [InlineData("api/reports/bookkeeping", "GET", Capability.ReportsView)]
    [InlineData("api/reports/dashboard", "GET", Capability.ReportsView)]
    [InlineData("api/reports/transactions", "GET", Capability.ReportsView)]
    [InlineData("api/reports/{report}/export", "GET", Capability.ReportsView)]
    [InlineData("api/imports/products", "POST", Capability.ImportsRun)]
    [InlineData("api/imports/nayax-sales", "POST", Capability.ImportsRun)]
    [InlineData("api/imports/pending-xml", "POST", Capability.ImportsRun)]
    [InlineData("api/site-commissions", "GET", Capability.SiteCommissionsManage)]
    [InlineData("api/site-commissions/agreements", "GET", Capability.SiteCommissionsManage)]
    [InlineData("api/site-commissions/agreements", "POST", Capability.SiteCommissionsManage)]
    [InlineData("api/site-commissions/{siteId:long}/payments", "POST", Capability.SiteCommissionsManage)]
    [InlineData("api/settings/nayax-processing-fee-rates", "GET", Capability.IntegrationManage)]
    [InlineData("api/settings/nayax-processing-fee-rates", "POST", Capability.IntegrationManage)]
    [InlineData("api/data-quality/nayax-catalog-reconciliation", "GET", Capability.IntegrationManage)]
    [InlineData("api/settings/nayax-connection/test", "POST", Capability.IntegrationManage)]
    [InlineData("api/nayax-sales-sync", "POST", Capability.DashboardView)]
    [InlineData("api/admin/inventory-cost-repair/preview", "POST", Capability.DataRepair)]
    [InlineData("api/admin/inventory-cost-repair/apply", "POST", Capability.DataRepair)]
    [InlineData("api/admin/inventory-cost-transition/preview", "POST", Capability.DataRepair)]
    [InlineData("api/admin/inventory-cost-transition/apply-all", "POST", Capability.DataRepair)]
    [InlineData("api/admin/historical-gst-classification/preview", "POST", Capability.DataRepair)]
    [InlineData("api/admin/historical-gst-classification/apply", "POST", Capability.DataRepair)]
    [InlineData("api/admin/nayax-sale-timestamp-repair/preview", "POST", Capability.DataRepair)]
    [InlineData("api/admin/nayax-sale-timestamp-repair/apply", "POST", Capability.DataRepair)]
    [InlineData("api/sale-costing/backfill", "POST", Capability.DataRepair)]
    [InlineData("api/sale-costing/nayax-cost-backfill/dry-run", "POST", Capability.DataRepair)]
    [InlineData("api/sale-costing/nayax-cost-backfill/apply", "POST", Capability.DataRepair)]
    public void The_manager_and_owner_endpoints_declare_the_capability_issue_526_assigns_them(
        string route,
        string httpMethod,
        Capability capability)
    {
        // Asked over every action at the route rather than one, because two of them have a JSON and
        // a multipart overload on the same route and verb - and both of those are endpoints a
        // caller can reach, so both have to declare it.
        var endpoints = EndpointsAt(route, httpMethod);

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint => Assert.Contains(capability, CapabilitiesOf(endpoint)));
    }

    /// <summary>
    /// The member management surface of issue #533 and the invite links of issue #531, read back
    /// off their routes. Every action needs <c>Members.Manage</c>, which #328's table gives to an
    /// Owner alone - the two reads included, because the member list is the roster the changes are
    /// made from and carries every member's display name and email address, and the invite list is
    /// the record of who was offered access to the business. A later change that put either read on
    /// a weaker capability "so a Manager can see the team" fails here.
    /// </summary>
    [Theory]
    [InlineData("api/members", "GET")]
    [InlineData("api/members/{membershipId:int}/role", "PUT")]
    [InlineData("api/members/{membershipId:int}/revoke", "POST")]
    [InlineData("api/members/{membershipId:int}/reinstate", "POST")]
    [InlineData("api/members/invites", "GET")]
    [InlineData("api/members/invites", "POST")]
    [InlineData("api/members/invites/{inviteId:int}/cancel", "POST")]
    public void Every_member_management_endpoint_requires_only_the_members_capability(
        string route,
        string httpMethod) =>
        Assert.Equal([Capability.MembersManage], CapabilitiesOf(Endpoint(route, httpMethod)));

    /// <summary>
    /// The Owner's business settings (issue #548). The read is listed beside the write deliberately:
    /// it reports the business's contact address, ABN and GST registration, so it is restricted with
    /// the write rather than following <c>GET api/business/current</c>'s <c>Dashboard.View</c> - and
    /// a later change that moved either of them onto a capability every member holds fails here.
    /// </summary>
    [Theory]
    [InlineData("api/business/settings", "GET", Capability.BusinessManage)]
    [InlineData("api/business/settings", "PUT", Capability.BusinessManage)]
    public void The_business_settings_endpoints_require_business_manage(
        string route,
        string httpMethod,
        Capability capability) =>
        Assert.Contains(capability, CapabilitiesOf(Endpoint(route, httpMethod)));

    /// <summary>
    /// The contrast that makes the row above meaningful: the two surfaces on the one business
    /// controller declare different capabilities, so the settings endpoints cannot have inherited
    /// the shell read's rule and the shell read cannot have been narrowed to an Owner's.
    /// </summary>
    [Fact]
    public void The_business_controllers_shell_read_and_settings_surfaces_declare_different_capabilities()
    {
        Assert.Equal(
            [Capability.DashboardView],
            CapabilitiesOf(Endpoint("api/business/current", "GET")));
        Assert.Equal(
            [Capability.BusinessManage],
            CapabilitiesOf(Endpoint("api/business/settings", "PUT")));
    }

    /// <summary>
    /// A preview is as restricted as its apply. Every action of every data-repair controller
    /// requires <c>Data.Repair</c> and declares no second, weaker capability beside it: a preview
    /// and a dry run read exactly the financial history the apply rewrites, and for
    /// <c>api/sale-costing/nayax-cost-backfill</c> the difference between the two is a flag. A later
    /// change that put a preview on <c>Reports.View</c> to "let a Manager look" fails here.
    /// </summary>
    [Fact]
    public void Every_action_of_a_repair_controller_requires_only_the_data_repair_capability()
    {
        var repairs = Endpoints()
            .Where(endpoint => DataRepairControllers.Contains(endpoint.Controller.Name, StringComparer.Ordinal))
            .ToArray();

        Assert.Equal(DataRepairControllers.Length, repairs.Select(endpoint => endpoint.Controller.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var endpoint in repairs)
        {
            Assert.Equal([Capability.DataRepair], CapabilitiesOf(endpoint));
        }
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

    #region There is no pending list

    /// <summary>
    /// The same rule as <see cref="Every_endpoint_declares_one_of_the_four_authorization_classes"/>,
    /// asked one controller at a time, which is what issue #525's pending list used to say and what
    /// issue #526 turned into a standing fact: every controller in the API is classified end to end.
    /// It is kept beside the per-endpoint rule because this is the failure message a reviewer wants
    /// - the controller to go and look at, not a list of action names - and because a controller
    /// half of whose actions declare a capability is a worse state than one where none do.
    /// </summary>
    [Fact]
    public void Every_controller_in_the_api_is_classified_end_to_end()
    {
        var controllers = Endpoints()
            .GroupBy(endpoint => endpoint.Controller.Name, StringComparer.Ordinal)
            .Where(group => group.Any(endpoint => ClassesOf(endpoint).Count == 0))
            .Select(group => $"{group.Key} ({group.Count(endpoint => ClassesOf(endpoint).Count == 0)} of "
                + $"{group.Count()} action(s) unclassified)")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            controllers.Length == 0,
            "Issue #526 removed the last temporary pending list, so every controller declares how "
                + "each of its actions is authorized and no list exists to add one to. Declare the "
                + "capability the endpoint needs (AGENTS.md § Architecture rules, "
                + "docs/architecture.md § Capability authorization on the Manager and Owner "
                + $"endpoints). Unclassified controller(s): {string.Join(", ", controllers)}.");
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
        Assert.Single(EndpointsAt(route, httpMethod));

    /// <summary>
    /// Every action routed at one route and verb. Usually one, but a route that offers a JSON and a
    /// multipart overload has two, and each of them is separately reachable and separately
    /// authorized.
    /// </summary>
    private static List<ApiEndpoint> EndpointsAt(string route, string httpMethod) =>
        [.. Endpoints().Where(endpoint =>
            string.Equals(endpoint.Route, route, StringComparison.Ordinal)
            && string.Equals(endpoint.HttpMethod, httpMethod, StringComparison.Ordinal))];

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
