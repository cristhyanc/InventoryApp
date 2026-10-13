using Inventory.Application.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace InventoryApi.Auth.PlatformAdmin;

/// <summary>
/// The one named authorization policy that lets a caller reach a platform-admin API - the read-only
/// diagnostics surface (issue #336) and the Super Admin's business directory and decisions (issue
/// #544).
///
/// Nothing else in the application uses it, and it decides nothing else: ordinary business
/// endpoints keep the authorization they already had - <c>[Authorize]</c>, the
/// <c>access_as_user</c> scope, and the <c>BusinessMembership</c> requirement
/// <c>BusinessScopeMiddleware</c> enforces - and this policy neither grants nor widens any of them.
/// A platform administrator who is not a member of a business still reads no business data through
/// a business endpoint.
/// </summary>
public static class PlatformAdminPolicy
{
    public const string Name = "PlatformDiagnostics";
}

/// <summary>
/// Marks an endpoint as part of the platform-admin surface - the diagnostics API it was introduced
/// for (issue #336), and the Super Admin businesses API that joined it (issue #544).
///
/// <see cref="BusinessScopeMiddleware"/> looks for this metadata to decide which endpoints are even
/// eligible to skip the membership requirement, and then re-evaluates
/// <see cref="PlatformAdminPolicy"/> itself before skipping it. The attribute is therefore a
/// marker, not a permission: on its own it grants nothing, and an endpoint carrying it without the
/// policy is still refused.
///
/// It keeps its original name on purpose: it is the same marker, decided the same way, and a second
/// one for a second platform surface would be a second mechanism to review rather than one. What a
/// marked endpoint may do is bounded by its own use case, not by this attribute - which is why the
/// Super Admin's write path is a narrow business-status store and nothing wider
/// (docs/architecture.md § Super Admin businesses API).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PlatformDiagnosticsEndpointAttribute : Attribute;

/// <summary>The requirement <see cref="PlatformAdminAuthorizationHandler"/> answers.</summary>
public sealed class PlatformAdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Decides whether the current caller is the configured platform administrator (issue #336).
///
/// It reads the caller's identity through <see cref="IAuthenticatedActorAccessor"/> - the same
/// <c>(tid, oid)</c> parse <c>EntraActorIdentityAccessor</c> performs for membership resolution -
/// rather than re-reading claims here. One parse means one answer to "who is calling": a handler
/// with its own claim reading could accept a token the membership path rejects, or disagree about
/// which actor a token with conflicting claim spellings names.
///
/// It fails closed in every direction. An unauthenticated caller, a token with no usable
/// <c>(tid, oid)</c> pair, an actor who is not the configured one, and the shipped state where no
/// administrator is configured at all each leave the requirement unmet, and an unmet requirement is
/// a refusal rather than a fallback to membership.
/// </summary>
public sealed class PlatformAdminAuthorizationHandler : AuthorizationHandler<PlatformAdminRequirement>
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly ConfiguredPlatformAdmin _configuredAdmin;
    private readonly ILogger<PlatformAdminAuthorizationHandler> _logger;

    public PlatformAdminAuthorizationHandler(
        IAuthenticatedActorAccessor actorAccessor,
        ConfiguredPlatformAdmin configuredAdmin,
        ILogger<PlatformAdminAuthorizationHandler> logger)
    {
        _actorAccessor = actorAccessor;
        _configuredAdmin = configuredAdmin;
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformAdminRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.Identity is not { IsAuthenticated: true })
        {
            return Task.CompletedTask;
        }

        var actor = _actorAccessor.GetCurrentActor().Actor;

        if (_configuredAdmin.Matches(actor))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Logged as a refusal without the actor's identifiers: an attempt on this endpoint is worth
        // seeing, and whose it was belongs in the audited success path, not in a warning about a
        // caller the application has decided to tell nothing.
        _logger.LogWarning(
            "Platform-admin access denied. A platform administrator {IsConfigured} configured.",
            _configuredAdmin.IsConfigured ? "is" : "is not");

        return Task.CompletedTask;
    }
}
