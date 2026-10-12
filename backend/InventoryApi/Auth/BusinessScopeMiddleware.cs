using Inventory.Application.Businesses;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth.PlatformAdmin;
using Microsoft.AspNetCore.Authorization;

namespace InventoryApi.Auth;

/// <summary>
/// Resolves the authenticated caller's business once per request and publishes it to the
/// persistence layer (issue #64), together with that business's own time zone for the business
/// calendar (issue #499).
///
/// It runs after authentication and before the endpoint, so membership is checked "before any
/// business endpoint reads data" rather than inside each controller. An authenticated caller
/// with no usable membership is refused here with 403 and never reaches an action.
///
/// An unauthenticated request is deliberately left alone: the authorization middleware already
/// answers it with 401, and turning that into a 403 here would hide the difference between "you
/// are not signed in" and "you are signed in but not a member of any business". The scope simply
/// stays denied, so even if such a request did reach persistence it would read and write nothing.
///
/// <para><strong>One endpoint-specific exception (issue #336, extended to a second surface by issue
/// #544).</strong> The platform-admin endpoints - the diagnostics API, and the Super Admin's
/// business directory and decisions - are marked with
/// <see cref="PlatformDiagnosticsEndpointAttribute"/> and may proceed
/// without a business membership - but only after this middleware re-evaluates
/// <see cref="PlatformAdminPolicy"/> and it succeeds. The check is made here, on this request,
/// rather than inferred from the fact that the authorization middleware ran earlier in the
/// pipeline: a membership bypass that depended on middleware ordering would be one pipeline edit
/// away from applying to a caller nobody authorised. Anything else - an unmarked endpoint, a
/// marked endpoint reached by a caller the policy refuses - takes the ordinary membership path, and
/// a business member therefore reaches the diagnostics endpoints no differently than they reach any
/// other endpoint they are not authorised for: refused.</para>
///
/// <para>A bypassed request carries a <em>denied</em> <c>BusinessScope</c>, not an unscoped one.
/// The tenant query filters and <c>BusinessOwnershipEnforcer</c> stay fully in force for it, so a
/// diagnostics request reads nothing at all through <c>AppDbContext</c> and writes nothing
/// anywhere; its cross-business read happens on the separate read-only SQLite connection the
/// diagnostics adapter owns. The Super Admin businesses API runs under exactly the same denied
/// scope: what it reaches through <c>AppDbContext</c> is the two structurally non-tenant-owned
/// tables plus one read-only column of the Nayax connection table, through one narrow store
/// (docs/architecture.md § Super Admin businesses API). "Unrestricted request-scoped context"
/// remains something no request path can obtain.</para>
///
/// <para><strong>A second endpoint-specific exception (issue #523).</strong> An endpoint marked
/// with <see cref="MembershipNotRequiredEndpointAttribute"/> may proceed without a membership for
/// any authenticated caller, because it is the family of endpoints a person with no usable
/// membership has to be able to call - <c>GET /api/me/account-state</c>, which tells them which
/// screen to show instead of a bare 403. No policy is evaluated for it: the only thing such an
/// endpoint may answer is a fact about the caller's own identity, so there is nothing to
/// authorise beyond being signed in. It is the same shape of bypass as the one above and no
/// weaker: the request keeps the <em>denied</em> scope and no business time zone, so a marked
/// endpoint reads no business data at all, and the marker does nothing on any other endpoint. An
/// endpoint carrying both markers stays a diagnostics endpoint, decided by the policy above.</para>
///
/// <para><strong>One thing happens after the response (issue #524).</strong> A request that
/// resolved a membership schedules the member's display name and email address to be refreshed from
/// the claims the same validated token carried. It is scheduling only: the write happens later, in
/// its own service scope with no business resolved, so it cannot delay, change or fail the
/// response - and it is skipped entirely for a request carrying
/// <see cref="SupportSessionHeader"/>. The two values it refreshes decide nothing; this middleware
/// still resolves access from the <c>(tid, oid)</c> pair and the membership row alone.</para>
/// </summary>
public sealed class BusinessScopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<BusinessScopeMiddleware> _logger;

    public BusinessScopeMiddleware(RequestDelegate next, ILogger<BusinessScopeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentBusinessProvider currentBusinessProvider,
        BusinessScope businessScope,
        BusinessTimeZoneScope businessTimeZoneScope,
        IBusinessProfileStore businessProfileStore,
        IAuthorizationService authorizationService,
        MemberProfileRefreshRequestScheduler memberProfileRefreshScheduler)
    {
        if (context.User.Identity is not { IsAuthenticated: true })
        {
            await _next(context);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<PlatformDiagnosticsEndpointAttribute>() is not null)
        {
            var platformAdmin = await authorizationService.AuthorizeAsync(
                context.User,
                resource: null,
                PlatformAdminPolicy.Name);

            if (platformAdmin.Succeeded)
            {
                // The scope stays denied on purpose; see the class remarks.
                await _next(context);
                return;
            }

            _logger.LogWarning(
                "Platform-admin access denied by the business-scope middleware: the "
                    + "platform-admin policy did not succeed for this request.");

            await WriteForbiddenAsync(context, BusinessAccessDenialReason.MembershipMissing);
            return;
        }

        // Membership is not resolved at all for a marked endpoint, rather than resolved and
        // ignored: there is no business to publish either way, and an endpoint that cannot be
        // given a business cannot accidentally be given one later. The caller's own memberships
        // are read by the use case that answers them, past this denied scope.
        if (context.GetEndpoint()?.Metadata.GetMetadata<MembershipNotRequiredEndpointAttribute>() is not null)
        {
            await _next(context);
            return;
        }

        var resolution = await currentBusinessProvider.ResolveAsync(context.RequestAborted);

        if (resolution.ResolvedBusinessId is not { } businessId)
        {
            await WriteForbiddenAsync(context, resolution.DenialReason);
            return;
        }

        businessScope.Resolve(businessId);

        await PublishBusinessTimeZoneAsync(context, businessId, businessTimeZoneScope, businessProfileStore);

        await _next(context);

        SchedulePostResponseProfileRefresh(context, memberProfileRefreshScheduler);
    }

    /// <summary>
    /// Schedules the member's display-name and email refresh (issue #524), after the response has
    /// been decided and only for a request that resolved a membership.
    ///
    /// Everything about where this call sits is deliberate. It is after <c>_next</c>, so the refresh
    /// cannot change or delay what the caller is answered; it only schedules, so the write itself
    /// happens in another service scope with no business resolved at all; and it is reached only on
    /// the path above, which means only for a request whose membership resolved - the row that is
    /// refreshed is the caller's own active membership.
    ///
    /// It also cannot fail the request. Scheduling is a dictionary read and a non-blocking queue
    /// write, neither of which throws, and anything unexpected is swallowed and logged: a display
    /// value is never worth turning a successful response into a failed one.
    /// </summary>
    private void SchedulePostResponseProfileRefresh(
        HttpContext context,
        MemberProfileRefreshRequestScheduler memberProfileRefreshScheduler)
    {
        try
        {
            // A support session is somebody from outside the business looking at the business's
            // data (issue #512). Their token's name and email address are not the member's, so the
            // row must not be refreshed from it - and the header's presence alone decides that,
            // because the only thing it can do here is stop a cosmetic write.
            if (context.Request.Headers.ContainsKey(SupportSessionHeader.Name))
            {
                return;
            }

            memberProfileRefreshScheduler.ScheduleForCurrentActor();
        }
        catch (Exception exception)
        {
            // Value-free on purpose: the display name and the email address must not reach a log.
            _logger.LogWarning(
                exception,
                "Scheduling the member display-name and email refresh failed for this request. The "
                    + "response is unaffected and the member's stored display details stay as they "
                    + "are.");
        }
    }

    /// <summary>
    /// Publishes the resolved business's own IANA time zone for the rest of the request (issue
    /// #499), so <c>IBusinessCalendar</c> derives business dates in that business's calendar
    /// without querying anything itself and without any possibility of a request value choosing a
    /// zone. The business id is the one resolved immediately above, from membership.
    ///
    /// A business with no readable zone leaves the request's zone unresolved rather than being
    /// given one. That is deliberate: the request is not refused here, because most endpoints
    /// derive no business date at all and refusing them would turn a configuration problem into a
    /// total outage, but every business-date derivation in it then fails closed with
    /// <c>BusinessTimeZoneUnavailableException</c>. The blank zone is logged for an operator,
    /// because the only real cause is a business row that never received one.
    /// </summary>
    private async Task PublishBusinessTimeZoneAsync(
        HttpContext context,
        BusinessId businessId,
        BusinessTimeZoneScope businessTimeZoneScope,
        IBusinessProfileStore businessProfileStore)
    {
        var profile = await businessProfileStore.FindAsync(businessId, context.RequestAborted);

        if (string.IsNullOrWhiteSpace(profile?.TimeZoneId))
        {
            _logger.LogError(
                "The current business has no usable time zone, so no business date can be derived "
                    + "for this request. Business dates will fail rather than fall back to another "
                    + "calendar.");
            return;
        }

        businessTimeZoneScope.Resolve(profile.TimeZoneId);
    }

    /// <summary>
    /// The response deliberately carries no detail about why membership failed, and no business
    /// or actor identifier. The denial reason is logged for an operator instead: telling an
    /// unrecognised caller whether a business exists, or whether their membership is merely
    /// ambiguous, is information they have not earned.
    /// </summary>
    private async Task WriteForbiddenAsync(HttpContext context, BusinessAccessDenialReason? reason)
    {
        _logger.LogWarning(
            "Business access denied for an authenticated caller. Reason: {DenialReason}.",
            reason ?? BusinessAccessDenialReason.MembershipMissing);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
            title = "Forbidden",
            status = StatusCodes.Status403Forbidden,
            detail = "The signed-in account is not associated with a business in this application.",
            traceId = context.TraceIdentifier,
        });
    }
}
