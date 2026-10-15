using System.Reflection;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Identity.Web.Resource;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// The reviewed allowlist for <see cref="MembershipNotRequiredEndpointAttribute"/> (issue #523).
///
/// <para>The membership requirement is what makes an authenticated caller a caller of <em>one</em>
/// business's API, so an endpoint that leaves it behind is a boundary decision rather than a
/// convenience. This test is how that decision stays reviewed: the marker may appear only on the
/// allowlisted endpoints, so marking a new one fails the build until the same pull request adds it
/// to the list and a human sees it.</para>
///
/// <para>Issue #525 folded that list into
/// <see cref="EndpointAuthorizationClassificationTests.MembershipNotRequiredAllowlist"/> as class 3
/// of the endpoint-classification test, which is where the marker now sits beside the other three
/// ways an endpoint may be authorized. This test reads the same single list rather than keeping a
/// second copy that could disagree with it, and keeps the assertions that are about the marker
/// itself.</para>
///
/// <para>Chief among those: what the marker is never allowed to remove. A marked endpoint still
/// requires authentication and the delegated <c>access_as_user</c> scope - "membership not
/// required" must not drift into "anonymous", which is the one mistake that would turn an exemption
/// into a public endpoint.</para>
/// </summary>
public class MembershipNotRequiredEndpointAllowlistTests
{
    /// <summary>
    /// Every endpoint allowed to carry the marker, as its route template and HTTP method - the one
    /// list, owned by the endpoint-classification test. Adding a line there is the reviewed
    /// exemption; see that test's remarks before doing it. Since issue #681 an entry names the
    /// method as well as the route, because an exemption belongs to one endpoint.
    /// </summary>
    private static readonly EndpointAuthorizationClassificationTests.MembershipNotRequiredEndpoint[] Allowlist =
        EndpointAuthorizationClassificationTests.MembershipNotRequiredAllowlist;

    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    /// <summary>
    /// Inherited actions included (issue #681), for the reason the classification test gives: an
    /// action declared on a base controller in this assembly is routed by its derived controller, so
    /// a marker on it has to be visible here too.
    /// </summary>
    private const BindingFlags ActionCandidates = BindingFlags.Public | BindingFlags.Instance;

    [Fact]
    public void Only_allowlisted_endpoints_may_skip_the_membership_requirement()
    {
        var marked = MarkedActions()
            .Select(action => Endpoint(action.Controller, action.Method).ToString())
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Allowlist.Select(entry => entry.ToString()).Order(StringComparer.Ordinal), marked);
    }

    /// <summary>
    /// The allowlist is a list of endpoints that exist: an entry whose endpoint was renamed, removed
    /// or moved to another HTTP method would otherwise keep an exemption alive for an endpoint nobody
    /// can see any more.
    /// </summary>
    [Fact]
    public void Every_allowlisted_endpoint_still_carries_the_marker()
    {
        var marked = MarkedActions().Select(action => Endpoint(action.Controller, action.Method)).ToList();

        Assert.All(Allowlist, entry => Assert.Contains(entry, marked));
    }

    /// <summary>
    /// Membership not required is not authentication not required. A marked endpoint keeps
    /// <c>[Authorize]</c> and the delegated scope every other endpoint carries, and must never
    /// carry <c>[AllowAnonymous]</c>.
    /// </summary>
    [Fact]
    public void A_marked_endpoint_still_requires_authentication_and_the_delegated_scope()
    {
        foreach (var (controller, method) in MarkedActions())
        {
            var route = Endpoint(controller, method).ToString();

            Assert.True(
                HasAttribute<AuthorizeAttribute>(controller, method),
                $"{route} skips the membership requirement without requiring authentication.");
            Assert.True(
                HasAttribute<RequiredScopeAttribute>(controller, method),
                $"{route} skips the membership requirement without requiring the delegated scope.");
            Assert.False(
                HasAttribute<AllowAnonymousAttribute>(controller, method),
                $"{route} skips the membership requirement and allows anonymous callers.");
        }
    }

    private static List<(Type Controller, MethodInfo Method)> MarkedActions() =>
        ApiAssembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetMethods(ActionCandidates)
                .Where(method => method.DeclaringType is { } declaring
                    && declaring.Assembly == ApiAssembly
                    && typeof(ControllerBase).IsAssignableFrom(declaring))
                .Where(method => !method.IsSpecialName)
                .Where(method => !method.GetCustomAttributes<NonActionAttribute>().Any())
                .Select(method => (Controller: type, Method: method)))
            .Where(action => HasAttribute<MembershipNotRequiredEndpointAttribute>(action.Controller, action.Method))
            .Where(action => action.Method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();

    /// <summary>
    /// Asked over the attribute collection rather than a single attribute, because
    /// <c>RequireCapabilityAttribute</c> (issue #525) is itself an <see cref="AuthorizeAttribute"/>:
    /// a controller carrying both <c>[Authorize]</c> and <c>[RequireCapability]</c> has two of them,
    /// and the single-attribute lookup refuses to answer at all rather than returning one.
    /// </summary>
    private static bool HasAttribute<TAttribute>(Type controller, MethodInfo method)
        where TAttribute : Attribute =>
        method.GetCustomAttributes<TAttribute>().Any()
        || controller.GetCustomAttributes<TAttribute>(inherit: true).Any();

    /// <summary>
    /// The endpoint as the allowlist states it: the route template and the HTTP method (issue #681).
    /// </summary>
    private static EndpointAuthorizationClassificationTests.MembershipNotRequiredEndpoint Endpoint(
        Type controller,
        MethodInfo method) =>
        new(
            RouteOf(controller, method),
            method.GetCustomAttributes<HttpMethodAttribute>()
                .SelectMany(attribute => attribute.HttpMethods)
                .FirstOrDefault() ?? "GET");

    /// <summary>
    /// The endpoint's route as a reviewer reads it in the controller: the controller's own
    /// <c>[Route]</c> template, with the <c>[controller]</c> token expanded, joined to the HTTP
    /// method attribute's template.
    /// </summary>
    private static string RouteOf(Type controller, MethodInfo method)
    {
        var controllerTemplate = (controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty)
            .Replace(
                "[controller]",
                controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                    ? controller.Name[..^"Controller".Length]
                    : controller.Name,
                StringComparison.Ordinal);

        var actionTemplate = method.GetCustomAttributes<HttpMethodAttribute>()
            .Select(attribute => attribute.Template)
            .FirstOrDefault(template => !string.IsNullOrEmpty(template));

        return string.Join('/', new[] { controllerTemplate, actionTemplate }
            .Where(segment => !string.IsNullOrEmpty(segment))
            .Select(segment => segment!.Trim('/')));
    }
}
