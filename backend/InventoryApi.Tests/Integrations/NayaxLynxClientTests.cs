#nullable enable

using System.Net;
using InventoryApi.Integrations.Nayax;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace InventoryApi.Tests.Integrations;

public class NayaxLynxClientTests
{
    // Obviously fake values. No real token, endpoint, or payload belongs here.
    private const string FakeToken = "fake-token-not-a-real-credential";
    private const string SensitiveUpstreamBody =
        "{\"error\":\"denied\",\"secretCustomerRef\":\"SHOULD-NEVER-SURFACE\"}";

    [Fact]
    public async Task GetMachines_when_nayax_returns_403_throws_NayaxUpstreamException()
    {
        var (client, _) = CreateClient(HttpStatusCode.Forbidden, SensitiveUpstreamBody);

        var ex = await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => client.GetMachinesAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal(403, ex.StatusCodeValue);
        Assert.Equal("GetMachinesAsync", ex.Operation);
        Assert.Equal("GET", ex.Method);
        Assert.Equal("machines", ex.Endpoint);
    }

    [Fact]
    public async Task GetMachines_when_nayax_returns_500_throws_NayaxUpstreamException()
    {
        var (client, _) = CreateClient(HttpStatusCode.InternalServerError, SensitiveUpstreamBody);

        var ex = await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => client.GetMachinesAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal(500, ex.StatusCodeValue);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Other_non_success_statuses_follow_the_same_upstream_error_path(HttpStatusCode status)
    {
        var (client, _) = CreateClient(status, SensitiveUpstreamBody);

        var ex = await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => client.GetMachinesAsync(CancellationToken.None));

        Assert.Equal(status, ex.StatusCode);
    }

    // The POST path must use the same centralized check as the GET paths.
    [Fact]
    public async Task CreateMachineProducts_when_nayax_returns_403_throws_NayaxUpstreamException()
    {
        var (client, _) = CreateClient(HttpStatusCode.Forbidden, SensitiveUpstreamBody);

        var ex = await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => client.CreateMachineProductsAsync(42, new List<NayaxMachineProduct>(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("CreateMachineProductsAsync", ex.Operation);
        Assert.Equal("POST", ex.Method);
        Assert.Equal("machines/42/machineProducts", ex.Endpoint);
    }

    [Fact]
    public async Task Upstream_failure_never_returns_an_empty_collection()
    {
        var (client, _) = CreateClient(HttpStatusCode.Forbidden, "[]");

        await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => client.GetProductsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Exception_carries_no_token_and_no_upstream_body()
    {
        var (client, _) = CreateClient(HttpStatusCode.Forbidden, SensitiveUpstreamBody);

        var ex = await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => client.GetMachinesAsync(CancellationToken.None));

        var rendered = ex.ToString();
        Assert.DoesNotContain(FakeToken, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHOULD-NEVER-SURFACE", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ex.Data);
    }

    [Fact]
    public async Task Failure_logs_operation_and_status_but_no_token_or_body()
    {
        var (client, logger) = CreateClient(HttpStatusCode.Forbidden, SensitiveUpstreamBody);

        await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => client.GetMachinesAsync(CancellationToken.None));

        var log = Assert.Single(logger.Messages);
        Assert.Contains("GetMachinesAsync", log, StringComparison.Ordinal);
        Assert.Contains("403", log, StringComparison.Ordinal);
        Assert.Contains("machines", log, StringComparison.Ordinal);

        Assert.DoesNotContain(FakeToken, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHOULD-NEVER-SURFACE", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Successful_response_still_deserializes_into_the_existing_model()
    {
        const string body = """
            [
              { "MachineID": 7, "MachineName": "Foyer", "MachineNumber": "M-007", "CustomerID": 3, "ActorID": 11 },
              { "MachineID": 8, "MachineName": "Level 2", "MachineNumber": "M-008" }
            ]
            """;
        var (client, logger) = CreateClient(HttpStatusCode.OK, body);

        var machines = await client.GetMachinesAsync(CancellationToken.None);

        Assert.Equal(2, machines.Count);
        Assert.Equal(7, machines[0].MachineID);
        Assert.Equal("Foyer", machines[0].MachineName);
        Assert.Equal("M-007", machines[0].MachineNumber);
        Assert.Equal(3, machines[0].CustomerID);
        Assert.Equal(11, machines[0].ActorID);
        Assert.Equal(8, machines[1].MachineID);
        Assert.Null(machines[1].CustomerID);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task Caller_cancellation_stays_cancellation_and_is_not_an_upstream_failure()
    {
        var (client, logger) = CreateClient(HttpStatusCode.InternalServerError, SensitiveUpstreamBody);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetMachinesAsync(cts.Token));

        Assert.Empty(logger.Messages);
    }

    private static (NayaxLynxClient Client, CapturingLogger<NayaxLynxClient> Logger) CreateClient(
        HttpStatusCode status, string body)
    {
        var handler = new StubHttpMessageHandler(status, body);
        // A deliberately unroutable host: these tests must never reach a real Nayax endpoint.
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://nayax.invalid") };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Nayax:Token"] = FakeToken })
            .Build();

        var options = Options.Create(new NayaxLynxOptions
        {
            BaseUrl = "https://nayax.invalid",
            OperatorId = "test-operator",
        });

        var logger = new CapturingLogger<NayaxLynxClient>();
        return (new NayaxLynxClient(http, options, configuration, logger), logger);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHttpMessageHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
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
