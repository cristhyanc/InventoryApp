using System.Diagnostics;
using Inventory.Application.Exceptions;
using InventoryApi.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Http;

/// <summary>
/// Maps domain/application validation and conflict failures to stable, safe ProblemDetails
/// responses, so controllers no longer need their own repetitive try/catch. It recognizes the new
/// typed exceptions (<see cref="DomainValidationException"/>, <see cref="DomainConflictException"/>)
/// plus the pre-existing exception types the controllers already caught one by one
/// (<see cref="InsufficientStockException"/>, <see cref="ArgumentException"/>,
/// <see cref="InvalidOperationException"/>), so centralizing the mapping preserves the exact
/// status code and message every caller already receives today.
///
/// <see cref="InventoryCostDataQualityException"/> is deliberately excluded even though
/// it derives from <see cref="InvalidOperationException"/>: it signals a data-integrity problem
/// found while rebuilding costing history, not a routine request-validation failure, and today it
/// is not caught anywhere, so it already surfaces as an unexpected error. This handler leaves that
/// behavior unchanged rather than silently reclassifying it as a client validation error.
///
/// Every other exception, and every Nayax upstream failure (already claimed by
/// <see cref="NayaxUpstreamExceptionHandler"/>, registered before this handler), returns
/// <see langword="false"/> and falls through to the general exception handler.
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
        if (exception is DomainConflictException)
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

    private static (int Status, string Title)? Classify(Exception exception) => exception switch
    {
        InventoryCostDataQualityException => null,
        DomainValidationException => (StatusCodes.Status400BadRequest, ValidationTitle),
        DomainConflictException => (StatusCodes.Status409Conflict, ConflictTitle),
        InsufficientStockException => (StatusCodes.Status400BadRequest, ValidationTitle),
        ArgumentException => (StatusCodes.Status400BadRequest, ValidationTitle),
        InvalidOperationException => (StatusCodes.Status400BadRequest, ValidationTitle),
        _ => null,
    };
}
