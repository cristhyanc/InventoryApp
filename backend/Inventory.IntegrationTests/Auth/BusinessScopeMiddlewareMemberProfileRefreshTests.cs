using System.Security.Claims;
using Inventory.Application.Businesses;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// Where the member-profile refresh is scheduled from (issue #524): the business-scope middleware,
/// after the response has been decided, for a request that resolved a membership and does not carry
/// the support-session header.
///
/// <para>These tests are about the request half only - they assert what was queued, never what was
/// written, because the middleware's whole contribution is that it does not write. The queue is
/// drained and the write proved separately
/// (<see cref="MemberProfileRefreshWorkerTests"/>, <c>EfMemberProfileWriteStoreTests</c>).</para>
/// </summary>
public class BusinessScopeMiddlewareMemberProfileRefreshTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var actor));
        return actor!;
    }

    private static MemberProfile Profile() => MemberProfile.Create("Ada Lovelace", "ada@vending.example");

    /// <summary>
    /// The refresh a member's request schedules: their own identity, their own token's claims, and
    /// one queued refresh.
    /// </summary>
    [Fact]
    public async Task A_members_request_schedules_a_refresh_for_their_own_claims()
    {
        var invocation = await InvokeAsync();

        Assert.True(invocation.Reached);
        var queued = Assert.Single(await TestMemberProfileRefresh.DrainAsync(invocation.Queue));
        Assert.Equal(Actor(), queued.Identity);
        Assert.Equal(Profile(), queued.Profile);
    }

    /// <summary>
    /// The response is decided first. The middleware schedules nothing before the endpoint has run,
    /// so a refresh can neither change what the caller is answered nor delay it.
    /// </summary>
    [Fact]
    public async Task Nothing_is_scheduled_until_the_response_has_been_decided()
    {
        var queue = new MemberProfileRefreshQueue();
        var queuedWhenTheEndpointRan = -1;

        await InvokeAsync(
            queue: queue,
            next: _ =>
            {
                queuedWhenTheEndpointRan = queue.Pending;
                return Task.CompletedTask;
            });

        Assert.Equal(0, queuedWhenTheEndpointRan);
        Assert.Single(await TestMemberProfileRefresh.DrainAsync(queue));
    }

    /// <summary>
    /// The support-session header suppresses the refresh entirely (issue #512's header, defined by
    /// #524). A support session is somebody else looking at the business's data: their token's name
    /// and email address must never be written into the member's row, and the presence of the header
    /// is enough - its value is not read.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("whatever-this-session-is")]
    public async Task A_request_carrying_the_support_session_header_schedules_nothing(string headerValue)
    {
        var invocation = await InvokeAsync(supportSessionHeader: headerValue);

        Assert.True(invocation.Reached);
        Assert.Empty(await TestMemberProfileRefresh.DrainAsync(invocation.Queue));
    }

    /// <summary>
    /// The header is read case-insensitively, as every HTTP header name is, so a differently cased
    /// spelling suppresses the refresh too.
    /// </summary>
    [Fact]
    public async Task The_support_session_header_name_is_matched_case_insensitively()
    {
        var context = AuthenticatedRequest();
        context.Request.Headers["x-support-session"] = "1";

        var invocation = await InvokeAsync(context: context);

        Assert.Empty(await TestMemberProfileRefresh.DrainAsync(invocation.Queue));
    }

    /// <summary>
    /// A caller the membership requirement refuses never reaches the scheduling path: there is no
    /// membership row to refresh, and the request was answered 403 before the endpoint ran.
    /// </summary>
    [Theory]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.MembershipInactive)]
    [InlineData(BusinessAccessDenialReason.MembershipAmbiguous)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    [InlineData(BusinessAccessDenialReason.RoleUnrecognised)]
    public async Task A_refused_caller_schedules_nothing(BusinessAccessDenialReason reason)
    {
        var invocation = await InvokeAsync(resolution: BusinessMembershipResolution.Denied(reason));

        Assert.False(invocation.Reached);
        Assert.Equal(StatusCodes.Status403Forbidden, invocation.Context.Response.StatusCode);
        Assert.Empty(await TestMemberProfileRefresh.DrainAsync(invocation.Queue));
    }

    /// <summary>
    /// An unauthenticated request is left alone, exactly as it is today, and schedules nothing:
    /// there is no validated token to take claims from.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_schedules_nothing()
    {
        var invocation = await InvokeAsync(context: new DefaultHttpContext());

        Assert.True(invocation.Reached);
        Assert.Empty(await TestMemberProfileRefresh.DrainAsync(invocation.Queue));
    }

    /// <summary>
    /// A request on one of the two membership-bypass endpoints schedules nothing either. Both run
    /// with a denied scope and resolve no membership at all, so there is no membership this request
    /// established to refresh - and the account-state endpoint is reachable precisely by people who
    /// have none.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_membership_bypass_endpoint_schedules_nothing(
        bool membershipNotRequired,
        bool platformDiagnostics)
    {
        var context = AuthenticatedRequest(membershipNotRequired, platformDiagnostics);

        var invocation = await InvokeAsync(context: context);

        Assert.True(invocation.Reached);
        Assert.Empty(await TestMemberProfileRefresh.DrainAsync(invocation.Queue));
    }

    /// <summary>
    /// Scheduling can never fail the request. Even an accessor that throws - which the real one
    /// cannot, but which stands for anything unexpected in this path - leaves the response as the
    /// endpoint decided it, and the failure is logged without either display value.
    /// </summary>
    [Fact]
    public async Task A_failure_while_scheduling_never_fails_the_request()
    {
        var logger = new CapturingLogger<BusinessScopeMiddleware>();

        var invocation = await InvokeAsync(
            profileAccessor: FakeActorProfileAccessor.Throwing(),
            logger: logger);

        Assert.True(invocation.Reached);
        Assert.Equal(StatusCodes.Status200OK, invocation.Context.Response.StatusCode);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Ada", StringComparison.Ordinal));
    }

    private sealed record Invocation(
        bool Reached,
        DefaultHttpContext Context,
        MemberProfileRefreshQueue Queue);

    private static async Task<Invocation> InvokeAsync(
        DefaultHttpContext? context = null,
        BusinessMembershipResolution? resolution = null,
        MemberProfileRefreshQueue? queue = null,
        FakeActorProfileAccessor? profileAccessor = null,
        RequestDelegate? next = null,
        string? supportSessionHeader = null,
        ILogger<BusinessScopeMiddleware>? logger = null)
    {
        context ??= AuthenticatedRequest();
        queue ??= new MemberProfileRefreshQueue();

        if (supportSessionHeader is not null)
        {
            context.Request.Headers[SupportSessionHeader.Name] = supportSessionHeader;
        }

        var reached = false;
        var middleware = new BusinessScopeMiddleware(
            async httpContext =>
            {
                reached = true;
                if (next is not null)
                {
                    await next(httpContext);
                }
            },
            logger ?? NullLogger<BusinessScopeMiddleware>.Instance);

        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            context,
            new StubCurrentBusinessProvider(
                resolution ?? BusinessMembershipResolution.Resolved(BusinessId.From(1), BusinessRole.Manager)),
            new BusinessScope(),
            new BusinessTimeZoneScope(),
            new StubBusinessProfileStore(),
            new StubAuthorizationService(),
            profileAccessor ?? FakeActorProfileAccessor.For(Actor(), Profile()),
            TestMemberProfileRefresh.Scheduler(queue));

        return new Invocation(reached, context, queue);
    }

    private static DefaultHttpContext AuthenticatedRequest(
        bool membershipNotRequiredEndpoint = false,
        bool platformDiagnosticsEndpoint = false)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tid", Tid), new Claim("oid", Oid)],
                authenticationType: "Test")),
        };

        List<object> metadata = [];
        if (membershipNotRequiredEndpoint)
        {
            metadata.Add(new MembershipNotRequiredEndpointAttribute());
        }

        if (platformDiagnosticsEndpoint)
        {
            metadata.Add(new PlatformDiagnosticsEndpointAttribute());
        }

        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata),
            "test"));

        return context;
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

    private sealed class StubAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
    }
}
