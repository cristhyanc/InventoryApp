using System.Security.Claims;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth;
using InventoryApi.Auth.PlatformAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The membership bypass, exercised directly on <see cref="BusinessScopeMiddleware"/> (issue #336).
///
/// <para>The HTTP tests prove the outcome through the whole pipeline; these prove <em>why</em> it is
/// the outcome. The middleware re-evaluates the platform-admin policy itself rather than trusting
/// that the authorization middleware ran before it, and that distinction is invisible end-to-end
/// because both mechanisms are in place there. Here the authorization service is a stub, so the
/// middleware's own decision is the only one being made - which is exactly the situation a future
/// pipeline edit could create by accident.</para>
///
/// <para>The other assertion worth making here is what a bypassed request carries: a
/// <em>denied</em> scope. The bypass is of the membership requirement, not of tenant filtering, so
/// a diagnostics request still reads nothing at all through <c>AppDbContext</c>.</para>
/// </summary>
public class BusinessScopeMiddlewarePlatformDiagnosticsTests
{
    [Fact]
    public async Task A_platform_admin_on_a_diagnostics_endpoint_proceeds_without_a_membership()
    {
        var context = AuthenticatedRequest(platformDiagnosticsEndpoint: true);
        var scope = new BusinessScope();

        var reached = await InvokeAsync(
            context,
            scope,
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing),
            policySucceeds: true);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>
    /// The scope a bypassed request runs with is the fail-closed one, not the unscoped one: the
    /// tenant query filters and <c>SaveChanges</c> enforcement stay in force, so the diagnostics
    /// request reads and writes nothing through the EF model.
    /// </summary>
    [Fact]
    public async Task A_bypassed_request_still_carries_a_denied_scope_and_never_an_unscoped_one()
    {
        var context = AuthenticatedRequest(platformDiagnosticsEndpoint: true);
        var scope = new BusinessScope();

        await InvokeAsync(
            context,
            scope,
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing),
            policySucceeds: true);

        Assert.Null(scope.BusinessId);
        Assert.NotEqual(BusinessScopeState.Unscoped, scope.State);
    }

    /// <summary>
    /// The independent re-check. The caller here <em>has</em> a usable membership, so the ordinary
    /// path would have let it through - but the endpoint is a diagnostics endpoint and the policy
    /// refuses, so the middleware refuses rather than falling back to membership.
    /// </summary>
    [Fact]
    public async Task A_business_member_is_refused_on_a_diagnostics_endpoint_even_with_a_usable_membership()
    {
        var context = AuthenticatedRequest(platformDiagnosticsEndpoint: true);

        var reached = await InvokeAsync(
            context,
            new BusinessScope(),
            BusinessMembershipResolution.Resolved(BusinessId.From(1)),
            policySucceeds: false);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    /// <summary>
    /// A caller the policy would accept must not get a membership bypass on an endpoint that is not
    /// marked as part of the diagnostics surface: the marker decides eligibility, and the policy
    /// decides the bypass, and both are required.
    /// </summary>
    [Fact]
    public async Task A_platform_admin_on_an_ordinary_endpoint_is_still_refused_without_a_membership()
    {
        var context = AuthenticatedRequest(platformDiagnosticsEndpoint: false);

        var reached = await InvokeAsync(
            context,
            new BusinessScope(),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing),
            policySucceeds: true);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task An_ordinary_member_on_an_ordinary_endpoint_is_scoped_exactly_as_before()
    {
        var context = AuthenticatedRequest(platformDiagnosticsEndpoint: false);
        var scope = new BusinessScope();

        var reached = await InvokeAsync(
            context,
            scope,
            BusinessMembershipResolution.Resolved(BusinessId.From(7)),
            policySucceeds: false);

        Assert.True(reached);
        Assert.Equal(7, scope.BusinessId);
    }

    /// <summary>
    /// An anonymous request is left to the authentication middleware's 401, on a diagnostics
    /// endpoint exactly as everywhere else: this change must not turn "not signed in" into
    /// "signed in but refused".
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_to_a_diagnostics_endpoint_is_left_alone()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(Endpoint(platformDiagnosticsEndpoint: true));

        var reached = await InvokeAsync(
            context,
            new BusinessScope(),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.NotAuthenticated),
            policySucceeds: false);

        Assert.True(reached);
    }

    private static async Task<bool> InvokeAsync(
        HttpContext context,
        BusinessScope scope,
        BusinessMembershipResolution resolution,
        bool policySucceeds)
    {
        var reached = false;
        var middleware = new BusinessScopeMiddleware(
            _ =>
            {
                reached = true;
                return Task.CompletedTask;
            },
            NullLogger<BusinessScopeMiddleware>.Instance);

        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            new StubCurrentBusinessProvider(resolution),
            scope,
            new StubAuthorizationService(policySucceeds));

        return reached;
    }

    private static DefaultHttpContext AuthenticatedRequest(bool platformDiagnosticsEndpoint)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tid", "11111111-1111-1111-1111-111111111111")],
                authenticationType: "Test")),
        };

        context.SetEndpoint(Endpoint(platformDiagnosticsEndpoint));

        return context;
    }

    private static Endpoint Endpoint(bool platformDiagnosticsEndpoint) =>
        new(
            _ => Task.CompletedTask,
            platformDiagnosticsEndpoint
                ? new EndpointMetadataCollection(new PlatformDiagnosticsEndpointAttribute())
                : EndpointMetadataCollection.Empty,
            "test");

    private sealed class StubCurrentBusinessProvider(BusinessMembershipResolution resolution)
        : ICurrentBusinessProvider
    {
        public Task<BusinessMembershipResolution> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(resolution);

        public Task<BusinessId> RequireBusinessIdAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Answers the policy question and nothing else, so the middleware's own re-check is the only
    /// authorisation happening in these tests.
    /// </summary>
    private sealed class StubAuthorizationService(bool succeeds) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(succeeds ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
        {
            Assert.Equal(PlatformAdminPolicy.Name, policyName);
            return Task.FromResult(succeeds ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
    }
}
