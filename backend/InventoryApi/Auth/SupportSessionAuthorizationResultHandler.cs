using System.Diagnostics;
using Inventory.Application.SupportSessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Auth;

/// <summary>
/// Gives the two support-session authorization refusals a body a client can branch on (issue #551),
/// and changes nothing else.
///
/// <para>The authorization middleware's default answer to a failed policy is a bare <c>403</c> with
/// no body. That is deliberately right for a capability refusal - a caller is told nothing about the
/// capability they lacked, their role, or the role matrix - and this handler keeps it exactly as it
/// is for every one of them, by delegating to the default implementation whenever the failure
/// carries no support-session code.</para>
///
/// <para>It is wrong for the two refusals this slice adds, where the code <em>is</em> the answer:
/// "your session is over, start another one"
/// (<see cref="SupportSessionErrorCodes.SessionEnded"/>) and "this view is read-only"
/// (<see cref="SupportSessionErrorCodes.ReadOnly"/>) need telling apart by the support banner, and a
/// client cannot tell them apart from a status code alone. The body is RFC 7807 with the same
/// <c>code</c>/<c>message</c> extensions the application's other handled failures carry, and it
/// names no business, no actor, no capability and no session id.</para>
///
/// <para>It is the authorization <em>result</em> handler rather than an exception handler because
/// nothing threw: the policy simply did not pass, which the middleware reports rather than raises.
/// The matching refusals that happen later, at the persistence boundary and in document storage, are
/// exceptions and are mapped by <c>SupportSessionExceptionHandler</c> to the same codes.</para>
/// </summary>
public sealed class SupportSessionAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        var code = authorizeResult.Forbidden
            ? SupportSessionAuthorizationFailure.TryGetCode(authorizeResult.AuthorizationFailure)
            : null;

        if (code is null)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
            return;
        }

        var detail = code == SupportSessionErrorCodes.SessionEnded
            ? SessionEndedMessage
            : SupportSessionReadOnlyException.RefusalMessage;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";

        var problemDetails = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
            Title = "Forbidden",
            Detail = detail,
            Status = StatusCodes.Status403Forbidden,
        };
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        // Kept beside the standard `detail` for the reason DomainExceptionHandler keeps it: the
        // existing frontend error handling reads `error.message`.
        problemDetails.Extensions["message"] = detail;
        problemDetails.Extensions["code"] = code;

        await context.Response.WriteAsJsonAsync(problemDetails).ConfigureAwait(false);
    }

    /// <summary>
    /// What a caller whose presented session is no longer usable is told. One sentence for all four
    /// ways it can happen - it was never theirs, it was ended, it expired, the business was
    /// deactivated - because three of them are facts about somebody else's session or about a
    /// business this caller is no longer entitled to anything about.
    /// </summary>
    internal const string SessionEndedMessage =
        "This support session is no longer active. Start a new one to continue.";
}
