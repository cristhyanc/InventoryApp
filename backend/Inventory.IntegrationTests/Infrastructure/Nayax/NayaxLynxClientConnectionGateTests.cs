#nullable enable

using System.Net;
using System.Text;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure.Nayax;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// How the Nayax client behaves at the per-business connection boundary (issue #520): the status
/// gate before a call, and the two Nayax answers that mean something about the connection rather
/// than about the request.
///
/// The two statuses are the Lynx API's own documented ones
/// (https://devzone.nayax.com/docs/manage-data-operations/lynx-api/app-tokens): <c>401</c> means the
/// token is invalid or has expired, which is a verdict on the stored credentials, and <c>403</c>
/// means the token's scopes do not cover the requested resource or action, which is a verdict on one
/// feature. They therefore have deliberately different consequences, and only one of them writes a
/// status.
/// </summary>
public class NayaxLynxClientConnectionGateTests
{
    // Obviously fake values. No real token, endpoint, or payload belongs here.
    private const string FakeToken = "fake-token-not-a-real-credential";
    private const string OperatorId = "test-operator";
    private const string SensitiveUpstreamBody =
        "{\"message\":\"Insufficient permissions to perform this action.\",\"secretCustomerRef\":\"SHOULD-NEVER-SURFACE\"}";

    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task A_refused_connection_fails_closed_without_calling_nayax(NayaxConnectionStatus status)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]");
        var client = CreateClient(handler, FakeNayaxRequestCredentialProvider.Refusing(status), out var logger);

        var exception = await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => client.GetMachinesAsync(CancellationToken.None));

        Assert.Equal(status, exception.Status);
        Assert.Empty(handler.Requests);
        Assert.Empty(logger.Messages);
    }

    [Theory]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    public async Task A_usable_connection_calls_nayax_with_its_own_credential(NayaxConnectionStatus status)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]");
        var credentials = new FakeNayaxRequestCredentialProvider(OperatorId, FakeToken, status);
        var client = CreateClient(handler, credentials, out _);

        await client.GetMachinesAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://nayax.invalid/operational/v1/machines", request.Url);
        Assert.Equal($"Bearer {FakeToken}", request.Authorization);
    }

    /// <summary>
    /// A 401 is the documented "token is invalid or has expired" answer, so it moves the connection
    /// to <see cref="NayaxConnectionStatus.NeedsAttention"/> - and it is reported for the revision
    /// the call <em>started</em> with, which is what lets the store discard it when a newer token
    /// has since been saved.
    /// </summary>
    [Fact]
    public async Task A_401_reports_the_revision_the_call_started_with_and_fails_closed()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, SensitiveUpstreamBody);
        var credentials = new FakeNayaxRequestCredentialProvider(
            OperatorId, FakeToken, NayaxConnectionStatus.Ready, credentialRevision: 7);
        var client = CreateClient(handler, credentials, out _);

        var exception = await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => client.GetMachinesAsync(CancellationToken.None));

        Assert.Equal(NayaxConnectionStatus.NeedsAttention, exception.Status);
        Assert.Equal(new[] { 7 }, credentials.UnauthorizedReports);
    }

    [Fact]
    public async Task A_401_on_the_post_path_reports_the_same_way()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, SensitiveUpstreamBody);
        var credentials = new FakeNayaxRequestCredentialProvider(
            OperatorId, FakeToken, NayaxConnectionStatus.Ready, credentialRevision: 3);
        var client = CreateClient(handler, credentials, out _);

        await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => client.CreateMachineProductsAsync(42, new List<NayaxMachineProduct>(), CancellationToken.None));

        Assert.Equal(new[] { 3 }, credentials.UnauthorizedReports);
    }

    /// <summary>
    /// While permissions are unverified, a 403 is a per-feature answer: the call fails with the
    /// stable "Nayax hasn't granted permission for this" error, nothing writes a status, and every
    /// other Nayax feature stays usable.
    /// </summary>
    [Fact]
    public async Task A_403_while_permissions_are_unverified_is_a_per_feature_permission_error()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, SensitiveUpstreamBody);
        var credentials = new FakeNayaxRequestCredentialProvider(
            OperatorId, FakeToken, NayaxConnectionStatus.PendingPermissions);
        var client = CreateClient(handler, credentials, out _);

        var exception = await Assert.ThrowsAsync<NayaxPermissionNotGrantedException>(
            () => client.GetMachineLastAlertsAsync(42, CancellationToken.None));

        Assert.Equal(nameof(INayaxLynxClient.GetMachineLastAlertsAsync), exception.Operation);
        Assert.Equal(NayaxPermissionNotGrantedException.StableMessage, exception.Message);
        Assert.Empty(credentials.UnauthorizedReports);
    }

    /// <summary>
    /// Only a 401 ever changes a status. A 403 leaves the connection exactly as it was - whichever
    /// usable status it was in - because the token's scopes are not a verdict on the token.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    public async Task A_403_never_writes_a_status(NayaxConnectionStatus status)
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, SensitiveUpstreamBody);
        var credentials = new FakeNayaxRequestCredentialProvider(OperatorId, FakeToken, status);
        var client = CreateClient(handler, credentials, out _);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetMachinesAsync(CancellationToken.None));

        Assert.Empty(credentials.UnauthorizedReports);
    }

    /// <summary>
    /// A 403 against credentials that were tested and worked stays an ordinary upstream failure, the
    /// behaviour that predates this change: the issue's per-feature permission error is defined for
    /// the unverified state, and widening it would change how a tested connection's 403 is reported
    /// without an issue asking for it (#329 owns per-screen degradation).
    /// </summary>
    [Fact]
    public async Task A_403_on_tested_credentials_stays_an_upstream_failure()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, SensitiveUpstreamBody);
        var credentials = new FakeNayaxRequestCredentialProvider(
            OperatorId, FakeToken, NayaxConnectionStatus.Ready);
        var client = CreateClient(handler, credentials, out _);

        var exception = await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => client.GetMachinesAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    /// <summary>
    /// The connection-boundary failures must be as free of the token and the upstream body as the
    /// upstream failure already is, in the exception and in the log line alike.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task The_failure_and_its_log_carry_no_token_or_upstream_body(HttpStatusCode status)
    {
        var handler = new RecordingHandler(status, SensitiveUpstreamBody);
        var credentials = new FakeNayaxRequestCredentialProvider(
            OperatorId, FakeToken, NayaxConnectionStatus.PendingPermissions);
        var client = CreateClient(handler, credentials, out var logger);

        var exception = await Record.ExceptionAsync(() => client.GetMachinesAsync(CancellationToken.None));

        Assert.NotNull(exception);
        var rendered = exception!.ToString() + '\n' + string.Join('\n', logger.Messages);
        Assert.DoesNotContain(FakeToken, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHOULD-NEVER-SURFACE", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", rendered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A 401 or an unverified-permissions 403 is a connection failure, not an upstream one: the
    /// client itself must not log it, because <c>NayaxConnectionExceptionHandler</c> logs it exactly
    /// once, at <c>Warning</c>, once it reaches the HTTP boundary. Logging it here too would leave
    /// every such failure traced twice - once at this client's own <c>Error</c> level - which is
    /// exactly the "expected outcome, not a server error" distinction the handler's contract draws.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_connection_failure_is_not_logged_by_the_client(HttpStatusCode status)
    {
        var handler = new RecordingHandler(status, SensitiveUpstreamBody);
        var credentials = new FakeNayaxRequestCredentialProvider(
            OperatorId, FakeToken, NayaxConnectionStatus.PendingPermissions);
        var client = CreateClient(handler, credentials, out var logger);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetMachinesAsync(CancellationToken.None));

        Assert.Empty(logger.Messages);
    }

    /// <summary>
    /// A 403 against credentials that were already tested and worked is an ordinary upstream
    /// failure (unchanged from before issue #520), so it is still an infrastructure event the client
    /// itself logs exactly once, with the same safe structured fields as any other failed call.
    /// </summary>
    [Fact]
    public async Task An_upstream_failure_is_logged_once_with_the_operation_and_status()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, SensitiveUpstreamBody);
        var credentials = new FakeNayaxRequestCredentialProvider(
            OperatorId, FakeToken, NayaxConnectionStatus.Ready);
        var client = CreateClient(handler, credentials, out var logger);

        await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => client.GetMachinesAsync(CancellationToken.None));

        var log = Assert.Single(logger.Messages);
        Assert.Contains("GetMachinesAsync", log, StringComparison.Ordinal);
        Assert.Contains("403", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Caller cancellation stays cancellation at this boundary too: a cancelled request must not be
    /// reported as a credential failure, and must never write a status.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_is_not_a_credential_failure()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, SensitiveUpstreamBody);
        var credentials = new FakeNayaxRequestCredentialProvider(
            OperatorId, FakeToken, NayaxConnectionStatus.Ready);
        var client = CreateClient(handler, credentials, out var logger);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetMachinesAsync(cts.Token));

        Assert.Empty(credentials.UnauthorizedReports);
        Assert.Empty(logger.Messages);
    }

    private static NayaxLynxClient CreateClient(
        RecordingHandler handler,
        INayaxRequestCredentialProvider credentials,
        out CapturingMessageLogger logger)
    {
        // A deliberately unroutable host: these tests must never reach a real Nayax endpoint.
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://nayax.invalid/operational/v1/"),
        };

        logger = new CapturingMessageLogger();
        return new NayaxLynxClient(http, credentials, logger);
    }

    /// <summary>
    /// Records a safe snapshot of each request rather than the <see cref="HttpRequestMessage"/>
    /// itself, because the client disposes the request it sent.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public RecordingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public List<(string Method, string? Url, string? Authorization)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((
                request.Method.Method,
                request.RequestUri?.ToString(),
                request.Headers.Authorization?.ToString()));

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    private sealed class CapturingMessageLogger : ILogger<NayaxLynxClient>
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
