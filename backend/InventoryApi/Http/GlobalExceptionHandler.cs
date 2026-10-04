using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Http;

/// <summary>
/// Last-resort handler for anything <see cref="NayaxUpstreamExceptionHandler"/> and
/// <see cref="DomainExceptionHandler"/> did not claim. It logs the full exception once at
/// <see cref="LogLevel.Error"/> - with the trace ID that also goes to the client, so the two can
/// be correlated - and returns a fixed-shape, generic 500 <c>ProblemDetails</c> response that
/// carries no exception message, type name, or stack trace.
///
/// The log entry carries <c>Method</c>, <c>Path</c> and <c>TraceId</c> as named structured
/// properties (issue #165), so Azure Monitor OpenTelemetry exports them as queryable custom
/// dimensions and an operator can find a reported failure from the trace ID the caller received.
/// The request context stops there on purpose: the query string and the request headers are the
/// two parts of a request that routinely carry a credential - a bearer token, a cookie - and
/// retained telemetry is exactly the wrong place for either, so neither is logged.
///
/// Caller cancellation is not an unexpected error: it is excluded so it is left to ASP.NET Core's
/// normal handling instead of being logged as a server failure, matching the same principle
/// already documented for the Nayax upstream handler.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    internal const string ProblemTitle = "An unexpected error occurred";
    internal const string ProblemDetail =
        "The request could not be completed. Contact support with the trace identifier if this persists.";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(IProblemDetailsService problemDetailsService, ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return false;
        }

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        _logger.LogError(
            exception,
            "Unhandled exception. Method={Method} Path={Path} TraceId={TraceId}",
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

        await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });

        return true;
    }
}
