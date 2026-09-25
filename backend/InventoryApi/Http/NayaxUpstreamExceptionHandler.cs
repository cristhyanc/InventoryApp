using System.Diagnostics;
using Inventory.Infrastructure.Nayax;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Http;

// Maps Nayax upstream failures, and only those, to a stable 502 ProblemDetails.
// Every other exception is left to the normal ASP.NET Core error pipeline.
// The public response carries no token, authorization header, upstream body,
// stack trace, or internal path.
public sealed class NayaxUpstreamExceptionHandler : IExceptionHandler
{
    internal const string ProblemTitle = "Nayax service error";
    internal const string ProblemDetail = "The Nayax service could not complete the request.";

    private readonly IProblemDetailsService _problemDetailsService;

    public NayaxUpstreamExceptionHandler(IProblemDetailsService problemDetailsService)
    {
        _problemDetailsService = problemDetailsService;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not NayaxUpstreamException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status502BadGateway;

        var problemDetails = new ProblemDetails
        {
            Title = ProblemTitle,
            Detail = ProblemDetail,
            Status = StatusCodes.Status502BadGateway,
        };
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

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
