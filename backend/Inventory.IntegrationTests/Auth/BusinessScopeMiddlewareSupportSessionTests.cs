using System.Security.Claims;
using System.Text.Json;
using Inventory.Application.Businesses;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.Tests.Application.SupportSessions;
using InventoryApi.Tests.Application.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The support-session half of <see cref="BusinessScopeMiddleware"/> (issue #551), exercised
/// directly on the middleware so that what is being decided is visible.
///
/// <para>Three things are being pinned. A usable session publishes its business as
/// <see cref="BusinessScopeState.SupportReadOnly"/> - the <em>same</em> business id the tenant query
/// filters compare against, so a support request reads exactly that business - together with that
/// business's own time zone. A presented session the server will not honour is refused with
/// <c>403</c> and the stable <c>support_session_ended</c> code <em>before</em> any business is
/// resolved, and never falls back to the caller's own membership. And a request that presented
/// nothing takes the membership path completely unchanged.</para>
///
/// <para>The HTTP-level behaviour over a real database is
/// <c>InventoryApi.Tests.Controllers.SupportSessionRequestApiTests</c>. What is here is the
/// middleware's own decision, with the session accessor stubbed, which is the one place that
/// decision is made.</para>
/// </summary>
public class BusinessScopeMiddlewareSupportSessionTests
{
    private const int SupportedBusinessId = 42;

    [Fact]
    public async Task A_usable_session_publishes_its_business_as_read_only()
    {
        var scope = new BusinessScope();

        var reached = await InvokeAsync(
            scope,
            FakeSupportSessionAccessor.Active(SupportedBusinessId),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing));

        Assert.True(reached);
        Assert.Equal(BusinessScopeState.SupportReadOnly, scope.State);
        Assert.Equal(SupportedBusinessId, scope.BusinessId);
    }

    /// <summary>
    /// The business calendar's half of the same resolution: a support request derives business dates
    /// in the business it is reading, not in the administrator's own anything - they have none.
    /// </summary>
    [Fact]
    public async Task A_usable_session_also_carries_the_supported_businesss_time_zone()
    {
        var timeZoneScope = new BusinessTimeZoneScope();

        await InvokeAsync(
            new BusinessScope(),
            FakeSupportSessionAccessor.Active(SupportedBusinessId),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing),
            timeZoneScope,
            storedTimeZoneId: "America/New_York");

        Assert.Equal("America/New_York", timeZoneScope.TimeZoneId);
    }

    /// <summary>
    /// The criterion: an unusable session is refused with <c>403</c> and
    /// <c>support_session_ended</c> before any business processing, and the endpoint is never
    /// reached.
    /// </summary>
    [Fact]
    public async Task An_unusable_session_is_refused_with_the_session_ended_code()
    {
        var context = AuthenticatedRequest();
        var scope = new BusinessScope();

        var reached = await InvokeAsync(
            scope,
            FakeSupportSessionAccessor.Ended(),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing),
            context: context);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(BusinessScopeState.Denied, scope.State);
        Assert.Null(scope.BusinessId);
        Assert.Equal(SupportSessionErrorCodes.SessionEnded, await CodeAsync(context));
    }

    /// <summary>
    /// The criterion that matters most: an unusable session never falls back to the caller's own
    /// membership. The caller here <em>has</em> a perfectly good one, and is still refused - a
    /// supporter whose session ended must be told so rather than quietly served as themselves.
    /// </summary>
    [Fact]
    public async Task An_unusable_session_never_falls_back_to_the_callers_own_membership()
    {
        var context = AuthenticatedRequest();
        var scope = new BusinessScope();

        var reached = await InvokeAsync(
            scope,
            FakeSupportSessionAccessor.Ended(),
            BusinessMembershipResolution.Resolved(BusinessId.From(7), BusinessRole.Owner),
            context: context);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Null(scope.BusinessId);
    }

    /// <summary>
    /// A request that presented no session is a membership request, exactly as before: the resolved
    /// state, not the read-only one.
    /// </summary>
    [Fact]
    public async Task A_request_that_presented_no_session_resolves_its_own_membership()
    {
        var scope = new BusinessScope();

        var reached = await InvokeAsync(
            scope,
            new FakeSupportSessionAccessor(),
            BusinessMembershipResolution.Resolved(BusinessId.From(7), BusinessRole.Owner));

        Assert.True(reached);
        Assert.Equal(BusinessScopeState.Resolved, scope.State);
        Assert.Equal(7, scope.BusinessId);
    }

    /// <summary>
    /// The refusal body says nothing about <em>why</em> the session is unusable. Three of the four
    /// reasons are facts about somebody else's session or about a business the caller is no longer
    /// entitled to anything about, and the fourth is not worth telling them apart for.
    /// </summary>
    [Fact]
    public async Task The_refusal_says_nothing_about_which_session_or_business_it_was()
    {
        var context = AuthenticatedRequest();

        await InvokeAsync(
            new BusinessScope(),
            FakeSupportSessionAccessor.Ended(),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing),
            context: context);

        var body = await BodyAsync(context);
        foreach (var leak in new[] { "expired", "deactivated", "Vending Co", "42", "business id" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Issue #524's rule, now structural rather than conditional: the support path schedules no
    /// member-profile refresh at all, because there is no member on it whose display name the
    /// supporter's claims could overwrite.
    /// </summary>
    [Fact]
    public async Task A_support_request_schedules_no_member_profile_refresh()
    {
        var queue = new MemberProfileRefreshQueue();

        await InvokeAsync(
            new BusinessScope(),
            FakeSupportSessionAccessor.Active(SupportedBusinessId),
            BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing),
            refreshQueue: queue);

        Assert.Empty(await TestMemberProfileRefresh.DrainAsync(queue));
    }

    private static async Task<bool> InvokeAsync(
        BusinessScope scope,
        ISupportSessionAccessor supportSession,
        BusinessMembershipResolution resolution,
        BusinessTimeZoneScope? timeZoneScope = null,
        string? storedTimeZoneId = "Australia/Sydney",
        HttpContext? context = null,
        MemberProfileRefreshQueue? refreshQueue = null)
    {
        var reached = false;
        var middleware = new BusinessScopeMiddleware(
            _ =>
            {
                reached = true;
                return Task.CompletedTask;
            },
            NullLogger<BusinessScopeMiddleware>.Instance);

        var httpContext = context ?? AuthenticatedRequest();
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(
            httpContext,
            new StubCurrentBusinessProvider(resolution),
            scope,
            timeZoneScope ?? new BusinessTimeZoneScope(),
            new StubBusinessProfileStore(storedTimeZoneId),
            new StubAuthorizationService(),
            supportSession,
            TestMemberProfileRefresh.RequestScheduler(
                FakeActorProfileAccessor.Unidentifiable(),
                refreshQueue));

        return reached;
    }

    private static async Task<string> BodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static async Task<string?> CodeAsync(HttpContext context)
    {
        using var json = JsonDocument.Parse(await BodyAsync(context));
        return json.RootElement.GetProperty("code").GetString();
    }

    private static DefaultHttpContext AuthenticatedRequest()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tid", "11111111-1111-1111-1111-111111111111")],
                authenticationType: "Test")),
        };

        // An ordinary business endpoint: a support session is for the endpoints the business's own
        // members use, never for the platform-admin control surface.
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "test"));

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

    private sealed class StubBusinessProfileStore(string? timeZoneId) : IBusinessProfileStore
    {
        public Task<BusinessProfile?> FindAsync(BusinessId businessId, CancellationToken cancellationToken) =>
            Task.FromResult<BusinessProfile?>(
                timeZoneId is null
                    ? null
                    : new BusinessProfile("Stub business", timeZoneId, BusinessStatus.Active));
    }

    /// <summary>
    /// Refuses the platform-admin policy, because nothing on this path should be asking it: the
    /// session accessor is what re-evaluates that policy, and these tests stub the accessor.
    /// </summary>
    private sealed class StubAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            Assert.Equal(PlatformAdminPolicy.Name, policyName);
            return Task.FromResult(AuthorizationResult.Failed());
        }
    }
}
