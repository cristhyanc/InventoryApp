#nullable enable

using System.Net;
using Inventory.Application.Nayax;
using Inventory.Infrastructure.Nayax;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

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

    [Fact]
    public async Task Missing_access_token_sends_no_authorization_header()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "[]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://nayax.invalid") };
        var options = new NayaxLynxOptions { BaseUrl = "https://nayax.invalid", OperatorId = "test-operator" };

        var client = new NayaxLynxClient(http, options, new CapturingLogger<NayaxLynxClient>());
        await client.GetMachinesAsync(CancellationToken.None);

        Assert.Null(handler.LastRequest?.Headers.Authorization);
    }

    [Fact]
    public async Task GetMachineLastAlerts_when_nayax_returns_403_throws_NayaxUpstreamException()
    {
        var (client, _) = CreateClient(HttpStatusCode.Forbidden, SensitiveUpstreamBody);

        var ex = await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => client.GetMachineLastAlertsAsync(42, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("GetMachineLastAlertsAsync", ex.Operation);
        Assert.Equal("GET", ex.Method);
        Assert.Equal("machines/42/lastAlerts", ex.Endpoint);
    }

    /// <summary>
    /// Proves the documented Get Machine Last Alerts response contract
    /// (https://devzone.nayax.com/reference/lynx/machines/get-machine-last-alerts) maps field by
    /// field, including the documented nullable fields arriving as null.
    /// </summary>
    [Fact]
    public async Task GetMachineLastAlerts_deserializes_the_documented_response_contract()
    {
        const string eventData = "Eunhye Chung 'Adjusted Stock, Product MDB: 13 | 25g Nobby's Beef Jerky Hot | 2";
        const string body = """
            [
              {
                "MachineID": 42,
                "EventDateTimeVMC": "2026-09-01T20:00:00.123",
                "TransactionID": null,
                "EventLogID": 8812345678,
                "SiteID": 2,
                "EntityTypeID": 1,
                "EntityTypeName": "Machine",
                "DeviceID": 7700123,
                "EntityActorID": 1234567,
                "EventDateTimeGMT": "2026-09-01T10:00:00.123Z",
                "EventCode": 501,
                "EventSourceID": 3,
                "EventSourceName": "Nayax Core",
                "EventGroupId": 12,
                "EventGroupName": "Inventory",
                "EventCategoryId": 4,
                "EventCategoryName": "Information",
                "EventDescription": "Stock Adjust for Machine",
                "EventData": "Eunhye Chung 'Adjusted Stock, Product MDB: 13 | 25g Nobby's Beef Jerky Hot | 2",
                "JSONData": null,
                "EventUserID": 998877
              },
              {
                "MachineID": null,
                "EventDateTimeVMC": "2026-09-01T21:00:00",
                "TransactionID": 2108816976,
                "EventLogID": 8812345679,
                "SiteID": 2,
                "EntityTypeID": 1,
                "EntityTypeName": null,
                "DeviceID": null,
                "EntityActorID": null,
                "EventDateTimeGMT": "2026-09-01T11:00:00Z",
                "EventCode": 100,
                "EventSourceID": 1,
                "EventSourceName": null,
                "EventGroupId": null,
                "EventGroupName": null,
                "EventCategoryId": null,
                "EventCategoryName": null,
                "EventDescription": null,
                "EventData": null,
                "JSONData": "{\"a\":1}",
                "EventUserID": null
              }
            ]
            """;
        var (client, logger) = CreateClient(HttpStatusCode.OK, body);

        var alerts = await client.GetMachineLastAlertsAsync(42, CancellationToken.None);

        Assert.Equal(2, alerts.Count);
        var alert = alerts[0];
        Assert.Equal(42, alert.MachineId);
        Assert.Equal(new DateTime(2026, 9, 1, 20, 0, 0, 123), alert.EventDateTimeVmc);
        Assert.Null(alert.TransactionId);
        Assert.Equal(8812345678, alert.EventLogId);
        Assert.Equal(2, alert.SiteId);
        Assert.Equal(1, alert.EntityTypeId);
        Assert.Equal("Machine", alert.EntityTypeName);
        Assert.Equal(7700123, alert.DeviceId);
        Assert.Equal(1234567, alert.EntityActorId);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, 123, DateTimeKind.Utc), alert.EventDateTimeGmt);
        Assert.Equal(DateTimeKind.Utc, alert.EventDateTimeGmt.Kind);
        Assert.Equal(501, alert.EventCode);
        Assert.Equal(3, alert.EventSourceId);
        Assert.Equal("Nayax Core", alert.EventSourceName);
        Assert.Equal(12, alert.EventGroupId);
        Assert.Equal("Inventory", alert.EventGroupName);
        Assert.Equal(4, alert.EventCategoryId);
        Assert.Equal("Information", alert.EventCategoryName);
        Assert.Equal("Stock Adjust for Machine", alert.EventDescription);
        // EventData is the raw text the Event 501 parser reads; it must arrive byte-for-byte.
        Assert.Equal(eventData, alert.EventData);
        Assert.Null(alert.JsonData);
        Assert.Equal(998877, alert.EventUserId);

        var sparse = alerts[1];
        Assert.Null(sparse.MachineId);
        Assert.Equal(2108816976, sparse.TransactionId);
        Assert.Equal(8812345679, sparse.EventLogId);
        Assert.Null(sparse.DeviceId);
        Assert.Null(sparse.EntityActorId);
        Assert.Null(sparse.EventGroupId);
        Assert.Null(sparse.EventCategoryId);
        Assert.Null(sparse.EventDescription);
        Assert.Null(sparse.EventData);
        Assert.Equal("{\"a\":1}", sparse.JsonData);
        Assert.Null(sparse.EventUserId);

        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task GetMachineLastAlerts_caller_cancellation_stays_cancellation()
    {
        var (client, logger) = CreateClient(HttpStatusCode.InternalServerError, SensitiveUpstreamBody);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetMachineLastAlertsAsync(42, cts.Token));

        Assert.Empty(logger.Messages);
    }

    private static (NayaxLynxClient Client, CapturingLogger<NayaxLynxClient> Logger) CreateClient(
        HttpStatusCode status, string body)
    {
        var handler = new StubHttpMessageHandler(status, body);
        // A deliberately unroutable host: these tests must never reach a real Nayax endpoint.
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://nayax.invalid") };

        var options = new NayaxLynxOptions
        {
            BaseUrl = "https://nayax.invalid",
            OperatorId = "test-operator",
            AccessToken = FakeToken,
        };

        var logger = new CapturingLogger<NayaxLynxClient>();
        return (new NayaxLynxClient(http, options, logger), logger);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHttpMessageHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
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
