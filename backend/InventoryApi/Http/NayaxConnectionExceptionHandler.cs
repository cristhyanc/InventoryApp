using System.Diagnostics;
using Inventory.Application.Nayax;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Http;

// Maps the two per-business Nayax connection failures (issue #520), and only those, to stable
// ProblemDetails responses the frontend can show:
//
//   NayaxNotConnectedException          the current business has no Nayax credentials the status
//                                       gate will use - nothing stored, or stored and known not to
//                                       work. An operator has to connect or reconnect Nayax.
//   NayaxPermissionNotGrantedException  Nayax refused one feature for lack of permission while the
//                                       stored credentials were not yet verified. The connection as
//                                       a whole may be fine.
//
// Both are 409 Conflict, for the same reason DomainConflictException is: the request conflicts with
// the current state of the business's own configuration. Neither is a caller authorization failure,
// so neither may become 401 or 403 - those statuses belong to the caller's own Entra token and
// business membership, and reusing them here would tell a signed-in member they are not allowed to
// use a feature they are perfectly entitled to. Neither is a 502 either: Nayax answered correctly,
// and nothing about retrying would help.
//
// The response carries the exception's own message - a fixed, caller-safe sentence declared as a
// constant on each type - plus the stable `code` a client may branch on, and, for a connection
// failure, the connection's own status so a screen can eventually say "connect" or "reconnect"
// (issue #329). It never carries the operator id, the token, a business identifier, an upstream
// response body or a stack trace.
//
// Each claimed failure is logged exactly once at Warning: it is an expected outcome that needs an
// operator's attention, not a server error, and it is how a "Nayax stopped working" report is
// traced back to a status rather than to an outage. As in NayaxUpstreamExceptionHandler, the
// exception object is deliberately not attached - only fields this application composed are logged.
public sealed class NayaxConnectionExceptionHandler : IExceptionHandler
{
    internal const string NotConnectedTitle = "Nayax is not connected";
    internal const string PermissionNotGrantedTitle = "Nayax permission not granted";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<NayaxConnectionExceptionHandler> _logger;

    public NayaxConnectionExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<NayaxConnectionExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var mapping = Classify(exception);
        if (mapping is null)
        {
            return false;
        }

        var (title, code, detailProperty) = mapping.Value;
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        _logger.LogWarning(
            "Nayax connection unavailable. NayaxErrorCode={NayaxErrorCode} NayaxConnectionDetail={NayaxConnectionDetail} "
                + "Method={Method} Path={Path} TraceId={TraceId}",
            code,
            detailProperty,
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            traceId);

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        var problemDetails = new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = StatusCodes.Status409Conflict,
        };
        problemDetails.Extensions["traceId"] = traceId;
        // Kept alongside the standard `detail` field for the same reason DomainExceptionHandler
        // keeps it: existing frontend error handling reads `error.message`.
        problemDetails.Extensions["message"] = exception.Message;
        problemDetails.Extensions["code"] = code;
        if (exception is NayaxNotConnectedException notConnected)
        {
            problemDetails.Extensions["nayaxConnectionStatus"] = notConnected.Status.ToString();
        }

        await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });

        // The 409 status is already committed to the response, so this handler owns the outcome
        // even if content negotiation declined to write a body.
        return true;
    }

    // Only the two Application-owned Nayax connection failures. Never a framework-wide base type,
    // and never an Infrastructure provider exception: this handler publishes the exception's own
    // message, so it may claim only types whose contract guarantees that message is caller-safe.
    private static (string Title, string Code, string Detail)? Classify(Exception exception) => exception switch
    {
        NayaxNotConnectedException notConnected => (
            NotConnectedTitle,
            NayaxNotConnectedException.ErrorCode,
            notConnected.Status.ToString()),
        NayaxPermissionNotGrantedException permission => (
            PermissionNotGrantedTitle,
            NayaxPermissionNotGrantedException.ErrorCode,
            permission.Operation),
        _ => null,
    };
}
