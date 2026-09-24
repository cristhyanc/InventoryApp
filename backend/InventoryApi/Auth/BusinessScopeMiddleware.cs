using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Auth;

/// <summary>
/// Resolves the authenticated caller's business once per request and publishes it to the
/// persistence layer (issue #64).
///
/// It runs after authentication and before the endpoint, so membership is checked "before any
/// business endpoint reads data" rather than inside each controller. An authenticated caller
/// with no usable membership is refused here with 403 and never reaches an action.
///
/// An unauthenticated request is deliberately left alone: the authorization middleware already
/// answers it with 401, and turning that into a 403 here would hide the difference between "you
/// are not signed in" and "you are signed in but not a member of any business". The scope simply
/// stays denied, so even if such a request did reach persistence it would read and write nothing.
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
        BusinessScope businessScope)
    {
        if (context.User.Identity is not { IsAuthenticated: true })
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

        await _next(context);
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
