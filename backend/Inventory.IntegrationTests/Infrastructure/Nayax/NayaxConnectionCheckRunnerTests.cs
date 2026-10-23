#nullable enable

using System.Net;
using System.Reflection;
using System.Text;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure.Nayax;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The read-only Nayax connection check runner of issue #536, over a fake HTTP handler. No test here
/// reaches a real Nayax endpoint: the base address is a deliberately unroutable host and every
/// response is scripted.
///
/// <para>Five things are pinned, and each is a property an operator's decision depends on. Each
/// check is reported on its own line, so one missing permission does not look like a dead token.
/// Nayax's documented statuses map to the documented causes
/// (https://devzone.nayax.com/docs/manage-data-operations/lynx-api/app-tokens). A business with no
/// machines gets "not checked" rather than a pass or a failure. <strong>No request is ever anything
/// but a <c>GET</c></strong>, so testing a connection cannot change an operator's Nayax data. And
/// the submitted token reaches the wire and nothing else - not a log line, not a result, not an
/// exception.</para>
/// </summary>
public class NayaxConnectionCheckRunnerTests
{
    // Obviously fake values. No real token, operator account or payload belongs here.
    private const string FakeToken = "fake-token-not-a-real-credential";
    private const string OperatorId = "2002736764";
    private const string SensitiveUpstreamBody =
        "{\"message\":\"Insufficient permissions to perform this action.\",\"secretCustomerRef\":\"SHOULD-NEVER-SURFACE\"}";

    private const string OneMachine = "[{\"MachineID\":4242,\"MachineName\":\"Lobby\"}]";

    [Fact]
    public async Task Every_check_passes_when_nayax_answers_every_endpoint()
    {
        var handler = ScriptedHandler.AllOk();

        var report = await Run(handler);

        Assert.Equal(NayaxConnectionCheckPolicy.All, report.Lines.Select(line => line.Check).ToArray());
        Assert.All(report.Lines, line => Assert.Equal(NayaxConnectionCheckStatus.Passed, line.Status));
        Assert.All(report.Lines, line => Assert.Null(line.Failure));
        Assert.True(report.NothingFailed);
        Assert.Equal(1, report.MachineCount);
    }

    /// <summary>
    /// The eight documented endpoints, each called exactly once and each with the submitted
    /// credentials. The paths are the ones <c>NayaxLynxClient</c> builds, which is the point of
    /// running the checks through it.
    /// </summary>
    [Fact]
    public async Task The_checks_call_the_eight_documented_read_endpoints_once_each()
    {
        var handler = ScriptedHandler.AllOk();

        await Run(handler);

        Assert.Equal(
            new[]
            {
                "devices",
                "machines",
                $"operators/{OperatorId}/products",
                $"operators/{OperatorId}/productGroups",
                "machines/4242/",
                "machines/4242/machineProducts",
                "machines/4242/lastSales",
                "machines/4242/lastAlerts",
            },
            handler.Paths);
        Assert.All(handler.Requests, request => Assert.Equal("GET", request.Method));
        Assert.All(handler.Requests, request => Assert.Equal($"Bearer {FakeToken}", request.Authorization));
    }

    /// <summary>
    /// The write endpoint is never called, whatever Nayax answers. It is the one check that would
    /// change an operator's data, so it is absent from the vocabulary and absent from the wire.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task No_check_ever_makes_a_write_call(HttpStatusCode status)
    {
        var handler = status == HttpStatusCode.OK
            ? ScriptedHandler.AllOk()
            : ScriptedHandler.Answering(status, SensitiveUpstreamBody);

        await Run(handler);

        Assert.DoesNotContain("POST", handler.Requests.Select(request => request.Method));
        Assert.All(handler.Requests, request => Assert.Equal("GET", request.Method));
    }

    /// <summary>
    /// The connection test reads the machine list and one machine's products - the test above pins
    /// that - and stays completely outside the caches of issues #840 and #842 while doing it, in both
    /// directions: it can read nothing from either cache and write nothing into them.
    ///
    /// <para>That is not a nicety. The credentials being tested are a pair an Owner pasted, which may
    /// belong to a different Nayax operator account entirely, so neither its fleet nor its machines'
    /// selections are the business's: a test that filled a cache would make every page in the
    /// application show the machines and stock levels of whatever account was being considered, and a
    /// test that read from one would report data the submitted token never fetched. The isolation is
    /// structural - this runner builds its own
    /// <see cref="NayaxLynxClient"/> over the candidate credentials and is constructed from an
    /// <see cref="HttpClient"/> and a logger and nothing else, so it never resolves the cached port -
    /// and this test is what keeps it that way.</para>
    /// </summary>
    [Fact]
    public void The_connection_test_cannot_reach_either_cache()
    {
        var constructor = Assert.Single(typeof(NayaxConnectionCheckRunner).GetConstructors());

        Assert.Equal(
            new[] { typeof(HttpClient), typeof(ILogger<NayaxLynxClient>) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));

        var fieldTypes = typeof(NayaxConnectionCheckRunner)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToList();

        Assert.DoesNotContain(typeof(NayaxMachineListCache), fieldTypes);
        Assert.DoesNotContain(typeof(NayaxMachineProductsCache), fieldTypes);
        Assert.DoesNotContain(typeof(INayaxLynxClient), fieldTypes);
        Assert.DoesNotContain(typeof(CachingNayaxLynxClient), fieldTypes);
    }

    [Fact]
    public async Task A_401_everywhere_reports_an_invalid_or_expired_token_on_every_check_that_ran()
    {
        var handler = ScriptedHandler.Answering(HttpStatusCode.Unauthorized, SensitiveUpstreamBody);

        var report = await Run(handler);

        // The machine list failed, so there is no machine to run the machine checks against.
        Assert.Null(report.MachineCount);
        Assert.False(report.NothingFailed);
        AssertLine(report, NayaxConnectionCheck.SignIn, NayaxConnectionCheckFailure.InvalidOrExpiredToken);
        AssertLine(report, NayaxConnectionCheck.Machines, NayaxConnectionCheckFailure.InvalidOrExpiredToken);
        AssertLine(report, NayaxConnectionCheck.ProductCatalogue, NayaxConnectionCheckFailure.InvalidOrExpiredToken);
        AssertLine(report, NayaxConnectionCheck.ProductGroups, NayaxConnectionCheckFailure.InvalidOrExpiredToken);
        AssertNotChecked(report, NayaxConnectionCheck.Machine);
        AssertNotChecked(report, NayaxConnectionCheck.MachineProducts);
        AssertNotChecked(report, NayaxConnectionCheck.MachineLastSales);
        AssertNotChecked(report, NayaxConnectionCheck.MachineLastAlerts);
    }

    /// <summary>
    /// The case the issue exists for: a token that signs in perfectly and lacks one permission. The
    /// refused line says so, and every other line still reports its own answer.
    /// </summary>
    [Fact]
    public async Task A_403_on_one_endpoint_fails_only_that_line()
    {
        var handler = ScriptedHandler.AllOk().Override("machines/4242/lastAlerts", HttpStatusCode.Forbidden);

        var report = await Run(handler);

        AssertLine(report, NayaxConnectionCheck.MachineLastAlerts, NayaxConnectionCheckFailure.PermissionNotGranted);
        Assert.All(
            report.Lines.Where(line => line.Check != NayaxConnectionCheck.MachineLastAlerts),
            line => Assert.Equal(NayaxConnectionCheckStatus.Passed, line.Status));
        Assert.False(report.NothingFailed);
    }

    /// <summary>
    /// A <c>404</c> on the two endpoints that carry the operator id in their path means the
    /// submitted operator id is not one the submitted token covers - which is exactly what those
    /// two checks are there to prove.
    /// </summary>
    [Fact]
    public async Task A_404_on_the_operator_endpoints_reports_an_operator_id_mismatch()
    {
        var handler = ScriptedHandler.AllOk()
            .Override($"operators/{OperatorId}/products", HttpStatusCode.NotFound)
            .Override($"operators/{OperatorId}/productGroups", HttpStatusCode.NotFound);

        var report = await Run(handler);

        AssertLine(report, NayaxConnectionCheck.ProductCatalogue, NayaxConnectionCheckFailure.OperatorIdMismatch);
        AssertLine(report, NayaxConnectionCheck.ProductGroups, NayaxConnectionCheckFailure.OperatorIdMismatch);
        AssertPassed(report, NayaxConnectionCheck.SignIn);
        AssertPassed(report, NayaxConnectionCheck.Machines);
    }

    /// <summary>
    /// A <c>404</c> on an endpoint that carries no operator id is not a mismatch. Reporting one
    /// would send an operator to re-check a correct operator id.
    /// </summary>
    [Fact]
    public async Task A_404_on_a_machine_endpoint_is_not_reported_as_an_operator_id_mismatch()
    {
        var handler = ScriptedHandler.AllOk().Override("machines/4242/lastSales", HttpStatusCode.NotFound);

        var report = await Run(handler);

        AssertLine(report, NayaxConnectionCheck.MachineLastSales, NayaxConnectionCheckFailure.UnexpectedResponse);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_5xx_reports_nayax_as_unavailable(HttpStatusCode status)
    {
        var handler = ScriptedHandler.Answering(status, "upstream failure");

        var report = await Run(handler);

        AssertLine(report, NayaxConnectionCheck.SignIn, NayaxConnectionCheckFailure.NayaxUnavailable);
        AssertLine(report, NayaxConnectionCheck.Machines, NayaxConnectionCheckFailure.NayaxUnavailable);
    }

    /// <summary>
    /// A connection that cannot be opened at all is Nayax being unavailable, not a verdict on the
    /// credentials: nothing about them was learned.
    /// </summary>
    [Fact]
    public async Task A_transport_failure_reports_nayax_as_unavailable()
    {
        var handler = ScriptedHandler.Failing();

        var report = await Run(handler);

        Assert.All(
            report.Lines.Where(line => !NayaxConnectionCheckPolicy.RequiresMachine(line.Check)),
            line => Assert.Equal(NayaxConnectionCheckFailure.NayaxUnavailable, line.Failure));
    }

    /// <summary>
    /// A body that is not the documented shape is an answer nobody has interpreted, and is reported
    /// as one rather than as a pass.
    /// </summary>
    [Fact]
    public async Task A_response_that_cannot_be_read_is_an_unexpected_response()
    {
        var handler = ScriptedHandler.AllOk().Override("devices", HttpStatusCode.OK, "not json at all");

        var report = await Run(handler);

        AssertLine(report, NayaxConnectionCheck.SignIn, NayaxConnectionCheckFailure.UnexpectedResponse);
        AssertPassed(report, NayaxConnectionCheck.Machines);
    }

    /// <summary>
    /// A business with no machines yet: the four machine checks are reported as not checked, nothing
    /// is reported as failing, and the machine endpoints are not called at all. An operator
    /// connecting Nayax before their first machine exists must be able to finish.
    /// </summary>
    [Fact]
    public async Task With_no_machines_the_machine_checks_are_not_checked()
    {
        var handler = ScriptedHandler.AllOk().Override("machines", HttpStatusCode.OK, "[]");

        var report = await Run(handler);

        Assert.Equal(0, report.MachineCount);
        Assert.True(report.NothingFailed);
        AssertPassed(report, NayaxConnectionCheck.SignIn);
        AssertPassed(report, NayaxConnectionCheck.Machines);
        AssertPassed(report, NayaxConnectionCheck.ProductCatalogue);
        AssertPassed(report, NayaxConnectionCheck.ProductGroups);
        AssertNotChecked(report, NayaxConnectionCheck.Machine);
        AssertNotChecked(report, NayaxConnectionCheck.MachineProducts);
        AssertNotChecked(report, NayaxConnectionCheck.MachineLastSales);
        AssertNotChecked(report, NayaxConnectionCheck.MachineLastAlerts);
        Assert.DoesNotContain(handler.Paths, path => path.StartsWith("machines/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The machine checks run against the <em>first</em> machine and that one only. Walking the
    /// estate would turn a connection test into a load test of somebody else's API.
    /// </summary>
    [Fact]
    public async Task The_machine_checks_run_against_the_first_machine_only()
    {
        var handler = ScriptedHandler.AllOk().Override(
            "machines",
            HttpStatusCode.OK,
            "[{\"MachineID\":11},{\"MachineID\":22},{\"MachineID\":33}]");

        var report = await Run(handler);

        Assert.Equal(3, report.MachineCount);
        Assert.Contains("machines/11/", handler.Paths);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/22/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/33/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The submitted token goes on the wire and nowhere else. Neither the report nor anything the
    /// runner logged may contain it, or the upstream body, or the authorization header - the same
    /// rule the ordinary client already keeps.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task The_token_and_the_upstream_body_never_reach_the_report_or_a_log(HttpStatusCode status)
    {
        var handler = status == HttpStatusCode.OK
            ? ScriptedHandler.AllOk()
            : ScriptedHandler.Answering(status, SensitiveUpstreamBody);

        var (report, logger) = await RunWithLog(handler);

        var rendered = report.ToString() + '\n' + string.Join('\n', logger.Messages);
        Assert.DoesNotContain(FakeToken, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHOULD-NEVER-SURFACE", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", rendered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Caller cancellation stays cancellation. A request the Owner abandoned must not be reported as
    /// a verdict on their credentials.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_is_not_a_check_failure()
    {
        var runner = CreateRunner(ScriptedHandler.AllOk(), out _);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(Candidate(), cts.Token));
    }

    private static async Task<(NayaxConnectionCheckReport Report, CapturingRunnerLogger Logger)> RunWithLog(
        ScriptedHandler handler)
    {
        var runner = CreateRunner(handler, out var logger);
        return (await runner.RunAsync(Candidate(), CancellationToken.None), logger);
    }

    private static async Task<NayaxConnectionCheckReport> Run(ScriptedHandler handler) =>
        (await RunWithLog(handler)).Report;

    private static NayaxConnectionCheckRunner CreateRunner(
        ScriptedHandler handler,
        out CapturingRunnerLogger logger)
    {
        // A deliberately unroutable host: these tests must never reach a real Nayax endpoint. The
        // resilience handler is deliberately absent, so a scripted 5xx is answered once rather than
        // retried - what is under test is the classification, not the retry policy, which has its
        // own tests.
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://nayax.invalid/operational/v1/"),
        };

        logger = new CapturingRunnerLogger();
        return new NayaxConnectionCheckRunner(http, logger);
    }

    private static NayaxConnectionCredential Candidate() =>
        new(OperatorId, FakeToken, credentialRevision: 0);

    private static void AssertLine(
        NayaxConnectionCheckReport report,
        NayaxConnectionCheck check,
        NayaxConnectionCheckFailure expected)
    {
        var line = report.Lines.Single(line => line.Check == check);
        Assert.Equal(NayaxConnectionCheckStatus.Failed, line.Status);
        Assert.Equal(expected, line.Failure);
    }

    private static void AssertPassed(NayaxConnectionCheckReport report, NayaxConnectionCheck check)
    {
        var line = report.Lines.Single(line => line.Check == check);
        Assert.Equal(NayaxConnectionCheckStatus.Passed, line.Status);
        Assert.Null(line.Failure);
    }

    private static void AssertNotChecked(NayaxConnectionCheckReport report, NayaxConnectionCheck check)
    {
        var line = report.Lines.Single(line => line.Check == check);
        Assert.Equal(NayaxConnectionCheckStatus.NotChecked, line.Status);
        Assert.Null(line.Failure);
    }

    /// <summary>
    /// Answers each relative path with a scripted status and body, and records a safe snapshot of
    /// every request - the client disposes the <see cref="HttpRequestMessage"/> it sent.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private const string BasePath = "/operational/v1/";

        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _byPath = new(StringComparer.Ordinal);
        private (HttpStatusCode Status, string Body)? _default;
        private bool _failTransport;

        public List<(string Method, string Path, string? Authorization)> Requests { get; } = [];

        public IReadOnlyList<string> Paths => [.. Requests.Select(request => request.Path)];

        /// <summary>Every documented read endpoint answering successfully, with one machine.</summary>
        public static ScriptedHandler AllOk()
        {
            var handler = new ScriptedHandler { _default = (HttpStatusCode.OK, "[]") };
            handler._byPath["machines"] = (HttpStatusCode.OK, OneMachine);
            // The single-machine read returns an object rather than a list.
            handler._byPath["machines/4242/"] = (HttpStatusCode.OK, "{\"MachineID\":4242}");
            return handler;
        }

        /// <summary>One answer for every endpoint.</summary>
        public static ScriptedHandler Answering(HttpStatusCode status, string body) =>
            new() { _default = (status, body) };

        /// <summary>A connection that cannot be opened at all.</summary>
        public static ScriptedHandler Failing() => new() { _failTransport = true };

        public ScriptedHandler Override(string path, HttpStatusCode status, string body = "[]")
        {
            _byPath[path] = (status, body);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = (request.RequestUri?.AbsolutePath ?? string.Empty)
                .Replace(BasePath, string.Empty, StringComparison.Ordinal);
            Requests.Add((request.Method.Method, path, request.Headers.Authorization?.ToString()));

            if (_failTransport)
            {
                throw new HttpRequestException("The scripted Nayax host is unreachable.");
            }

            var (status, body) = _byPath.TryGetValue(path, out var scripted)
                ? scripted
                : _default ?? (HttpStatusCode.NotFound, "[]");

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    private sealed class CapturingRunnerLogger : ILogger<NayaxLynxClient>
    {
        public List<string> Messages { get; } = [];

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
