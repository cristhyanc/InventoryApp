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
/// endpoints named below, so marking a new one fails the build until the same pull request adds it
/// here and a human sees it. Issue #502 folds this list into its endpoint-classification test as
/// class 3 (<c>/api/me/account-state</c>, #504's invite redemption, #507's application endpoints).
/// </para>
///
/// <para>It also pins what the marker is never allowed to remove. A marked endpoint still requires
/// authentication and the delegated <c>access_as_user</c> scope: "membership not required" must not
/// drift into "anonymous", which is the one mistake that would turn an exemption into a public
/// endpoint.</para>
/// </summary>
public class MembershipNotRequiredEndpointAllowlistTests
{
    /// <summary>
    /// Every endpoint allowed to carry the marker, as its route template. Adding a line here is
    /// the reviewed exemption; see the class remarks before doing it.
    /// </summary>
    private static readonly string[] Allowlist = ["api/me/account-state"];

    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    private const BindingFlags DeclaredActions = BindingFlags.Public
        | BindingFlags.Instance
        | BindingFlags.DeclaredOnly;

    [Fact]
    public void Only_allowlisted_endpoints_may_skip_the_membership_requirement()
    {
        var marked = MarkedActions()
            .Select(action => RouteOf(action.Controller, action.Method))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Allowlist.Order(StringComparer.Ordinal), marked);
    }

    /// <summary>
    /// The allowlist is a list of endpoints that exist: an entry whose endpoint was renamed or
    /// removed would otherwise keep an exemption alive for a route nobody can see any more.
    /// </summary>
    [Fact]
    public void Every_allowlisted_endpoint_still_carries_the_marker()
    {
        var marked = MarkedActions().Select(action => RouteOf(action.Controller, action.Method)).ToList();

        Assert.All(Allowlist, route => Assert.Contains(route, marked));
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
            var route = RouteOf(controller, method);

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
            .SelectMany(type => type.GetMethods(DeclaredActions).Select(method => (Controller: type, Method: method)))
            .Where(action => HasAttribute<MembershipNotRequiredEndpointAttribute>(action.Controller, action.Method))
            .ToList();

    private static bool HasAttribute<TAttribute>(Type controller, MethodInfo method)
        where TAttribute : Attribute =>
        method.GetCustomAttribute<TAttribute>() is not null
        || controller.GetCustomAttribute<TAttribute>(inherit: true) is not null;

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
