using System.Net;
using Inventory.Application.Time;
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
///
/// Issue #844 bounded the rate-limited case separately. A request still gets at most 3 retries in
/// total, whatever mix of failures it meets, but at most one of them may follow a
/// <c>429 Too Many Requests</c>: a rate-limited Nayax that answers 429 twice is told to stop rather
/// than sent two more copies of the same request, and the wait before that single retry is the one
/// the upstream asked for when <see cref="NayaxRetryAfterPolicy"/> accepts it.
/// </summary>
public sealed class NayaxResilienceHandler : DelegatingHandler
{
    /// <summary>
    /// Retries that may follow a 429 on one request. The total retry budget
    /// (<see cref="NayaxResilienceOptions.MaxRetryAttempts"/>) still applies on top of this: a 429
    /// never buys an extra attempt, it only spends one of the attempts already budgeted.
    /// </summary>
    private const int MaxRetriesAfterTooManyRequests = 1;

    private static readonly ResiliencePropertyKey<bool> IsIdempotentKey = new("Nayax.IsIdempotentRequest");
    private static readonly ResiliencePropertyKey<string> RequestPathKey = new("Nayax.RequestPath");
    private static readonly ResiliencePropertyKey<int> TooManyRequestsRetriesKey = new("Nayax.TooManyRequestsRetries");

    /// <summary>
    /// The wait <see cref="NayaxRetryAfterPolicy"/> accepted for the 429 being retried, or
    /// <see langword="null"/> for the ordinary jittered backoff. Decided once, in
    /// <c>ShouldHandle</c>, so the clock is read once per outcome and the delay the request waits is
    /// exactly the delay the decision to retry was taken on.
    /// </summary>
    private static readonly ResiliencePropertyKey<TimeSpan?> HonouredRetryAfterKey = new("Nayax.HonouredRetryAfter");

    private readonly ILogger<NayaxResilienceHandler> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public NayaxResilienceHandler(ILogger<NayaxResilienceHandler> logger, IClock clock)
        : this(logger, clock, NayaxResilienceOptions.Default)
    {
    }

    public NayaxResilienceHandler(
        ILogger<NayaxResilienceHandler> logger, IClock clock, NayaxResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _pipeline = BuildPipeline(options, clock, _logger);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(IsIdempotentKey, IsIdempotent(request.Method));
        // No query string is ever built for a Nayax request, so the path alone is safe to log:
        // it never carries the token, an id from a filter value, or anything else sensitive.
        context.Properties.Set(RequestPathKey, request.RequestUri?.AbsolutePath ?? string.Empty);
        // Both counted per request, because the 429 budget is per request: a pooled context may
        // carry a previous request's values, so neither is left to whatever it was last time.
        context.Properties.Set(TooManyRequestsRetriesKey, 0);
        context.Properties.Set(HonouredRetryAfterKey, null);

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

    private static bool IsTooManyRequests(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is null && outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests;

    // Whether this transient failure may be retried, and - for a 429 - how long the retry waits.
    // Both answers are decided together, on one reading of the clock, because a 429 whose
    // Retry-After asks for longer than the policy honours is not retried at all (issue #844).
    private static bool ShouldRetry(
        RetryPredicateArguments<HttpResponseMessage> args, IClock clock)
    {
        if (!args.Context.Properties.TryGetValue(IsIdempotentKey, out var idempotent) || !idempotent)
        {
            return false;
        }

        if (!IsTransientFailure(args.Outcome))
        {
            return false;
        }

        if (!IsTooManyRequests(args.Outcome))
        {
            args.Context.Properties.Set(HonouredRetryAfterKey, null);
            return true;
        }

        if (args.Context.Properties.GetValue(TooManyRequestsRetriesKey, 0) >= MaxRetriesAfterTooManyRequests)
        {
            // A second 429 on one request ends it at once: the upstream has now said twice that we
            // are sending too many requests, and another attempt is exactly what it asked against.
            return false;
        }

        var decision = NayaxRetryAfterPolicy.Decide(args.Outcome.Result!.Headers, clock.UtcNow);
        args.Context.Properties.Set(HonouredRetryAfterKey, decision.HonouredDelay);
        return decision.ShouldRetry;
    }

    private static ResiliencePipeline<HttpResponseMessage> BuildPipeline(
        NayaxResilienceOptions options, IClock clock, ILogger<NayaxResilienceHandler> logger)
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = args => new ValueTask<bool>(ShouldRetry(args, clock)),
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = options.RetryBaseDelay,
                UseJitter = true,
                // Returning null leaves the jittered exponential backoff above in charge, which is
                // what every retry except an honoured 429 uses.
                DelayGenerator = args => new ValueTask<TimeSpan?>(
                    args.Context.Properties.GetValue(HonouredRetryAfterKey, null)),
                OnRetry = args =>
                {
                    // The response being abandoned in favour of a retry is never returned to any
                    // caller, so it must be disposed here or its connection/stream leaks.
                    var isTooManyRequests = IsTooManyRequests(args.Outcome);
                    args.Outcome.Result?.Dispose();
                    if (isTooManyRequests)
                    {
                        args.Context.Properties.Set(
                            TooManyRequestsRetriesKey,
                            args.Context.Properties.GetValue(TooManyRequestsRetriesKey, 0) + 1);
                    }

                    args.Context.Properties.TryGetValue(RequestPathKey, out var path);
                    logger.LogWarning(
                        "Nayax request transient failure, retrying. Attempt={NayaxRetryAttempt} Path={NayaxRequestPath} Status={NayaxStatusCode} Delay={NayaxRetryDelay}",
                        args.AttemptNumber + 1,
                        path,
                        args.Outcome.Result?.StatusCode,
                        args.RetryDelay);
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
