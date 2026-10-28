using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace InventoryApi.Auth.PriceCollector;

/// <summary>
/// The one named authorization policy that lets a caller reach the price collector surface (issue
/// #924, parent #817).
///
/// <para>Nothing else in the application uses it, and it decides nothing else. Ordinary business
/// endpoints keep the authorization they already had - <c>[Authorize]</c>, the
/// <c>access_as_user</c> scope, a capability and the <c>BusinessMembership</c> requirement
/// <see cref="BusinessScopeMiddleware"/> enforces - and this policy neither grants nor widens any of
/// them. It grants no capability, resolves no business, and is satisfiable by no person: a member, an
/// Owner, a Super Admin and a support session are all refused here exactly as they are refused the
/// platform-admin policy they do not hold.</para>
/// </summary>
public static class PriceCollectorPolicy
{
    public const string Name = "PriceCollector";
}

/// <summary>The requirement <see cref="PriceCollectorAuthorizationHandler"/> answers.</summary>
public sealed class PriceCollectorRequirement : IAuthorizationRequirement;

/// <summary>
/// Decides whether the current caller is the configured price collector (issue #924, parent #817).
///
/// <para><strong>It asks the same four questions the bearer scheme already asked, and that is
/// deliberate.</strong> <see cref="PriceCollectorBearerScheme"/> refuses a token that fails any of
/// them, so a request that reaches this handler has already passed them; asking again is what makes
/// the answer independent of which scheme happened to authenticate the request. Entra puts an
/// assigned application role in a <em>user's</em> token too, so a policy that only checked the role
/// would be satisfied by a delegated token from the right tenant - and the one thing this surface
/// must never be is reachable as a person. The four together are the issue's first criterion: the
/// role, no delegated scope at all, the pinned tenant, and the pinned client application.</para>
///
/// <para><strong>It fails closed in every direction.</strong> An unauthenticated caller, a token
/// missing any of the four, a token whose claims disagree with each other, and the shipped state
/// where no collector is configured at all each leave the requirement unmet - and an unmet
/// requirement is a refusal, never a fallback to membership or to anything else.</para>
///
/// <para>Nothing it reads can be influenced by a request beyond the validated token's own claims:
/// there is no header, route value, query value or body field in any of it.</para>
/// </summary>
public sealed class PriceCollectorAuthorizationHandler
    : AuthorizationHandler<PriceCollectorRequirement>
{
    /// <summary>The directory tenant claim, in the short and the WS-* mapped spelling.</summary>
    private static readonly string[] DirectoryTenantIdClaimTypes =
    [
        "tid",
        "http://schemas.microsoft.com/identity/claims/tenantid",
    ];

    /// <summary>The delegated-scope claim, in the same two spellings.</summary>
    private static readonly string[] ScopeClaimTypes =
    [
        "scp",
        "http://schemas.microsoft.com/identity/claims/scope",
    ];

    private readonly PriceCollectorAuthenticationDecision _collector;
    private readonly ILogger<PriceCollectorAuthorizationHandler> _logger;

    public PriceCollectorAuthorizationHandler(
        PriceCollectorAuthenticationDecision collector,
        ILogger<PriceCollectorAuthorizationHandler> logger)
    {
        _collector = collector;
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PriceCollectorRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.Identity is not { IsAuthenticated: true } || !_collector.IsRegistered)
        {
            LogRefusal();
            return Task.CompletedTask;
        }

        if (!HasCollectorRole(context.User)
            || HasAnyDelegatedScope(context.User)
            || !NamesOnly(context.User, DirectoryTenantIdClaimTypes, IsConfiguredTenant)
            || !NamesOnly(context.User, PriceCollectorRole.ClientApplicationClaimTypes, IsConfiguredClient))
        {
            LogRefusal();
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }

    private static IEnumerable<Claim> Claims(ClaimsPrincipal principal, string[] claimTypes) =>
        claimTypes.SelectMany(principal.FindAll);

    /// <summary>
    /// Whether the principal carries at least one of these claims and every one of them is the
    /// configured value. Both halves matter: a token carrying none names nobody, and a token whose
    /// two spellings of one claim disagree names two callers - neither is the collector.
    /// </summary>
    private static bool NamesOnly(
        ClaimsPrincipal principal,
        string[] claimTypes,
        Func<Claim, bool> isConfigured)
    {
        var claims = Claims(principal, claimTypes).ToArray();

        return claims.Length > 0 && claims.All(isConfigured);
    }

    private static bool HasCollectorRole(ClaimsPrincipal principal) =>
        Claims(principal, PriceCollectorRole.RoleClaimTypes)
            .Any(claim => string.Equals(
                claim.Value?.Trim(), PriceCollectorRole.Name, StringComparison.Ordinal));

    private static bool HasAnyDelegatedScope(ClaimsPrincipal principal) =>
        Claims(principal, ScopeClaimTypes)
            .Any(claim => !string.IsNullOrWhiteSpace(claim.Value));

    private bool IsConfiguredTenant(Claim claim) =>
        Guid.TryParse(claim.Value?.Trim(), out var claimed) && claimed == _collector.TenantId;

    private bool IsConfiguredClient(Claim claim) =>
        Guid.TryParse(claim.Value?.Trim(), out var claimed) && claimed == _collector.ClientId;

    /// <summary>
    /// Logged as a refusal with no claim, token or identifier in it: an attempt on this surface is
    /// worth seeing, and which of the four rules the caller failed is information a caller the
    /// application has decided to tell nothing must not be able to probe for.
    /// </summary>
    private void LogRefusal() =>
        _logger.LogWarning(
            "Price collector access denied. A collector {IsConfigured} configured.",
            _collector.IsRegistered ? "is" : "is not");
}
