using System.Security.Claims;
using Inventory.Application.Access;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth;
using InventoryApi.Auth.PlatformAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The boundary half of a role preview (issue #720): what
/// <see cref="RequestRolePreviewAccessor"/> makes of one request's <c>X-Role-Preview</c> header.
///
/// <para>It is here rather than in <c>RolePreviewApiTests</c> for the values a real client can send
/// but <c>HttpClient</c> will not: an <strong>empty</strong> header value is dropped before it
/// reaches a server, so the only way to exercise it is to present the request as a server receives
/// it. The rest of the matrix is asserted over the real pipeline there as well, deliberately: this
/// file pins the decision and that one pins the answer a caller gets.</para>
/// </summary>
public class RequestRolePreviewAccessorTests
{
    [Theory]
    [InlineData("Owner", BusinessRole.Owner)]
    [InlineData("Manager", BusinessRole.Manager)]
    [InlineData("Operator", BusinessRole.Operator)]
    public async Task A_published_role_name_is_honoured_for_a_platform_administrator(
        string presented,
        BusinessRole expected)
    {
        var access = await ResolveAsync(presented);

        Assert.Equal(RolePreviewState.Active, access.State);
        Assert.Equal(expected, access.Role);
    }

    /// <summary>
    /// The criterion an HTTP client cannot express: a header that is present with no value at all
    /// names no role, and is refused rather than read as "no preview was asked for".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("owner")]
    [InlineData("OWNER")]
    [InlineData("20")]
    [InlineData("Viewer")]
    public async Task A_value_that_names_no_role_is_invalid(string presented)
    {
        var access = await ResolveAsync(presented);

        Assert.Equal(RolePreviewState.Invalid, access.State);
        Assert.Null(access.Role);
    }

    /// <summary>
    /// The header sent twice names no single role either, and is refused rather than resolved from
    /// one of the two values.
    /// </summary>
    [Fact]
    public async Task The_header_sent_twice_is_invalid()
    {
        var access = await ResolveAsync(new StringValues(["Owner", "Operator"]));

        Assert.Equal(RolePreviewState.Invalid, access.State);
    }

    /// <summary>
    /// Even when both values are the same declared role: "sent once" is part of the contract, so a
    /// client cannot find a spelling of the header that a reader has to disambiguate.
    /// </summary>
    [Fact]
    public async Task The_header_sent_twice_with_one_role_is_invalid_too()
    {
        var access = await ResolveAsync(new StringValues(["Owner", "Owner"]));

        Assert.Equal(RolePreviewState.Invalid, access.State);
    }

    [Fact]
    public async Task No_header_asks_for_nothing()
    {
        var access = await ResolveAsync(StringValues.Empty);

        Assert.Equal(RolePreviewState.NotRequested, access.State);
    }

    /// <summary>
    /// A caller the platform-admin policy refuses is refused rather than ignored, which is the
    /// difference from <c>X-Support-Session</c>: a header whose only effect is to take access away
    /// must never be droppable into full access.
    /// </summary>
    [Theory]
    [InlineData("Operator")]
    [InlineData("nonsense")]
    public async Task A_caller_the_policy_refuses_is_not_permitted(string presented)
    {
        var access = await ResolveAsync(presented, platformAdmin: false);

        Assert.Equal(RolePreviewState.NotPermitted, access.State);
    }

    /// <summary>
    /// And that refusal outranks an unreadable value: the policy is asked first, so the header's
    /// value is never read for somebody it does not belong to.
    /// </summary>
    [Fact]
    public async Task Not_being_permitted_is_decided_before_the_value_is_read()
    {
        var authorization = new RecordingAuthorizationService(succeeds: false);

        var access = await ResolveAsync("Viewer", authorization);

        Assert.Equal(RolePreviewState.NotPermitted, access.State);
        Assert.Equal(1, authorization.Evaluations);
    }

    /// <summary>
    /// An unauthenticated request has the header not read at all, so it keeps the <c>401</c> the
    /// authentication and authorization middleware answer it with rather than being told about a
    /// header. Nothing is served to it either way.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_asks_for_nothing()
    {
        var authorization = new RecordingAuthorizationService(succeeds: true);

        var access = await ResolveAsync("Operator", authorization, authenticated: false);

        Assert.Equal(RolePreviewState.NotRequested, access.State);
        // The policy is not even evaluated: there is no actor to evaluate it for.
        Assert.Equal(0, authorization.Evaluations);
    }

    /// <summary>
    /// A scope with no request at all - the background member-profile refresh, for instance -
    /// previews nothing.
    /// </summary>
    [Fact]
    public async Task A_scope_with_no_request_asks_for_nothing()
    {
        var accessor = new RequestRolePreviewAccessor(
            new HttpContextAccessor(),
            NullLogger<RequestRolePreviewAccessor>.Instance);

        Assert.Equal(
            RolePreviewState.NotRequested,
            (await accessor.ResolveAsync(CancellationToken.None)).State);
    }

    /// <summary>
    /// Memoised: several components ask in one request - the middleware, the capability handler
    /// through <c>CurrentCapabilities</c>, the use cases - and the platform-admin policy is
    /// evaluated once for all of them.
    /// </summary>
    [Fact]
    public async Task The_answer_is_resolved_once_per_request()
    {
        var authorization = new RecordingAuthorizationService(succeeds: true);
        var accessor = Accessor("Operator", authorization, authenticated: true);

        var first = await accessor.ResolveAsync(CancellationToken.None);
        var second = await accessor.ResolveAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, authorization.Evaluations);
    }

    private static Task<RolePreviewAccess> ResolveAsync(string presented, bool platformAdmin = true) =>
        ResolveAsync(
            new StringValues(presented),
            new RecordingAuthorizationService(platformAdmin));

    private static Task<RolePreviewAccess> ResolveAsync(
        string presented,
        RecordingAuthorizationService authorization,
        bool authenticated = true) =>
        ResolveAsync(new StringValues(presented), authorization, authenticated);

    private static Task<RolePreviewAccess> ResolveAsync(
        StringValues presented,
        RecordingAuthorizationService? authorization = null,
        bool authenticated = true) =>
        Accessor(
                presented,
                authorization ?? new RecordingAuthorizationService(succeeds: true),
                authenticated)
            .ResolveAsync(CancellationToken.None);

    private static RequestRolePreviewAccessor Accessor(
        StringValues presented,
        RecordingAuthorizationService authorization,
        bool authenticated)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthorizationService>(authorization)
                .BuildServiceProvider(),
        };

        if (presented.Count > 0)
        {
            context.Request.Headers[RolePreviewHeader.Name] = presented;
        }

        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tid", "99999999-9999-9999-9999-999999999999"),
                    new Claim("oid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ],
                authenticationType: "Test"));
        }

        return new RequestRolePreviewAccessor(
            new HttpContextAccessor { HttpContext = context },
            NullLogger<RequestRolePreviewAccessor>.Instance);
    }

    /// <summary>
    /// Answers the platform-admin policy with a fixed decision and counts how often it was asked,
    /// so "the policy is re-evaluated on this request" and "it is evaluated once" are both
    /// assertable. It asserts the policy name, because this accessor must ask about platform
    /// authority and nothing else.
    /// </summary>
    private sealed class RecordingAuthorizationService(bool succeeds) : IAuthorizationService
    {
        public int Evaluations { get; private set; }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            throw new NotSupportedException();

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
        {
            Assert.Equal(PlatformAdminPolicy.Name, policyName);
            Evaluations++;

            return Task.FromResult(
                succeeds ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
    }
}
