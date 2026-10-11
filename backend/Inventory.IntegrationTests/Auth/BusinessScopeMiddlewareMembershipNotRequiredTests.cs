using System.Security.Claims;
using Inventory.Application.Businesses;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth;
using InventoryApi.Auth.PlatformAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The second endpoint marker <see cref="BusinessScopeMiddleware"/> honours (issue #523):
/// <see cref="MembershipNotRequiredEndpointAttribute"/>, which lets a signed-in person reach the
/// account-state endpoint without a membership - the one thing a person who has no usable
/// membership needs to be able to ask.
///
/// <para>What these tests pin down is how little the marker does. It is eligibility, not
/// permission: it applies to the endpoint carrying it and nowhere else, the request still runs
/// with a <em>denied</em> business scope so it reads nothing through <c>AppDbContext</c>, and an
/// unauthenticated request is still left to the authentication middleware's <c>401</c>.</para>
/// </summary>
public class BusinessScopeMiddlewareMembershipNotRequiredTests
{
    /// <summary>
    /// The reason the marker exists: a caller the membership rule refuses - here, no membership at
    /// all - reaches the marked endpoint instead of the public 403.
    /// </summary>
    [Theory]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.MembershipInactive)]
    [InlineData(BusinessAccessDenialReason.MembershipAmbiguous)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    [InlineData(BusinessAccessDenialReason.UnidentifiableActor)]
    public async Task A_caller_with_no_usable_membership_reaches_a_marked_endpoint(
        BusinessAccessDenialReason reason)
    {
        var context = AuthenticatedRequest(membershipNotRequiredEndpoint: true);

        var reached = await InvokeAsync(context, new BusinessScope(), BusinessMembershipResolution.Denied(reason));

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>
    /// The marker bypasses the membership requirement and nothing else. The request carries the
    /// fail-closed scope, exactly as a platform-diagnostics request does, so the tenant query
    /// filters and <c>BusinessOwnershipEnforcer</c> stay fully in force and the endpoint reads no
    /// business data at all.
    /// </summary>
    [Fact]
    public async Task A_marked_endpoint_runs_with_a_denied_scope_and_never_an_unscoped_one()
    {
        var context = AuthenticatedRequest(membershipNotRequiredEndpoint: true);
        var scope = new BusinessScope();
        var timeZoneScope = new BusinessTimeZoneScope();

        await InvokeAsync(
            context,
            scope,
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing),
            timeZoneScope);

        Assert.Null(scope.BusinessId);
        Assert.NotEqual(BusinessScopeState.Unscoped, scope.State);
        Assert.Null(timeZoneScope.TimeZoneId);
    }

    /// <summary>
    /// A member reaching a marked endpoint is scoped no differently: the marker is about the
    /// membership <em>requirement</em>, so the request that asks what state an account is in never
    /// becomes a request that can read that account's business data.
    /// </summary>
    [Fact]
    public async Task A_member_on_a_marked_endpoint_is_also_left_with_a_denied_scope()
    {
        var context = AuthenticatedRequest(membershipNotRequiredEndpoint: true);
        var scope = new BusinessScope();

        var reached = await InvokeAsync(
            context,
            scope,
            BusinessMembershipResolution.Resolved(BusinessId.From(7), BusinessRole.Owner));

        Assert.True(reached);
        Assert.Null(scope.BusinessId);
        Assert.NotEqual(BusinessScopeState.Unscoped, scope.State);
    }

    /// <summary>
    /// The marker grants nothing anywhere else. An unmarked endpoint keeps the membership
    /// requirement it has today, with the same public 403, whoever the caller is.
    /// </summary>
    [Fact]
    public async Task An_unmarked_endpoint_still_refuses_a_caller_with_no_usable_membership()
    {
        var context = AuthenticatedRequest(membershipNotRequiredEndpoint: false);

        var reached = await InvokeAsync(
            context,
            new BusinessScope(),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing));

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    /// <summary>
    /// An anonymous request is left to the authentication middleware's 401 on a marked endpoint
    /// exactly as everywhere else: "membership not required" is not "sign-in not required".
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_to_a_marked_endpoint_is_left_alone()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(Endpoint(membershipNotRequiredEndpoint: true));

        var reached = await InvokeAsync(
            context,
            new BusinessScope(),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.NotAuthenticated));

        Assert.True(reached);
    }

    /// <summary>
    /// The marker is not the platform-diagnostics one and asks no policy. A marked endpoint is
    /// reached without the platform-admin policy being evaluated at all, so no caller can gain the
    /// diagnostics bypass - or anything else it decides - by carrying this marker.
    /// </summary>
    [Fact]
    public async Task A_marked_endpoint_evaluates_no_authorization_policy()
    {
        var authorizationService = new RecordingAuthorizationService();
        var context = AuthenticatedRequest(membershipNotRequiredEndpoint: true);

        await InvokeAsync(
            context,
            new BusinessScope(),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing),
            authorizationService: authorizationService);

        Assert.Empty(authorizationService.EvaluatedPolicies);
    }

    /// <summary>
    /// An endpoint that carries both markers is still a diagnostics endpoint: the platform-admin
    /// policy decides it, and a caller the policy refuses is refused. The membership-not-required
    /// marker must not become a way of removing the policy check from the diagnostics surface.
    /// </summary>
    [Fact]
    public async Task The_marker_does_not_weaken_a_platform_diagnostics_endpoint()
    {
        var context = AuthenticatedRequest(membershipNotRequiredEndpoint: true, platformDiagnosticsEndpoint: true);

        var reached = await InvokeAsync(
            context,
            new BusinessScope(),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing),
            authorizationService: new RecordingAuthorizationService(succeeds: false));

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private static async Task<bool> InvokeAsync(
        HttpContext context,
        BusinessScope scope,
        BusinessMembershipResolution resolution,
        BusinessTimeZoneScope? timeZoneScope = null,
        RecordingAuthorizationService? authorizationService = null)
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
            timeZoneScope ?? new BusinessTimeZoneScope(),
            new StubBusinessProfileStore(),
            authorizationService ?? new RecordingAuthorizationService());

        return reached;
    }

    private static DefaultHttpContext AuthenticatedRequest(
        bool membershipNotRequiredEndpoint,
        bool platformDiagnosticsEndpoint = false)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tid", "11111111-1111-1111-1111-111111111111")],
                authenticationType: "Test")),
        };

        context.SetEndpoint(Endpoint(membershipNotRequiredEndpoint, platformDiagnosticsEndpoint));

        return context;
    }

    private static Endpoint Endpoint(
        bool membershipNotRequiredEndpoint,
        bool platformDiagnosticsEndpoint = false)
    {
        List<object> metadata = [];
        if (membershipNotRequiredEndpoint)
        {
            metadata.Add(new MembershipNotRequiredEndpointAttribute());
        }

        if (platformDiagnosticsEndpoint)
        {
            metadata.Add(new PlatformDiagnosticsEndpointAttribute());
        }

        return new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test");
    }

    private sealed class StubCurrentBusinessProvider(BusinessMembershipResolution resolution)
        : ICurrentBusinessProvider
    {
        public Task<BusinessMembershipResolution> ResolveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(resolution);

        public Task<BusinessId> RequireBusinessIdAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BusinessRole> RequireRoleAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubBusinessProfileStore : IBusinessProfileStore
    {
        public Task<BusinessProfile?> FindAsync(BusinessId businessId, CancellationToken cancellationToken) =>
            Task.FromResult<BusinessProfile?>(new BusinessProfile("Stub business", "Australia/Sydney"));
    }

    /// <summary>
    /// Records every policy the middleware evaluates, so a test can assert that a marked endpoint
    /// asks none at all rather than merely being let through.
    /// </summary>
    private sealed class RecordingAuthorizationService(bool succeeds = true) : IAuthorizationService
    {
        public List<string> EvaluatedPolicies { get; } = [];

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
            EvaluatedPolicies.Add(policyName);
            return Task.FromResult(succeeds ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
    }
}
