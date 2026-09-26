using System.Diagnostics;
using Inventory.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Http;

/// <summary>
/// Maps domain/application validation and conflict failures to stable, safe ProblemDetails
/// responses, so controllers no longer need their own repetitive try/catch.
///
/// It only claims exception types whose contract guarantees a caller-safe message, all defined in
/// the <c>Inventory.Domain.Exceptions</c> hierarchy that this handler translates to HTTP:
/// <see cref="DomainValidationException"/> and <see cref="DomainConflictException"/> (both
/// documented as carrying a message written for the caller), and its subclass
/// <see cref="InsufficientStockException"/>, whose message is a fixed sentence plus an available
/// stock count the caller is already entitled to see.
///
/// It deliberately does not claim framework-wide types such as
/// <see cref="ArgumentException"/> or <see cref="InvalidOperationException"/>. Those are thrown
/// all over the BCL, EF Core, and this application's own infrastructure (tenant scope resolution,
/// costing data-quality checks, report export), and their messages are written for a developer,
/// not for an API client. Claiming them here would turn unrelated internal failures into public
/// 400 responses that echo an internal message and are never logged. Every one of them - like any
/// other unrecognized exception - returns <see langword="false"/> and falls through to
/// <see cref="GlobalExceptionHandler"/>, which logs it once at <see cref="LogLevel.Error"/> and
/// answers with a generic 500 that carries no message. A deliberate validation check that should
/// reach the caller must therefore throw <see cref="DomainValidationException"/> or
/// <see cref="DomainConflictException"/> at the throw site.
///
/// Nayax upstream failures are already claimed by <see cref="NayaxUpstreamExceptionHandler"/>,
/// which is registered before this handler.
/// </summary>
public sealed class DomainExceptionHandler : IExceptionHandler
{
    internal const string ValidationTitle = "Request validation failed";
    internal const string ConflictTitle = "The request conflicts with the current state";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<DomainExceptionHandler> _logger;

    public DomainExceptionHandler(IProblemDetailsService problemDetailsService, ILogger<DomainExceptionHandler> logger)
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

        var (status, title) = mapping.Value;
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // A conflict is worth an operator's attention (it may indicate a real race between two
        // callers), but it is still an expected outcome, not a server error - warning only.
        // InsufficientStockException is excluded even though it now derives from
        // DomainConflictException: its established mapping (400, not logged) predates that
        // inheritance and must not change because of it.
        if (exception is DomainConflictException && exception is not InsufficientStockException)
        {
            _logger.LogWarning(
                "Domain conflict. TraceId={TraceId} Detail={DomainConflictDetail}",
                traceId,
                exception.Message);
        }

        httpContext.Response.StatusCode = status;

        var problemDetails = new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = status,
        };
        problemDetails.Extensions["traceId"] = traceId;
        // Kept alongside the standard `detail` field because existing frontend error handling
        // already reads `error.message` for this exact migration; see machine-detail.component.ts.
        problemDetails.Extensions["message"] = exception.Message;

        await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });

        return true;
    }

    // Only exception types whose own contract guarantees a caller-safe message may appear here.
    // Never add a framework-wide base type such as ArgumentException or InvalidOperationException:
    // that would publish internal messages from throw sites nobody reviewed.
    //
    // InsufficientStockException must stay listed ahead of DomainConflictException: it is now a
    // subclass of DomainConflictException (see Inventory.Domain.Exceptions.InsufficientStockException),
    // and a C# type-pattern switch matches arms in source order, so its own arm has to come first to
    // keep its established 400 mapping instead of falling through to the base type's 409.
    private static (int Status, string Title)? Classify(Exception exception) => exception switch
    {
        InsufficientStockException => (StatusCodes.Status400BadRequest, ValidationTitle),
        DomainValidationException => (StatusCodes.Status400BadRequest, ValidationTitle),
        DomainConflictException => (StatusCodes.Status409Conflict, ConflictTitle),
        _ => null,
    };
}
