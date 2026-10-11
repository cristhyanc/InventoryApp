using Inventory.Application.Access;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace InventoryApi.Auth;

/// <summary>
/// The named authorization policy behind each <see cref="Capability"/> (issue #525, one slice of
/// #502 under #328).
///
/// <para>There is exactly one policy per declared capability, registered in <c>Program.cs</c> from
/// <see cref="Capabilities.All"/>, and every policy is answered by the one handler below from the
/// one role → capability table of issue #521. A controller therefore names a capability and never a
/// role: which roles hold it is <see cref="RoleCapabilities"/>'s answer, in one place, and the same
/// answer <c>GET /api/me/access</c> reports - so what an endpoint enforces and what the frontend was
/// told can never drift apart.</para>
///
/// <para>The name is the published capability name behind a fixed prefix, so a capability policy
/// cannot collide with <see cref="PlatformAdmin.PlatformAdminPolicy"/> or any future named policy,
/// and a reader looking at an endpoint's authorization metadata sees which capability was
/// required.</para>
/// </summary>
public static class CapabilityPolicy
{
    /// <summary>
    /// The prefix every capability policy name carries. It is part of the name rather than a
    /// convention, so "is this a capability policy?" is answerable from the name alone.
    /// </summary>
    public const string NamePrefix = "Capability:";

    /// <summary>The policy name for <paramref name="capability"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capability"/> is not a declared capability. An undeclared value names no
    /// policy, and inventing one would leave an endpoint requiring a policy nothing registered.
    /// </exception>
    public static string Name(Capability capability) => NamePrefix + Capabilities.Name(capability);
}

/// <summary>
/// Declares the capability an endpoint requires:
/// <c>[RequireCapability(Capability.InventoryOperate)]</c>.
///
/// <para>It is an <see cref="AuthorizeAttribute"/> carrying the capability's policy name, so the
/// authorization middleware evaluates it before any action runs, exactly as it evaluates the
/// controllers' plain <c>[Authorize]</c> and the diagnostics surface's
/// <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c>. It adds a requirement and removes none: a
/// marked endpoint still requires authentication, the delegated <c>access_as_user</c> scope and a
/// business membership.</para>
///
/// <para>Requirements accumulate down the endpoint, which is deliberate. A controller-level
/// capability plus an action-level one means that action needs <em>both</em> - the shape
/// <c>SuppliersController</c> uses, where every action needs <c>Purchasing.Operate</c> and the
/// GST-default edit additionally needs <c>Suppliers.ManageGstDefaults</c>. Narrowing one action is
/// therefore additive and cannot accidentally widen the controller's rule.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireCapabilityAttribute : AuthorizeAttribute
{
    public RequireCapabilityAttribute(Capability capability)
    {
        // Name() refuses an undeclared capability, and the attribute is constructed while MVC
        // discovers the endpoint - so a controller naming one fails startup rather than quietly
        // requiring a policy that was never registered.
        Policy = CapabilityPolicy.Name(capability);
        Capability = capability;
    }

    /// <summary>The capability this endpoint requires, for the endpoint-classification test.</summary>
    public Capability Capability { get; }
}

/// <summary>The requirement <see cref="BusinessCapabilityAuthorizationHandler"/> answers.</summary>
public sealed class BusinessCapabilityRequirement : IAuthorizationRequirement
{
    public BusinessCapabilityRequirement(Capability capability) => Capability = capability;

    public Capability Capability { get; }
}

/// <summary>
/// Decides whether the current caller's role in their current business grants a capability
/// (issue #525).
///
/// <para><strong>The role comes from the request's memoised membership resolution.</strong> The
/// handler resolves it through <see cref="ICurrentBusinessProvider"/> - the same per-request scoped
/// provider <see cref="BusinessScopeMiddleware"/> uses - so the capability decision and the business
/// the request then acts in come from one membership lookup and cannot disagree. Resolving it here,
/// rather than reading a business scope the middleware published, is what makes the policy
/// independent of pipeline ordering: the authorization middleware runs before that middleware, and a
/// policy that assumed otherwise would be able to pass before the membership had been checked at
/// all. It is the same reasoning that makes the diagnostics bypass re-evaluate its own policy
/// instead of inferring one from where it sits in the pipeline.</para>
///
/// <para><strong>It fails closed in every direction.</strong> An unauthenticated caller, a caller
/// whose membership does not resolve - missing, revoked, ambiguous, an inactive business, or a
/// stored role this deployment does not declare - and a member whose role simply does not grant the
/// capability each leave the requirement unmet. An unmet requirement is a refusal, never a fallback
/// to the least-privileged role, and the <c>403</c> that follows carries no detail about the
/// capability, the caller's role or the roles that would have been allowed. The reason is logged for
/// an operator instead.</para>
/// </summary>
public sealed class BusinessCapabilityAuthorizationHandler : AuthorizationHandler<BusinessCapabilityRequirement>
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<BusinessCapabilityAuthorizationHandler> _logger;

    public BusinessCapabilityAuthorizationHandler(
        ICurrentBusinessProvider currentBusiness,
        IHttpContextAccessor httpContextAccessor,
        ILogger<BusinessCapabilityAuthorizationHandler> logger)
    {
        _currentBusiness = currentBusiness;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BusinessCapabilityRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.Identity is not { IsAuthenticated: true })
        {
            // No membership lookup for a caller nothing can be recorded against: the authentication
            // middleware answers them 401, and the requirement stays unmet either way.
            return;
        }

        var resolution = await _currentBusiness
            .ResolveAsync(_httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None)
            .ConfigureAwait(false);

        if (resolution.ResolvedRole is not { } role)
        {
            _logger.LogWarning(
                "Capability {Capability} denied: the caller's business membership did not resolve. "
                    + "Reason: {DenialReason}.",
                Capabilities.Name(requirement.Capability),
                resolution.DenialReason ?? BusinessAccessDenialReason.NotAuthenticated);
            return;
        }

        if (RoleCapabilities.Allows(role, requirement.Capability))
        {
            context.Succeed(requirement);
            return;
        }

        // The role is worth logging and the actor is not: which member it was belongs with the
        // request's own correlation id, while an operator reading this needs to know which role was
        // refused what. The caller is told none of it.
        _logger.LogWarning(
            "Capability {Capability} denied: the caller's role {Role} does not grant it.",
            Capabilities.Name(requirement.Capability),
            role);
    }
}
