#nullable enable

using System.Net;
using Inventory.Infrastructure.Nayax;
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

    private static HttpClient CreateClient(
        HttpMessageHandler inner, NayaxResilienceOptions options, ILogger<NayaxResilienceHandler>? logger = null)
    {
        var resilienceHandler = new NayaxResilienceHandler(logger ?? NullLogger<NayaxResilienceHandler>.Instance, options)
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
