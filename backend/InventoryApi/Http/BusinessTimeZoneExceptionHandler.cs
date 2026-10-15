using System.Diagnostics;
using Inventory.Application.Time;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Http;

/// <summary>
/// Maps <see cref="BusinessTimeZoneUnavailableException"/> (issue #499) to a 500 carrying a
/// <em>stable error code</em> (issue #681), and nothing else about the failure.
///
/// <para><strong>Why it is not left to <see cref="GlobalExceptionHandler"/>.</strong> Before this
/// handler, a business whose stored IANA time zone the host cannot resolve got the same anonymous
/// 500 as a null reference: a client could not tell "this business has no usable time zone, which an
/// Owner can correct in Business Settings" from "something broke". That is a distinguishable,
/// recurring, actionable state, and <c>code</c> is how every other such state in this API is
/// distinguished (see <see cref="NayaxConnectionExceptionHandler"/> and
/// <c>SupportSessionExceptionHandler</c>).</para>
///
/// <para><strong>It stays a 500.</strong> Refusing to derive a business date is a server-side
/// configuration fault, not a conflict the caller created and not anything their request could have
/// avoided: every report, dashboard and period read of that business fails the same way until a
/// human fixes the stored zone. Keeping the status is also what makes this a purely additive change
/// to the API contract - the body gains a code, no caller's status handling changes.</para>
///
/// <para><strong>What the response does not carry.</strong> Not the stored time-zone id, not the
/// exception message, not the business, not a stack trace. The id is configuration an Owner typed
/// and the message names it, so it goes to the server log - which is where the operator who has to
/// correct it reads - and the caller gets the fixed sentence plus the trace id to quote.</para>
/// </summary>
public sealed class BusinessTimeZoneExceptionHandler : IExceptionHandler
{
    /// <summary>
    /// The stable code a client branches on. Snake case, exactly like the support-session codes, so
    /// the whole API reads one way.
    /// </summary>
    internal const string ErrorCode = "business_time_zone_unavailable";

    internal const string ProblemTitle = "The business time zone is unavailable";

    /// <summary>
    /// The one sentence the caller is given. It says what cannot be done and who can fix it, and
    /// names no zone id, business or actor.
    /// </summary>
    internal const string ProblemDetail =
        "This business has no usable time zone, so no business date could be worked out and the "
            + "request was not completed. An Owner can set the business time zone in Admin → "
            + "Business Settings. Contact support with the trace identifier if it is already set.";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<BusinessTimeZoneExceptionHandler> _logger;

    public BusinessTimeZoneExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<BusinessTimeZoneExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BusinessTimeZoneUnavailableException unavailable)
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(httpContext);

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Logged once, with the exception, because the message is the part an operator needs: it
        // names the unusable stored id, which is the thing to correct. Method, Path and TraceId
        // match the other handlers' structured properties, and the query string and headers stay out
        // for the reason GlobalExceptionHandler documents.
        _logger.LogError(
            unavailable,
            "No business time zone could be resolved for this request. ErrorCode={ErrorCode} "
                + "Method={Method} Path={Path} TraceId={TraceId}",
            ErrorCode,
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            traceId);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var problemDetails = new ProblemDetails
        {
            Title = ProblemTitle,
            Detail = ProblemDetail,
            Status = StatusCodes.Status500InternalServerError,
        };
        problemDetails.Extensions["traceId"] = traceId;
        // Kept beside the standard `detail` field for the same reason DomainExceptionHandler keeps
        // it: existing frontend error handling reads `error.message`.
        problemDetails.Extensions["message"] = ProblemDetail;
        problemDetails.Extensions["code"] = ErrorCode;

        await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });

        // The status is already committed to the response, so this handler owns the outcome even if
        // content negotiation declined to write a body.
        return true;
    }
}
