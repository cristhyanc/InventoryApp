using System.Net;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// Bounded HTTP resilience for the Nayax Lynx client (issue #48): a per-attempt timeout, retry
/// with backoff for transient failures only, and a circuit breaker so a sustained outage fails
/// fast instead of being retried into. Applied as a <see cref="DelegatingHandler"/> around
/// <see cref="NayaxLynxClient"/>'s <c>HttpClient</c> (<c>AddNayaxLynxClient</c>), so retry/timeout/
/// circuit-breaker types stay an Infrastructure concern and never appear in the
/// <c>INayaxLynxClient</c> port <see cref="NayaxLynxClient"/> implements.
///
/// <see cref="NayaxLynxClient"/> itself is unchanged: it still calls <c>HttpClient.GetAsync</c>/
/// <c>PostAsJsonAsync</c> and inspects the final <see cref="HttpResponseMessage"/> exactly as
/// before, unaware that some of those calls were retried underneath it.
/// </summary>
public sealed class NayaxResilienceHandler : DelegatingHandler
{
    private static readonly ResiliencePropertyKey<bool> IsIdempotentKey = new("Nayax.IsIdempotentRequest");
    private static readonly ResiliencePropertyKey<string> RequestPathKey = new("Nayax.RequestPath");

    private readonly ILogger<NayaxResilienceHandler> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public NayaxResilienceHandler(ILogger<NayaxResilienceHandler> logger)
        : this(logger, NayaxResilienceOptions.Default)
    {
    }

    public NayaxResilienceHandler(ILogger<NayaxResilienceHandler> logger, NayaxResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _pipeline = BuildPipeline(options, _logger);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(IsIdempotentKey, IsIdempotent(request.Method));
        // No query string is ever built for a Nayax request, so the path alone is safe to log:
        // it never carries the token, an id from a filter value, or anything else sensitive.
        context.Properties.Set(RequestPathKey, request.RequestUri?.AbsolutePath ?? string.Empty);

        try
        {
            return await _pipeline.ExecuteAsync(
                static (ctx, state) => new ValueTask<HttpResponseMessage>(
                    state.Handler.SendToInnerHandlerAsync(state.Request, ctx.CancellationToken)),
                context,
                (Handler: this, Request: request)).ConfigureAwait(false);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    // Named separately from the overridden SendAsync so the pipeline callback above can reach the
    // base HttpMessageHandler (retrying calls straight into base.SendAsync would recurse into this
    // handler instead of the next one in the chain).
    private Task<HttpResponseMessage> SendToInnerHandlerAsync(HttpRequestMessage request, CancellationToken ct) =>
        base.SendAsync(request, ct);

    private static bool IsIdempotent(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Head;

    // The only place that decides what counts as transient. Caller cancellation is deliberately
    // excluded: by the time an outer strategy observes it, the inner per-attempt timeout has
    // already told genuine caller cancellation apart from an attempt timing out (see BuildPipeline),
    // so an OperationCanceledException here can only be the caller's own token.
    private static bool IsTransientFailure(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is OperationCanceledException)
        {
            return false;
        }

        if (outcome.Exception is HttpRequestException or TimeoutRejectedException)
        {
            return true;
        }

        if (outcome.Exception is not null)
        {
            return false;
        }

        var statusCode = (int)outcome.Result!.StatusCode;
        return statusCode == (int)HttpStatusCode.RequestTimeout ||
            statusCode == (int)HttpStatusCode.TooManyRequests ||
            statusCode is >= 500 and <= 599;
    }

    private static ResiliencePipeline<HttpResponseMessage> BuildPipeline(
        NayaxResilienceOptions options, ILogger<NayaxResilienceHandler> logger)
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = args => new ValueTask<bool>(
                    args.Context.Properties.TryGetValue(IsIdempotentKey, out var idempotent) &&
                    idempotent &&
                    IsTransientFailure(args.Outcome)),
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = options.RetryBaseDelay,
                UseJitter = true,
                OnRetry = args =>
                {
                    // The response being abandoned in favour of a retry is never returned to any
                    // caller, so it must be disposed here or its connection/stream leaks.
                    args.Outcome.Result?.Dispose();
                    args.Context.Properties.TryGetValue(RequestPathKey, out var path);
                    logger.LogWarning(
                        "Nayax request transient failure, retrying. Attempt={NayaxRetryAttempt} Path={NayaxRequestPath} Status={NayaxStatusCode}",
                        args.AttemptNumber + 1,
                        path,
                        args.Outcome.Result?.StatusCode);
                    return default;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = args => new ValueTask<bool>(IsTransientFailure(args.Outcome)),
                FailureRatio = options.CircuitBreakerFailureRatio,
                MinimumThroughput = options.CircuitBreakerMinimumThroughput,
                SamplingDuration = options.CircuitBreakerSamplingDuration,
                BreakDuration = options.CircuitBreakerBreakDuration,
                OnOpened = args =>
                {
                    logger.LogError(
                        "Nayax circuit breaker opened after repeated transient failures. BreakDuration={NayaxCircuitBreakDuration}",
                        args.BreakDuration);
                    return default;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("Nayax circuit breaker closed; upstream calls resumed normally.");
                    return default;
                },
            })
            .AddTimeout(options.AttemptTimeout)
            .Build();
    }
}
