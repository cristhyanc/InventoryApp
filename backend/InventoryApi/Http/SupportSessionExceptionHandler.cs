using System.Diagnostics;
using Inventory.Application.SupportSessions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Http;

// Maps the two support-session refusals that are raised rather than decided (issue #551), and only
// those:
//
//   SupportSessionReadOnlyException            something in a support session tried to write. The
//                                              capability handler's method rule already refuses
//                                              every non-GET call in a session, so reaching here
//                                              means a read with a side effect was stopped at the
//                                              persistence boundary or by document storage.
//   SupportSessionNayaxUnavailableException    a Nayax call was attempted in a support session. The
//                                              business's own operator id and token are never
//                                              resolved for a supporter, so the refusal happens
//                                              before any request leaves the API.
//
// Both are 403 Forbidden, which is the one place this differs from NayaxConnectionExceptionHandler's
// reasoning beside it. Those two failures are 409 because the request conflicts with the business's
// own configuration and the caller is entitled to the feature; these two are refusals of the
// *caller* - a support session grants read access to this business and nothing else - so 403 is
// exactly what they are. Neither is a 500: nothing is broken, and nothing about retrying would help.
//
// The response carries the exception's own fixed, caller-safe sentence plus the stable `code` the
// support banner branches on - the same two codes the authorization result handler publishes for the
// refusals that are decided before an action runs, so a client has one set to switch on. It never
// carries a business name or id, an actor, a session id, a record, or a stack trace.
//
// Each claimed failure is logged exactly once at Warning, with the route template and method and
// nothing from the request's data: a support session trying to write is worth seeing - it means a
// read path has a side effect - and it is not a server error. As in the Nayax handlers, the
// exception object is deliberately not attached; only fields this application composed are logged.
public sealed class SupportSessionExceptionHandler : IExceptionHandler
{
    internal const string ReadOnlyTitle = "Support session is read-only";
    internal const string NayaxUnavailableTitle = "Nayax is unavailable in a support view";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<SupportSessionExceptionHandler> _logger;

    public SupportSessionExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<SupportSessionExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var mapping = Classify(exception);
        if (mapping is null)
        {
            return false;
        }

        var (title, code) = mapping.Value;
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        _logger.LogWarning(
            "A support-session request was refused. SupportSessionErrorCode={SupportSessionErrorCode} "
                + "Method={Method} Route={Route} TraceId={TraceId}",
            code,
            httpContext.Request.Method,
            RouteTemplate(httpContext),
            traceId);

        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;

        var problemDetails = new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = StatusCodes.Status403Forbidden,
        };
        problemDetails.Extensions["traceId"] = traceId;
        problemDetails.Extensions["message"] = exception.Message;
        problemDetails.Extensions["code"] = code;

        await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });

        // The 403 is already committed to the response, so this handler owns the outcome even if
        // content negotiation declined to write a body.
        return true;
    }

    /// <summary>
    /// The matched endpoint's route template, or <see langword="null"/>. It is the template and never
    /// the path, so no id, name or query value a request carried reaches the log.
    /// </summary>
    internal static string? RouteTemplate(HttpContext httpContext) =>
        (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;

    // Only the two Application-owned support-session failures. Never a framework-wide base type:
    // this handler publishes the exception's own message, so it may claim only types whose contract
    // guarantees that message is caller-safe.
    private static (string Title, string Code)? Classify(Exception exception) => exception switch
    {
        SupportSessionReadOnlyException => (ReadOnlyTitle, SupportSessionErrorCodes.ReadOnly),
        SupportSessionNayaxUnavailableException => (
            NayaxUnavailableTitle,
            SupportSessionErrorCodes.NayaxUnavailable),
        _ => null,
    };
}
