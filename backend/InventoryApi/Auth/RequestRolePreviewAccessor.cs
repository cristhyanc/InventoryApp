using Inventory.Application.Access;
using InventoryApi.Auth.PlatformAdmin;
using Microsoft.AspNetCore.Authorization;

namespace InventoryApi.Auth;

/// <summary>
/// The boundary implementation of <see cref="IRolePreviewAccessor"/> (issue #720): the one place
/// that reads <see cref="RolePreviewHeader"/>'s value and turns it into the role this request is
/// answered as.
///
/// <para><strong>Both halves are required, and both are this request's own.</strong> A preview is
/// honoured only when <see cref="PlatformAdminPolicy"/> succeeds for this request - re-evaluated
/// here, never inferred from the fact that the authorization middleware ran - and the presented
/// value is exactly one of the three published role names. The same reasoning
/// <see cref="RequestSupportSessionAccessor"/> and the diagnostics membership bypass follow: an
/// access decision that depended on pipeline position would be one edit away from applying to
/// somebody nobody authorised.</para>
///
/// <para><strong>Neither failure is ignored.</strong> A caller the policy refuses resolves
/// <see cref="RolePreviewState.NotPermitted"/> and an unreadable value
/// <see cref="RolePreviewState.Invalid"/>, and <see cref="RolePreviewMiddleware"/> refuses both
/// before the endpoint runs. That is deliberately unlike the support-session header, whose value is
/// simply not read for a caller the policy refuses: that header is a credential, and reading it for
/// an ordinary member would let a session rule refuse a request that has nothing to do with
/// sessions. This header is the opposite shape - its only effect is to take access away - so
/// quietly ignoring it would answer a client that believes it is previewing an Operator with
/// everything an Owner may see.</para>
///
/// <para><strong>An unauthenticated request is left alone</strong>, exactly as
/// <see cref="BusinessScopeMiddleware"/> leaves one alone: the authentication and authorization
/// middleware answer it <c>401</c>, and turning that into a <c>403</c> about a header would hide
/// the difference between "you are not signed in" and "you may not preview a role". Nothing is
/// served to such a request either way.</para>
///
/// <para><strong>Memoised, so the question is asked once.</strong> The middleware asks before the
/// endpoint is chosen, the capability authorization handler asks through
/// <c>CurrentCapabilities</c>, and the use cases and response projections ask again. One answer
/// serves all of them, so they cannot disagree and the platform-admin policy is evaluated once.</para>
///
/// <para><strong>Why the authorization service is resolved from the request rather than
/// injected.</strong> One of this type's consumers is <c>CurrentCapabilities</c>, which the
/// container builds as part of <c>IAuthorizationService</c>'s own dependency graph through
/// <see cref="BusinessCapabilityAuthorizationHandler"/>. Taking that service in the constructor
/// would make the graph circular and fail at resolution; taking it from the request's own service
/// provider is the same instance, obtained at the moment it is used.</para>
/// </summary>
public sealed class RequestRolePreviewAccessor : IRolePreviewAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<RequestRolePreviewAccessor> _logger;
    private RolePreviewAccess? _access;

    public RequestRolePreviewAccessor(
        IHttpContextAccessor httpContextAccessor,
        ILogger<RequestRolePreviewAccessor> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RolePreviewAccess> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_access is not null)
        {
            return _access;
        }

        return _access = await ResolveOnceAsync().ConfigureAwait(false);
    }

    private async Task<RolePreviewAccess> ResolveOnceAsync()
    {
        var context = _httpContextAccessor.HttpContext;

        if (context is null)
        {
            // No request at all - the background profile-refresh scope, for instance. Nothing is
            // previewing a role there.
            return RolePreviewAccess.NotRequested;
        }

        if (!context.Request.Headers.TryGetValue(RolePreviewHeader.Name, out var presented))
        {
            return RolePreviewAccess.NotRequested;
        }

        if (context.User.Identity is not { IsAuthenticated: true })
        {
            // See the class remarks: a request nobody has identified keeps its ordinary 401.
            return RolePreviewAccess.NotRequested;
        }

        var platformAdmin = await context.RequestServices
            .GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(context.User, resource: null, PlatformAdminPolicy.Name)
            .ConfigureAwait(false);

        if (!platformAdmin.Succeeded)
        {
            _logger.LogWarning(
                "A role preview was refused for this request: the caller is not the configured "
                    + "platform administrator.");

            return RolePreviewAccess.NotPermitted;
        }

        if (presented.Count > 1)
        {
            // Sent twice, which names no single role. It is refused rather than resolved from the
            // first or last value, because which of them a reader would pick is not something a
            // client should have to know.
            return Invalid("the header was sent more than once");
        }

        if (!RolePreviewPolicy.TryParseRole(presented.ToString(), out var role))
        {
            // The value itself is deliberately not logged: it is caller-supplied text, and which
            // role was asked for adds nothing an operator can act on beyond "it named none".
            return Invalid("the presented value is not one of the three published role names");
        }

        return RolePreviewAccess.Active(role);
    }

    /// <summary>
    /// Records why a presented preview could not be read, for an operator, and answers with the one
    /// state the caller is told about. The reason is logged rather than returned for the reason
    /// every other refusal in this layer logs one: the caller is told the stable code, which is all
    /// they need to correct the request.
    /// </summary>
    private RolePreviewAccess Invalid(string reason)
    {
        _logger.LogWarning(
            "A role preview was refused for this request: {RolePreviewDenialReason}.",
            reason);

        return RolePreviewAccess.Invalid;
    }
}
