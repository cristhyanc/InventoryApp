using System.Diagnostics;
using Inventory.Infrastructure.Nayax;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Http;

// Maps Nayax upstream failures, and only those, to a stable 502 ProblemDetails.
// Every other exception is left to the normal ASP.NET Core error pipeline.
// The public response carries no token, authorization header, upstream body,
// stack trace, or internal path.
//
// An unreachable or refusing Nayax is an infrastructure failure an operator has to be able to
// find, so each one is logged exactly once at Error (issue #165) with the safe diagnostics
// NayaxUpstreamException was designed to carry - the operation, the HTTP method and relative
// endpoint this application built, and the numeric upstream status - plus the request's own
// method, path and trace ID for correlation.
//
// The exception object itself is deliberately NOT attached to that log entry. Its own message is
// safe, but its inner exception is whatever the transport threw, and a transport exception's
// message is outside this application's control: it can repeat a request header or an upstream
// response body. Telemetry is retained and searchable, so the rule from AGENTS.md - never log API
// tokens, authorization headers, or sensitive upstream payloads - is enforced here by logging only
// fields this application composed, never a provider's text. The stack trace is of little
// diagnostic value in exchange: the operation name identifies the call site exactly.
public sealed class NayaxUpstreamExceptionHandler : IExceptionHandler
{
    internal const string ProblemTitle = "Nayax service error";
    internal const string ProblemDetail = "The Nayax service could not complete the request.";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<NayaxUpstreamExceptionHandler> _logger;

    public NayaxUpstreamExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<NayaxUpstreamExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not NayaxUpstreamException upstream)
        {
            return false;
        }

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        _logger.LogError(
            "Nayax upstream failure. Operation={NayaxOperation} UpstreamMethod={UpstreamMethod} "
                + "Endpoint={NayaxEndpoint} UpstreamStatus={UpstreamStatus} Method={Method} "
                + "Path={Path} TraceId={TraceId}",
            upstream.Operation,
            upstream.Method,
            upstream.Endpoint,
            upstream.StatusCodeValue,
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            traceId);

        httpContext.Response.StatusCode = StatusCodes.Status502BadGateway;

        var problemDetails = new ProblemDetails
        {
            Title = ProblemTitle,
            Detail = ProblemDetail,
            Status = StatusCodes.Status502BadGateway,
        };
        problemDetails.Extensions["traceId"] = traceId;

        await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });

        // The 502 status is already committed to the response, so this handler owns
        // the outcome even if content negotiation declined to write a body.
        return true;
    }
}
