#nullable enable

using System.Globalization;
using System.Net;
using Inventory.Application.Time;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Application.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

// Exercises NayaxResilienceHandler directly, below NayaxLynxClient: a scripted/delegating inner
// HttpMessageHandler stands in for the real Nayax transport so retry counts, timeout behaviour,
// and cancellation are deterministic and never touch the network.
public class NayaxResilienceHandlerTests
{
    private static NayaxResilienceOptions FastRetryOptions(int maxRetryAttempts = 3) => new()
    {
        AttemptTimeout = TimeSpan.FromMilliseconds(300),
        MaxRetryAttempts = maxRetryAttempts,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        CircuitBreakerFailureRatio = 0.5,
        CircuitBreakerMinimumThroughput = 1000, // effectively disabled unless a test lowers it
        CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(30),
        CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task Retryable_5xx_on_a_GET_is_retried_until_it_succeeds()
    {
        var inner = new ScriptedHandler((attempt, _, _) => Respond(attempt < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.RequestCount);
    }

    [Fact]
    public async Task Retries_are_bounded_and_the_final_failure_status_is_returned_to_the_caller()
    {
        var inner = new ScriptedHandler((_, _, _) => Respond(HttpStatusCode.ServiceUnavailable));
        var options = FastRetryOptions(maxRetryAttempts: 3);
        using var client = CreateClient(inner, options);

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // One initial attempt plus the configured number of retries - never unbounded.
        Assert.Equal(options.MaxRetryAttempts + 1, inner.RequestCount);
    }

    [Fact]
    public async Task TooManyRequests_429_on_a_GET_is_treated_as_transient_and_retried()
    {
        var inner = new ScriptedHandler((attempt, _, _) => Respond(attempt == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
    }

    // Issue #844: a rate-limited Nayax must not be sent three more copies of the same request.
    // One retry may follow a 429; a second 429 on the same request stops retrying at once, and the
    // caller gets the 429 - which NayaxLynxClient turns into today's NayaxUpstreamException/502.
    [Fact]
    public async Task A_second_429_on_one_request_stops_retrying_at_once()
    {
        var inner = new ScriptedHandler((_, _, _) => Respond(HttpStatusCode.TooManyRequests));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task A_429_is_still_retried_once_after_earlier_transient_failures()
    {
        var inner = new ScriptedHandler((attempt, _, _) => Respond(attempt switch
        {
            1 or 2 => HttpStatusCode.ServiceUnavailable,
            3 => HttpStatusCode.TooManyRequests,
            _ => HttpStatusCode.OK,
        }));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, inner.RequestCount);
    }

    [Fact]
    public async Task A_second_429_stops_retrying_even_with_retry_budget_left()
    {
        var inner = new ScriptedHandler((attempt, _, _) => Respond(
            attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        // 503, 429, 429: the second 429 ends the request although one retry was still budgeted.
        Assert.Equal(3, inner.RequestCount);
    }

    [Fact]
    public async Task A_429_after_the_retry_budget_is_spent_is_returned_without_a_further_attempt()
    {
        var inner = new ScriptedHandler((attempt, _, _) => Respond(
            attempt <= 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(4, inner.RequestCount);
    }

    [Fact]
    public async Task A_retry_after_a_429_does_not_raise_the_total_retry_budget()
    {
        var inner = new ScriptedHandler((attempt, _, _) => Respond(
            attempt == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // 429, 503, 503, 503: four attempts in total, never five.
        Assert.Equal(4, inner.RequestCount);
    }

    [Fact]
    public async Task A_429_that_follows_an_attempt_timeout_is_retried_once_within_the_budget()
    {
        var inner = new ScriptedHandler(async (attempt, _, ct) =>
        {
            if (attempt == 1)
            {
                // Deliberately longer than AttemptTimeout, so this attempt is a timeout.
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }

            return new HttpResponseMessage(attempt == 2 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
        });
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.RequestCount);
    }

    [Fact]
    public async Task A_429_asking_for_longer_than_the_honoured_maximum_is_not_retried_at_all()
    {
        var inner = new ScriptedHandler((_, _, _) => RespondWithRetryAfter(HttpStatusCode.TooManyRequests, "30"));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task A_429_with_an_honoured_Retry_After_waits_for_it_before_the_single_retry()
    {
        var inner = new ScriptedHandler((attempt, _, _) => attempt == 1
            ? RespondWithRetryAfter(HttpStatusCode.TooManyRequests, "2")
            : Respond(HttpStatusCode.OK));
        using var client = CreateClient(inner, FastRetryOptions());

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.GetAsync("devices");
        elapsed.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
        // The honoured two seconds, not the 1 ms test backoff: a lower bound only, because a
        // loaded machine may wait longer but must never retry sooner than Nayax asked.
        Assert.True(
            elapsed.Elapsed >= TimeSpan.FromSeconds(1.5),
            $"Retry-After: 2 must be waited out before the retry, but the call took {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task A_429_with_a_malformed_Retry_After_is_retried_once_after_the_base_backoff()
    {
        var inner = new ScriptedHandler((attempt, _, _) => attempt == 1
            ? RespondWithRetryAfter(HttpStatusCode.TooManyRequests, "soon-ish")
            : Respond(HttpStatusCode.OK));
        using var client = CreateClient(inner, FastRetryOptions());

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.GetAsync("devices");
        elapsed.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
        // A header this handler cannot parse is the same as no header: the existing jittered
        // backoff, never a wait invented from an unparseable value.
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"A malformed Retry-After must fall back to the base backoff, but the call took {elapsed.Elapsed}.");
    }

    // The injected clock, not the host's own reading of the time, decides what an HTTP-date
    // Retry-After asks for: the same header is honoured or refused purely by where the clock says
    // "now" is. (NayaxRetryAfterPolicyTests states the whole parsing table row by row.)
    [Fact]
    public async Task An_HTTP_date_Retry_After_is_measured_from_the_applications_own_clock()
    {
        var retryAt = new DateTime(2026, 10, 23, 9, 30, 0, DateTimeKind.Utc);
        var inner = new ScriptedHandler((_, _, _) => RespondWithRetryAfter(
            HttpStatusCode.TooManyRequests, retryAt.ToString("R", CultureInfo.InvariantCulture)));
        // An hour before the stated instant: further ahead than the honoured maximum, so no retry.
        using var client = CreateClient(inner, FastRetryOptions(), clock: new FakeClock(retryAt.AddHours(-1)));

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task An_HTTP_date_Retry_After_already_past_on_that_clock_is_retried_once_after_the_base_backoff()
    {
        var retryAt = new DateTime(2026, 10, 23, 9, 30, 0, DateTimeKind.Utc);
        var inner = new ScriptedHandler((attempt, _, _) => attempt == 1
            ? RespondWithRetryAfter(
                HttpStatusCode.TooManyRequests, retryAt.ToString("R", CultureInfo.InvariantCulture))
            : Respond(HttpStatusCode.OK));
        using var client = CreateClient(inner, FastRetryOptions(), clock: new FakeClock(retryAt.AddHours(1)));

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.GetAsync("devices");
        elapsed.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"An elapsed Retry-After instant must fall back to the base backoff, but the call took {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task A_429_on_a_POST_write_is_never_retried()
    {
        var inner = new ScriptedHandler((_, _, _) => RespondWithRetryAfter(HttpStatusCode.TooManyRequests, "1"));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.PostAsync("machines/42/machineProducts", new StringContent("[]"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task Caller_cancellation_while_a_honoured_Retry_After_is_being_waited_out_stays_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedHandler((_, _, _) =>
        {
            // Withdrawn by the caller exactly while the handler is waiting out the ten seconds
            // Nayax asked for: that must surface as cancellation, not as an upstream failure.
            cts.Cancel();
            return RespondWithRetryAfter(HttpStatusCode.TooManyRequests, "10");
        });
        using var client = CreateClient(inner, FastRetryOptions());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("devices", cts.Token));

        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task A_429_still_counts_as_a_failure_for_the_circuit_breaker()
    {
        var inner = new ScriptedHandler((_, _, _) => Respond(HttpStatusCode.TooManyRequests));
        var options = new NayaxResilienceOptions
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(300),
            MaxRetryAttempts = 1,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerMinimumThroughput = 4,
            CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(30),
            CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30),
        };
        using var client = CreateClient(inner, options);

        // POST (never retried) keeps this to exactly one sampled outcome per call, as in the
        // breaker test below.
        for (var i = 0; i < 4; i++)
        {
            var response = await client.PostAsync("machines/1/machineProducts", new StringContent("[]"));
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var requestsBeforeBreakerOpened = inner.RequestCount;
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => client.PostAsync("machines/1/machineProducts", new StringContent("[]")));

        Assert.Equal(requestsBeforeBreakerOpened, inner.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Authentication_authorization_and_validation_4xx_on_a_GET_is_never_retried(HttpStatusCode status)
    {
        var inner = new ScriptedHandler((_, _, _) => Respond(status));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task Retryable_5xx_on_a_POST_write_is_never_retried()
    {
        var inner = new ScriptedHandler((_, _, _) => Respond(HttpStatusCode.ServiceUnavailable));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.PostAsync("machines/42/machineProducts", new StringContent("[]"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task Network_failure_on_a_GET_is_treated_as_transient_and_retried()
    {
        var inner = new ScriptedHandler((attempt, _, _) => attempt == 1
            ? throw new HttpRequestException("connection refused")
            : Respond(HttpStatusCode.OK));
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task An_attempt_exceeding_the_timeout_is_retried_as_transient()
    {
        var inner = new ScriptedHandler(async (attempt, _, ct) =>
        {
            if (attempt == 1)
            {
                // Deliberately longer than AttemptTimeout; the linked token created by the
                // timeout strategy must cancel this before the real 30-second delay elapses.
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateClient(inner, FastRetryOptions());

        var response = await client.GetAsync("devices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_cancellation_not_as_a_retried_or_transient_failure()
    {
        var inner = new ScriptedHandler((_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Respond(HttpStatusCode.OK);
        });
        using var client = CreateClient(inner, FastRetryOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("devices", cts.Token));

        // A cancelled caller must not be charged a retry attempt against a request it withdrew.
        Assert.True(inner.RequestCount <= 1);
    }

    [Fact]
    public async Task Circuit_breaker_opens_after_repeated_transient_failures_and_then_fails_fast_without_calling_upstream()
    {
        var inner = new ScriptedHandler((_, _, _) => Respond(HttpStatusCode.ServiceUnavailable));
        var options = new NayaxResilienceOptions
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(300),
            MaxRetryAttempts = 1, // irrelevant here: a POST is never idempotent, so it is never retried anyway
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerMinimumThroughput = 4,
            CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(30),
            CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30),
        };
        using var client = CreateClient(inner, options);

        // POST (never retried) keeps this test to exactly one sampled outcome per call, isolating
        // the circuit breaker from the retry strategy it shares a pipeline with.
        for (var i = 0; i < 4; i++)
        {
            var response = await client.PostAsync("machines/1/machineProducts", new StringContent("[]"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        var requestsBeforeBreakerOpened = inner.RequestCount;
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => client.PostAsync("machines/1/machineProducts", new StringContent("[]")));

        Assert.Equal(requestsBeforeBreakerOpened, inner.RequestCount);
    }

    [Fact]
    public async Task Retry_log_names_the_attempt_and_path_but_never_a_token_or_body()
    {
        const string sensitiveBody = "{\"secret\":\"SHOULD-NEVER-SURFACE\"}";
        var inner = new ScriptedHandler((attempt, _, _) => attempt == 1
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(sensitiveBody),
            })
            : Respond(HttpStatusCode.OK));
        var logger = new CapturingLogger<NayaxResilienceHandler>();
        using var client = CreateClient(inner, FastRetryOptions(), logger);

        await client.GetAsync("devices");

        var log = Assert.Single(logger.Messages);
        Assert.Contains("devices", log, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveBody, log, StringComparison.Ordinal);
        Assert.DoesNotContain("SHOULD-NEVER-SURFACE", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", log, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status) =>
        Task.FromResult(new HttpResponseMessage(status));

    // TryAddWithoutValidation so a deliberately malformed value reaches the handler exactly as a
    // real upstream could send it, instead of being rejected while the test response is built.
    private static Task<HttpResponseMessage> RespondWithRetryAfter(HttpStatusCode status, string retryAfter)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return Task.FromResult(response);
    }

    private static HttpClient CreateClient(
        HttpMessageHandler inner,
        NayaxResilienceOptions options,
        ILogger<NayaxResilienceHandler>? logger = null,
        IClock? clock = null)
    {
        var resilienceHandler = new NayaxResilienceHandler(
            logger ?? NullLogger<NayaxResilienceHandler>.Instance,
            clock ?? new FakeClock(DateTime.UtcNow),
            options)
        {
            InnerHandler = inner,
        };
        return new HttpClient(resilienceHandler) { BaseAddress = new Uri("https://nayax.invalid/") };
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public int RequestCount { get; private set; }

        public ScriptedHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = ++RequestCount;
            return await _respond(attempt, request, cancellationToken);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
