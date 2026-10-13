using Inventory.Application.Access;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
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
///
/// <para><strong>A support session is the second source of a capability, and it is read-only (issue
/// #551).</strong> When <see cref="ISupportSessionAccessor"/> reports an active session, the
/// decision is made from <see cref="SupportSessionAccessPolicy"/>'s fixed set instead of a role -
/// the administrator holds none - and <em>additionally</em> from the request's HTTP method: a
/// <c>GET</c> or <c>HEAD</c> may proceed if the capability is in that set, and anything else is
/// refused whatever capability it carried. The two rules are checked in that order, so a write is
/// refused as a write rather than as a missing capability, which is the difference a support banner
/// has to be able to show. A presented session that is no longer usable is refused here too, rather
/// than being allowed to fall through to the administrator's own (non-existent) membership: this
/// handler runs before <see cref="BusinessScopeMiddleware"/>, so without that the first answer the
/// caller got would be a bare membership refusal instead of
/// <see cref="SupportSessionErrorCodes.SessionEnded"/>.</para>
///
/// <para>The support-session state is read from the accessor rather than from the business scope for
/// the same reason the role is resolved here rather than read from it: the authorization middleware
/// runs <em>before</em> the middleware that publishes that scope, and a policy that read it would be
/// one pipeline edit away from passing before anything had been checked.</para>
/// </summary>
public sealed class BusinessCapabilityAuthorizationHandler : AuthorizationHandler<BusinessCapabilityRequirement>
{
    private readonly CurrentCapabilities _capabilities;
    private readonly ISupportSessionAccessor _supportSession;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<BusinessCapabilityAuthorizationHandler> _logger;

    public BusinessCapabilityAuthorizationHandler(
        CurrentCapabilities capabilities,
        ISupportSessionAccessor supportSession,
        IHttpContextAccessor httpContextAccessor,
        ILogger<BusinessCapabilityAuthorizationHandler> logger)
    {
        _capabilities = capabilities;
        _supportSession = supportSession;
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
            // No membership or session lookup for a caller nothing can be recorded against: the
            // authentication middleware answers them 401, and the requirement stays unmet either
            // way.
            return;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        var cancellationToken = httpContext?.RequestAborted ?? CancellationToken.None;
        var support = await _supportSession.ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (support.State == SupportSessionAccessState.Ended)
        {
            // Refused as a dead session, not as a missing membership, and refused outright rather
            // than left merely unmet: a caller who presented a session the server will not honour is
            // told to start another one.
            context.Fail(SupportSessionAuthorizationFailure.Reason(
                this, SupportSessionErrorCodes.SessionEnded));
            return;
        }

        if (support.IsActive && !SupportSessionAccessPolicy.IsReadMethod(httpContext?.Request.Method))
        {
            // Before the capability is even consulted: in a support session no method other than a
            // read is allowed, whatever the endpoint required.
            _logger.LogWarning(
                "Capability {Capability} denied: a support session is read-only and this request's "
                    + "method is not a read.",
                Capabilities.Name(requirement.Capability));

            context.Fail(SupportSessionAuthorizationFailure.Reason(
                this, SupportSessionErrorCodes.ReadOnly));
            return;
        }

        if (await Allows(requirement.Capability, cancellationToken).ConfigureAwait(false))
        {
            context.Succeed(requirement);
            return;
        }

        // Which capability was refused is worth logging and the actor is not: which caller it was
        // belongs with the request's own correlation id, while an operator reading this needs to know
        // what was refused. The caller is told none of it.
        _logger.LogWarning(
            "Capability {Capability} denied: the caller does not hold it.",
            Capabilities.Name(requirement.Capability));
    }

    /// <summary>
    /// Whether this request holds the capability, with the membership refusals that
    /// <see cref="CurrentCapabilities"/> raises turned back into an unmet requirement.
    ///
    /// A caller whose membership does not resolve - missing, revoked, ambiguous, an inactive
    /// business, or a stored role this deployment does not declare - is exactly the caller this
    /// policy exists to refuse, and an exception thrown out of an authorization handler would become
    /// a <c>500</c> rather than the <c>403</c> they have earned.
    /// </summary>
    private async Task<bool> Allows(Capability capability, CancellationToken cancellationToken)
    {
        try
        {
            return await _capabilities.AllowsAsync(capability, cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessAccessDeniedException denied)
        {
            _logger.LogWarning(
                "Capability {Capability} denied: the caller's business membership did not resolve. "
                    + "Reason: {DenialReason}.",
                Capabilities.Name(capability),
                denied.Reason);

            return false;
        }
    }
}

/// <summary>
/// The stable support-session error code carried out of a failed authorization decision (issue
/// #551), and the one way to read it back.
///
/// <para>ASP.NET Core's authorization middleware answers a failed policy with a bare <c>403</c> and
/// no body, which is right for a capability refusal - a caller is told nothing about the role matrix
/// - but wrong for the two support-session refusals, where the code is the whole point: a client has
/// to tell "your session is over" from "this view is read-only". The handler therefore attaches the
/// code as an <see cref="AuthorizationFailureReason"/>, and
/// <see cref="SupportSessionAuthorizationResultHandler"/> turns it into a problem response. A
/// failure with no such reason is left exactly as it was.</para>
///
/// <para>The code is the <em>whole</em> message, so nothing else can leak through it, and the
/// reason object never leaves the server as-is.</para>
/// </summary>
public static class SupportSessionAuthorizationFailure
{
    /// <summary>Builds the failure reason carrying <paramref name="code"/>.</summary>
    public static AuthorizationFailureReason Reason(IAuthorizationHandler handler, string code) =>
        new(handler, code);

    /// <summary>
    /// The support-session code <paramref name="failure"/> carries, or <see langword="null"/> when
    /// it carries none - which is every other authorization failure in the application.
    /// </summary>
    public static string? TryGetCode(AuthorizationFailure? failure) =>
        failure?.FailureReasons.Select(reason => reason.Message).FirstOrDefault(IsSupportSessionCode);

    private static bool IsSupportSessionCode(string? message) =>
        message is SupportSessionErrorCodes.SessionEnded or SupportSessionErrorCodes.ReadOnly;
}
