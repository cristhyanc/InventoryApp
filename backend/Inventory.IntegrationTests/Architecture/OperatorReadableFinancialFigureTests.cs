using System.Collections;
using System.Reflection;
using Inventory.Application.Access;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Every revenue, profit, margin and inventory-value figure reachable by a role without
/// <c>Dashboard.ViewFinancials</c> is accounted for, by name, in one maintained list (issue #700).
///
/// <para>This is the standing guard behind the audit issues #527, #697, #698 and #700 carried out
/// one surface at a time. Each of those found the same failure mode: an endpoint an Operator holds
/// the capability for - correctly, because restocking and purchasing are what the capability is for
/// - carried a financial figure inside its response, where an <c>AuthorizeAttribute</c> cannot
/// reach. The figures were withheld and the endpoints kept. What none of those slices could do is
/// stop the <em>next</em> one appearing: a property added to <c>ProductResponse</c>,
/// <c>MachineResponse</c> or any type nested inside them is published to every caller of the
/// endpoint, and nothing in the diff says so.</para>
///
/// <para>So this test walks the response type of every endpoint an Operator can reach - from the
/// actual capability policies, never from a role name written here - collects every property whose
/// name matches the restricted vocabulary, and compares the result against
/// <see cref="AccountedFigures"/>. A new revenue, profit, margin or inventory-value property fails
/// the build until the same pull request records what happens to it: withheld from a caller without
/// the capability, or safe and why. It is an allow-list of <em>names</em>, deliberately: it cannot
/// prove a figure is actually withheld at runtime - the per-surface API tests
/// (<c>DashboardSummaryRedactionApiTests</c>, <c>SiteFinancialRedactionApiTests</c>,
/// <c>MachineAndProductFinancialRedactionApiTests</c> and
/// <c>StockAndPurchasingFinancialRedactionApiTests</c>) do that, over the real pipeline - but it is
/// the only thing that notices a figure nobody has considered at all.</para>
/// </summary>
public class OperatorReadableFinancialFigureTests
{
    /// <summary>
    /// The restricted vocabulary, as the recorded decision (Cristhyan, 15 October 2026) names it:
    /// <c>Dashboard.ViewFinancials</c> governs every sales, revenue, profit, margin,
    /// profitability-status, trend and inventory-value figure.
    ///
    /// <para>Matched as a case-insensitive substring of the property name, so <c>TodayRevenue</c>,
    /// <c>previousComparableWeekGrossRevenue</c> and <c>inventoryValueAfter</c> are all caught by
    /// one fragment each. Unit cost and selling price are deliberately <b>not</b> here: the same
    /// decision keeps <c>averageUnitCost</c>, <c>unitPrice</c>, <c>machinePrice</c>, <c>sitePrice</c>
    /// and a purchase's own line costs visible to an Operator, who needs them to restock, to price a
    /// slot and to record a purchase.</para>
    /// </summary>
    private static readonly string[] RestrictedNameFragments =
    [
        "Revenue",
        "Profit",
        "Margin",
        "InventoryValue",
        "Valuation",
        "Sales",
    ];

    /// <summary>
    /// What happens to one restricted figure on an Operator-readable response.
    /// </summary>
    private enum FigureOutcome
    {
        /// <summary>
        /// Withheld from a caller without <c>Dashboard.ViewFinancials</c>: either absent from the
        /// shape that caller is answered (issue #527's two published types) or serialized as an
        /// explicit <c>null</c>, never <c>0</c>.
        /// </summary>
        Withheld,

        /// <summary>
        /// Carries no amount, so there is nothing to withhold: a completeness flag, a count, or a
        /// figure the recorded decision keeps visible. The reason is part of the entry.
        /// </summary>
        NotAFinancialAmount,
    }

    /// <summary>
    /// Every restricted-named property reachable from an Operator-readable response, and what this
    /// repository has decided about it. The list is the review surface: adding a line is the
    /// decision, and the pull request that adds the property is where it is made.
    /// </summary>
    private static readonly RestrictedFigure[] AccountedFigures =
    [
        // GET api/dashboard/summary (issue #527). A caller without the capability is answered
        // RedactedDashboardSummaryDto, which has no salesThisWeek object and whose inventory card
        // has no valuation property at all - absent, not null.
        new(
            "DashboardSummaryDto.SalesThisWeek",
            FigureOutcome.Withheld,
            "Issue #527: the shape a caller without the capability is answered, "
                + "RedactedDashboardSummaryDto, does not declare the property at all - the object "
                + "is absent from the JSON rather than null or 0."),
        new(
            "SupportViewDashboardSummaryDto.SalesThisWeek",
            FigureOutcome.Withheld,
            "Issue #552's support-view shape, which is only ever built for a request whose fixed "
                + "capability set grants Dashboard.ViewFinancials (issue #551), read through the "
                + "same CurrentCapabilities rather than assumed."),
        new(
            "DashboardSalesThisWeekDto.Sales",
            FigureOutcome.Withheld,
            "Issue #527: the whole salesThisWeek object is absent from RedactedDashboardSummaryDto, "
                + "and GetDashboardSummary makes no week-to-date read for such a caller."),
        new(
            "DashboardSalesThisWeekDto.ComparisonSales",
            FigureOutcome.Withheld,
            "Issue #527: part of the same withheld salesThisWeek object."),
        new(
            "DashboardInventorySummaryDto.InventoryValueAtCost",
            FigureOutcome.Withheld,
            "Issue #527: RedactedDashboardInventorySummaryDto does not declare the property."),
        new(
            "DashboardInventorySummaryDto.IsInventoryValueComplete",
            FigureOutcome.NotAFinancialAmount,
            "A costing data-quality flag, not an amount. Issue #527 keeps it on the redacted card: "
                + "a role that stocks the machines may know the valuation is incomplete."),
        new(
            "RedactedDashboardInventorySummaryDto.IsInventoryValueComplete",
            FigureOutcome.NotAFinancialAmount,
            "The same flag on the redacted shape, for the same reason."),

        // GET api/sites and GET api/sites/{id}/products (issue #697).
        new(
            "SiteSummaryDto.TodayRevenue",
            FigureOutcome.Withheld,
            "Issue #697: null without the capability, and GetSiteSummaries reads no completed sale."),
        new(
            "SiteSummaryDto.CurrentWeekRevenue",
            FigureOutcome.Withheld,
            "Issue #697, as above."),
        new(
            "SiteSummaryDto.PreviousComparableWeekRevenue",
            FigureOutcome.Withheld,
            "Issue #697, as above."),
        new(
            "SiteProductDto.EstimatedCardProfit",
            FigureOutcome.Withheld,
            "Issue #697: null without the capability, and GetSiteProducts resolves neither the card "
                + "commission nor the processing fee the estimate needs."),

        // GET api/machines and GET api/machines/{id} (issue #698): six periods of gross revenue,
        // the six matching direct-profit figures and the profitability status.
        new("MachineResponse.TodayGrossRevenue", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.CurrentWeekGrossRevenue", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.LastWeekGrossRevenue", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.TwoWeeksAgoGrossRevenue", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.PreviousComparableWeekGrossRevenue", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.MonthToDateGrossRevenue", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.TodayDirectProfit", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.CurrentWeekDirectProfit", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.LastWeekDirectProfit", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.TwoWeeksAgoDirectProfit", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.PreviousComparableWeekDirectProfit", FigureOutcome.Withheld, "Issue #698."),
        new("MachineResponse.MonthToDateDirectProfit", FigureOutcome.Withheld, "Issue #698."),
        new(
            "MachineResponse.ProfitabilityStatus",
            FigureOutcome.Withheld,
            "Issue #698: the status names why a profit is unavailable, which is a statement about "
                + "the machine's profitability."),

        // The catalogue product, on every endpoint that serves one (issue #698).
        new(
            "ProductResponse.InventoryValue",
            FigureOutcome.Withheld,
            "Issue #698 for the product and machine endpoints, and issue #700 for the nested "
                + "catalogue snapshot a purchase line and a supplier-order line carry: summing it "
                + "recovers the valuation the Dashboard withholds."),

        // A stock movement, on every endpoint that serves one (issue #700).
        new(
            "ProductStockAdjustmentResponse.InventoryValueAfter",
            FigureOutcome.Withheld,
            "Issue #700: the movement's resulting inventory value at cost, which on the most recent "
                + "movement is the product's current valuation. Withheld on the product endpoints "
                + "and on api/products/{id}/stock alike."),
        new(
            "StockHistoryEntryResponse.InventoryValueAfter",
            FigureOutcome.Withheld,
            "Issue #700: the same figure on GET api/stock-history."),
    ];

    /// <summary>
    /// The Operator-readable endpoints whose response body this scan cannot inspect, because their
    /// declared return type is an <see cref="IActionResult"/> rather than a payload type.
    ///
    /// <para>The list exists so the guard's blind spots are reviewed rather than invisible. Every
    /// entry here is an endpoint that answers a status code, a stored document stream or a
    /// <c>ProblemDetails</c>, and none of them constructs a financial figure. An endpoint that
    /// starts returning a payload through <see cref="IActionResult"/> fails this test until
    /// somebody decides which of the two lists it belongs on.</para>
    /// </summary>
    private static readonly string[] UninspectableOperatorEndpoints =
    [
        // Status codes and ProblemDetails only.
        //
        // The two category writes joined this list with issue #736, which gave the categories
        // resource a write surface: each answers a status code, or a refusal sentence saying what a
        // provider manages or how many products use the category, and neither constructs a
        // financial figure. The create actions of both resources are deliberately *not* here -
        // POST api/Products and POST api/Categories declare their response payload, so the scan
        // above inspects them like any other read.
        "DELETE api/Categories/{id:long}",
        "DELETE api/Products/{id:long}",
        "DELETE api/Suppliers/{id:int}",
        "DELETE api/purchases/{id:int}",
        "POST api/SupplierOrders/{id:int}/cancel",
        "POST api/nayax-sales-sync",
        "PUT api/Categories/{id:long}",
        "PUT api/Products/{id:long}",
        "PUT api/Products/{id:long}/gst-rule",
        "PUT api/Suppliers/{id:int}",

        // The purchase's stored document: a file stream with its own content type and name, and
        // never a projection of the purchase's figures.
        "GET api/purchases/{id:int}/file",
    ];

    /// <summary>One accounted figure: where it is, what happens to it and why.</summary>
    private sealed record RestrictedFigure(string Name, FigureOutcome Outcome, string Reason);

    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    private static readonly Assembly[] ModelAssemblies =
    [
        ApiAssembly,
        typeof(Capability).Assembly,
        typeof(BusinessRole).Assembly,
    ];

    private const BindingFlags ActionCandidates = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// The rule itself. A restricted-named property reachable from an Operator-readable response is
    /// either on the list with a decision, or it is a figure nobody has considered.
    /// </summary>
    [Fact]
    public void Every_restricted_figure_on_an_operator_readable_response_is_accounted_for()
    {
        var discovered = DiscoverRestrictedFigureNames();
        var accounted = AccountedFigures.Select(figure => figure.Name).Order(StringComparer.Ordinal).ToArray();

        var unaccounted = discovered.Except(accounted, StringComparer.Ordinal).ToArray();
        var stale = accounted.Except(discovered, StringComparer.Ordinal).ToArray();

        Assert.True(
            unaccounted.Length == 0,
            "A revenue, profit, margin, sales or inventory-value property is reachable from an "
                + "endpoint an Operator can call, and nothing in this repository has decided what "
                + "happens to it. Withhold it from a caller without Dashboard.ViewFinancials "
                + "(null, never 0 - see docs/architecture.md § Operator-readable financial "
                + "disclosure) and record it in AccountedFigures, or record why it is not a "
                + $"financial amount. Unaccounted figure(s): {string.Join(", ", unaccounted)}.");

        Assert.True(
            stale.Length == 0,
            "AccountedFigures names a property that no Operator-readable response carries any "
                + "more. Remove the line, so the list stays the audit it claims to be. Stale "
                + $"entry/entries: {string.Join(", ", stale)}.");
    }

    /// <summary>
    /// Every accounted figure carries a reason, and a withheld one is the default answer. An entry
    /// with no reason is a line somebody added to make the build pass.
    /// </summary>
    [Fact]
    public void Every_accounted_figure_states_why()
    {
        Assert.NotEmpty(AccountedFigures);
        Assert.All(AccountedFigures, figure => Assert.False(string.IsNullOrWhiteSpace(figure.Reason)));
        Assert.All(
            AccountedFigures,
            figure => Assert.Contains(figure.Outcome, Enum.GetValues<FigureOutcome>()));
        Assert.Distinct(AccountedFigures.Select(figure => figure.Name));
    }

    /// <summary>
    /// The scan's blind spots are exactly the reviewed list. An Operator-readable endpoint whose
    /// payload this test cannot see is an endpoint the guard above does not cover, so it is named
    /// rather than skipped.
    /// </summary>
    [Fact]
    public void The_uninspectable_operator_endpoints_are_exactly_the_reviewed_list()
    {
        var uninspectable = OperatorReadableEndpoints()
            .Where(endpoint => PayloadTypeOf(endpoint.Method) is null)
            .Select(endpoint => $"{endpoint.HttpMethod} {endpoint.Route}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var reviewed = UninspectableOperatorEndpoints.Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            uninspectable.SequenceEqual(reviewed, StringComparer.Ordinal),
            "The Operator-readable endpoints whose payload this scan cannot inspect must be "
                + "exactly the reviewed list, so the guard's blind spots stay visible. "
                + $"Found: {string.Join(", ", uninspectable)}. "
                + $"Reviewed: {string.Join(", ", reviewed)}.");
    }

    /// <summary>
    /// The scan is taken over the capabilities an Operator actually holds, read from the one
    /// <c>RoleCapabilities</c> table. Written out here as issue #328's table states them, so a
    /// change to that table that quietly narrowed this scan - and with it the set of endpoints the
    /// guard above covers - fails here.
    /// </summary>
    [Fact]
    public void An_operator_holds_exactly_the_four_capabilities_this_scan_is_taken_over()
    {
        Assert.Equal(
            [
                Capability.DashboardView,
                Capability.PickListView,
                Capability.InventoryOperate,
                Capability.PurchasingOperate,
            ],
            RoleCapabilities.For(BusinessRole.Operator));
    }

    /// <summary>
    /// The scan covers endpoints, so it has to find some. A reflection walk that silently matched
    /// nothing would pass every assertion above.
    /// </summary>
    [Fact]
    public void The_scan_reaches_the_operator_readable_endpoints()
    {
        var endpoints = OperatorReadableEndpoints();

        Assert.NotEmpty(endpoints);
        Assert.Contains(endpoints, endpoint => endpoint.Route == "api/Products" && endpoint.HttpMethod == "GET");
        Assert.Contains(endpoints, endpoint => endpoint.Route == "api/stock-history" && endpoint.HttpMethod == "GET");
        Assert.Contains(endpoints, endpoint => endpoint.Route == "api/purchases" && endpoint.HttpMethod == "GET");
        Assert.Contains(endpoints, endpoint => endpoint.Route == "api/SupplierOrders" && endpoint.HttpMethod == "GET");

        // The one action on an Operator-readable controller that an Operator cannot call: it
        // declares Dashboard.ViewFinancials of its own (issue #681), so the valuation it reports is
        // deliberately outside this scan.
        Assert.DoesNotContain(
            endpoints,
            endpoint => endpoint.Route == "api/Products/inventory-value-summary");
    }

    #region The scan

    private sealed record ApiEndpoint(Type Controller, MethodInfo Method, string Route, string HttpMethod);

    /// <summary>
    /// Every restricted-named property reachable from the payload of an Operator-readable endpoint,
    /// as <c>DeclaringType.PropertyName</c>.
    /// </summary>
    private static string[] DiscoverRestrictedFigureNames()
    {
        HashSet<Type> visited = [];
        HashSet<string> found = new(StringComparer.Ordinal);

        foreach (var endpoint in OperatorReadableEndpoints())
        {
            if (PayloadTypeOf(endpoint.Method) is { } payload)
            {
                Walk(payload, visited, found);
            }
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Collects the restricted-named properties of one payload type and recurses into every nested
    /// model type it carries. An interface or abstract type is walked together with its concrete
    /// implementations, because that is what MVC serializes: <c>GET api/dashboard/summary</c>
    /// declares <c>IDashboardSummaryDto</c> and returns one of three records (issue #527).
    /// </summary>
    private static void Walk(Type type, HashSet<Type> visited, HashSet<string> found)
    {
        if (!visited.Add(type))
        {
            return;
        }

        foreach (var implementation in ImplementationsOf(type))
        {
            Walk(implementation, visited, found);
        }

        foreach (var property in type.GetProperties(ActionCandidates).Where(property => property.CanRead))
        {
            if (RestrictedNameFragments.Any(fragment =>
                property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add($"{type.Name}.{property.Name}");
            }

            if (ModelTypeOf(property.PropertyType) is { } nested)
            {
                Walk(nested, visited, found);
            }
        }
    }

    /// <summary>
    /// The concrete types in this application that implement an abstract payload contract. Empty
    /// for a concrete type, which is the ordinary case.
    /// </summary>
    private static IEnumerable<Type> ImplementationsOf(Type type) =>
        type.IsInterface || type.IsAbstract
            ? ModelAssemblies
                .SelectMany(assembly => assembly.GetTypes())
                .Where(candidate => !candidate.IsAbstract && !candidate.IsInterface && type.IsAssignableFrom(candidate))
            : [];

    /// <summary>
    /// The model type behind a declared property or return type - the element type of a collection,
    /// the underlying type of a <see cref="Nullable{T}"/> - or <see langword="null"/> when it is a
    /// scalar, a framework type or anything else this application does not own. Only types declared
    /// in this application's own assemblies are walked: a <c>DateTime</c> or a
    /// <c>FrozenDictionary</c> has no business figure inside it and walking the framework would
    /// never terminate usefully.
    /// </summary>
    private static Type? ModelTypeOf(Type type)
    {
        var candidate = Nullable.GetUnderlyingType(type) ?? type;

        if (candidate.IsArray)
        {
            return candidate.GetElementType() is { } element ? ModelTypeOf(element) : null;
        }

        if (candidate.IsGenericType && typeof(IEnumerable).IsAssignableFrom(candidate))
        {
            var arguments = candidate.GetGenericArguments();
            return arguments.Length == 1 ? ModelTypeOf(arguments[0]) : null;
        }

        if (candidate.IsPrimitive || candidate.IsEnum || candidate == typeof(string))
        {
            return null;
        }

        return ModelAssemblies.Contains(candidate.Assembly) ? candidate : null;
    }

    /// <summary>
    /// The payload type an action serializes, or <see langword="null"/> when its declared return
    /// type does not name one. <c>Task&lt;T&gt;</c> and <c>ActionResult&lt;T&gt;</c> are unwrapped;
    /// a bare <c>IActionResult</c> is not a payload type and is counted as uninspectable by
    /// <see cref="The_uninspectable_operator_endpoints_are_exactly_the_reviewed_list"/>.
    /// </summary>
    private static Type? PayloadTypeOf(MethodInfo action)
    {
        var returned = action.ReturnType;

        if (returned.IsGenericType
            && (returned.GetGenericTypeDefinition() == typeof(Task<>)
                || returned.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            returned = returned.GetGenericArguments()[0];
        }

        if (returned.IsGenericType && returned.GetGenericTypeDefinition() == typeof(ActionResult<>))
        {
            returned = returned.GetGenericArguments()[0];
        }

        if (returned == typeof(void)
            || returned == typeof(Task)
            || typeof(IActionResult).IsAssignableFrom(returned))
        {
            return null;
        }

        return ModelTypeOf(returned);
    }

    /// <summary>
    /// Every endpoint a member holding only an Operator's capabilities can reach: one whose
    /// declared capability requirements are all granted by that role, plus the two endpoint classes
    /// that declare no capability at all and that any signed-in caller - an Operator included -
    /// reaches (<c>docs/architecture.md</c> § Capability authorization, classes 3 and 4).
    ///
    /// <para>The capabilities come from the endpoints' own attributes and
    /// <see cref="RoleCapabilities"/>, so this set follows a change to either without being
    /// restated. Requirements accumulate down an endpoint, which is why <em>every</em> declared
    /// capability has to be granted: <c>GET api/products/inventory-value-summary</c> carries the
    /// catalogue's <c>Inventory.Operate</c> and its own <c>Dashboard.ViewFinancials</c> (issue
    /// #681), and an Operator cannot call it.</para>
    /// </summary>
    private static List<ApiEndpoint> OperatorReadableEndpoints()
    {
        var operatorCapabilities = RoleCapabilities.For(BusinessRole.Operator);

        return
        [
            .. Endpoints().Where(endpoint =>
            {
                var required = CapabilitiesOf(endpoint);

                return required.Count == 0
                    ? DeclaresNoCapabilityByDesign(endpoint)
                    : required.All(operatorCapabilities.Contains);
            }),
        ];
    }

    /// <summary>
    /// Whether an endpoint that declares no capability is one of the two reviewed classes that
    /// deliberately declare none - and so is reachable by any signed-in caller - rather than the
    /// platform-admin surface, which no member of a business can reach at all.
    /// <c>EndpointAuthorizationClassificationTests</c> is what proves there is no fourth answer.
    /// </summary>
    private static bool DeclaresNoCapabilityByDesign(ApiEndpoint endpoint) =>
        endpoint.Method.GetCustomAttributes<MembershipNotRequiredEndpointAttribute>().Any()
        || endpoint.Controller.GetCustomAttributes<MembershipNotRequiredEndpointAttribute>(inherit: true).Any()
        || string.Equals(endpoint.Route, "api/me/access", StringComparison.Ordinal);

    private static List<ApiEndpoint> Endpoints() =>
        [
            .. ApiAssembly.GetTypes()
                .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
                .SelectMany(type => type.GetMethods(ActionCandidates)
                    .Where(method => method.DeclaringType?.Assembly == ApiAssembly)
                    .Where(method => !method.IsSpecialName)
                    .Where(method => !method.GetCustomAttributes<NonActionAttribute>().Any())
                    .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>()
                        .Select(http => new ApiEndpoint(
                            type,
                            method,
                            RouteOf(type, http),
                            http.HttpMethods.FirstOrDefault() ?? "GET")))),
        ];

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

    /// <summary>
    /// The endpoint's route as a reviewer reads it in the controller, the same spelling
    /// <c>EndpointAuthorizationClassificationTests</c> uses.
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

    #endregion
}
