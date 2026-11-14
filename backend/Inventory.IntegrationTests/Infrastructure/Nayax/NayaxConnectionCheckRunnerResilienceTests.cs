#nullable enable

using System.Net;
using System.Text;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The connection check runner as the composition root really builds it (issue #1310): resolved from
/// <c>AddNayaxLynxClient</c>, so the production <see cref="NayaxResilienceHandler"/> is in its
/// <c>HttpClient</c>'s pipeline, with only the transport substituted.
///
/// <para>That is the whole point of this file, and why it exists beside
/// <see cref="NayaxConnectionCheckRunnerTests"/> rather than in it: those tests construct the runner
/// over a bare <see cref="HttpClient"/>, which answers each scripted response once. A test that
/// counts machine-list calls there is counting <em>method</em> calls, and the retry policy attached
/// in production can turn one of those into four live requests. "Each check run makes one live
/// <c>GET /v1/machines</c> call" is an acceptance criterion about the wire, so it has to be
/// asserted below the handler that decides how many requests a call becomes.</para>
///
/// <para>The machine list is the only request that opts out. The other six probes share the same
/// client and keep the ordinary retry budget, which the second test states - an owner's transient
/// outage must not be reported as a missing permission.</para>
/// </summary>
public class NayaxConnectionCheckRunnerResilienceTests
{
    // Obviously fake values. No real token or operator account belongs in a test.
    private const string FakeToken = "fake-token-not-a-real-credential";
    private const string OperatorId = "2002736764";
    private const string OneMachine = "[{\"MachineID\":4242,\"MachineName\":\"Lobby\"}]";

    private const int TestBusinessId = 7;
    private static readonly DateTime Now = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastTestedAt = new(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// A transient machine-list answer is sent once and is not retried into a second live request,
    /// although the handler's retry budget would happily have turned this 503 into a success on the
    /// next attempt. Retrying would be the wrong favour: the run already has its answer, because a
    /// machine list that did not pass concludes nothing whichever attempt said so, and the extra
    /// requests come out of the owner's own Nayax allowance.
    ///
    /// <para>And what "concludes nothing" costs the stored connection is nothing: an inconclusive
    /// re-test leaves the credentials, the status, the machine-check state and the last-tested
    /// instant exactly as they were, so a provider outage can neither break a working connection nor
    /// bless a broken one.</para>
    /// </summary>
    [Fact]
    public async Task A_retryable_machine_list_answer_is_sent_once_and_leaves_the_stored_connection_untouched()
    {
        // 503 then 200: were the gate's read retried, it would succeed on the second attempt and the
        // assertions below would all describe a *different* outcome - a passed machine list, a Ready
        // status and a fresh last-tested instant.
        var transport = ScriptedTransport.AllOk().Script("machines", HttpStatusCode.ServiceUnavailable);
        using var composition = ProductionComposition.With(transport);
        var connections = await StoredReadyConnectionAsync();
        var storedBefore = connections.Connection;
        var writesBefore = connections.AppliedResults.Count;

        var outcome = await Retest(composition.Runner, connections).Handle();

        // One live request for the machine list, whatever the handler would otherwise have done.
        Assert.Equal(1, transport.Attempts("machines"));

        var result = outcome.Result;
        Assert.NotNull(result);
        Assert.Equal(NayaxConnectionCheckOutcome.Inconclusive, result!.Report.Outcome);
        Assert.False(result.StatusUpdated);

        // The row is compared whole, so "intact" covers the operator id, the status, the revision,
        // the machine-check state, the last-tested instant and the updated instant at once.
        Assert.Equal(storedBefore, connections.Connection);
        Assert.Equal(NayaxConnectionStatus.Ready, connections.Connection!.Status);
        Assert.Equal(LastTestedAt, connections.Connection.LastTestedAtUtc);
        Assert.Equal(writesBefore, connections.AppliedResults.Count);
        Assert.Empty(connections.DiscardedResults);
        // No credential was stored either, so the token behind that unchanged revision is still the
        // one the business had.
        Assert.Empty(connections.SavedTokens);
    }

    /// <summary>
    /// The opt-out is the machine list's alone. A feature probe that meets a transient failure is
    /// still retried exactly as every other Nayax read is, so a momentary 503 on the product
    /// catalogue is not reported to an owner as a permission they need to go and ask Nayax for.
    /// </summary>
    [Fact]
    public async Task Another_probe_s_transient_failure_is_still_retried()
    {
        var products = $"operators/{OperatorId}/products";
        var transport = ScriptedTransport.AllOk().Script(products, HttpStatusCode.ServiceUnavailable);
        using var composition = ProductionComposition.With(transport);

        var report = await composition.Runner.RunAsync(Candidate(), CancellationToken.None);

        Assert.Equal(2, transport.Attempts(products));
        Assert.Equal(1, transport.Attempts("machines"));
        Assert.Equal(
            NayaxConnectionCheckStatus.Passed,
            report.Lines.Single(line => line.Check == NayaxConnectionCheck.ProductCatalogue).Status);
        Assert.Equal(NayaxConnectionCheckOutcome.Ready, report.Outcome);
    }

    /// <summary>
    /// A stored connection to re-test: the credentials, <see cref="NayaxConnectionStatus.Ready"/>
    /// and a last-tested instant from an earlier run, applied through the store so the row carries
    /// the instant a real test left on it.
    /// </summary>
    private static async Task<FakeNayaxConnectionStore> StoredReadyConnectionAsync()
    {
        var connections = new FakeNayaxConnectionStore(
            OperatorId, FakeToken, NayaxConnectionStatus.Ready, credentialRevision: 1);

        Assert.True(await connections.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(1, NayaxConnectionStatus.Ready, LastTestedAt)
            {
                MachineChecksPending = false,
            },
            CancellationToken.None));

        return connections;
    }

    private static RetestNayaxConnection Retest(
        INayaxConnectionCheckRunner runner, FakeNayaxConnectionStore connections)
    {
        var clock = new FakeClock(Now);
        return new RetestNayaxConnection(
            new NayaxConnectionRetestCredentialProvider(connections),
            runner,
            connections,
            new NayaxConnectionTestRateLimiter(clock),
            new FakeCurrentBusinessProvider(TestBusinessId),
            clock);
    }

    private static NayaxConnectionCredential Candidate() =>
        new(OperatorId, FakeToken, credentialRevision: 0);

    /// <summary>
    /// The runner resolved from the production registration, with the transport - and only the
    /// transport - replaced, exactly as <c>PlanEnforcementApiTests</c> substitutes it: the named
    /// <c>HttpClient</c>'s base address and its <see cref="NayaxResilienceHandler"/> are the shipped
    /// ones.
    /// </summary>
    private sealed class ProductionComposition : IDisposable
    {
        private readonly ServiceProvider _provider;

        private ProductionComposition(ServiceProvider provider)
        {
            _provider = provider;
            Runner = provider.GetRequiredService<INayaxConnectionCheckRunner>();
        }

        public INayaxConnectionCheckRunner Runner { get; }

        public static ProductionComposition With(ScriptedTransport transport)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            // The handler reads the clock only to interpret a Retry-After on a 429 (issue #844); no
            // test here answers one, so a fixed instant keeps the composition deterministic.
            services.AddSingleton<IClock>(new FakeClock(Now));

            // A deliberately unroutable host: the scripted transport below is what answers, and a
            // registration that stopped reaching it would fail rather than call Nayax.
            services.AddNayaxLynxClient(new NayaxLynxOptions { BaseUrl = "https://nayax.invalid" });
            services.AddHttpClient(nameof(INayaxConnectionCheckRunner))
                .ConfigurePrimaryHttpMessageHandler(() => transport);

            return new ProductionComposition(services.BuildServiceProvider());
        }

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// The transport under the production pipeline: it counts the live requests per relative path,
    /// and answers a scripted status on a path's <em>first</em> attempt only, so "this was retried"
    /// and "this was not" are told apart by the answer as well as by the count.
    /// </summary>
    private sealed class ScriptedTransport : HttpMessageHandler
    {
        private const string BasePath = "/operational/v1/";

        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HttpStatusCode> _firstAttemptFailures =
            new(StringComparer.Ordinal);

        private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);

        /// <summary>Every documented read endpoint answering successfully, with one machine.</summary>
        public static ScriptedTransport AllOk()
        {
            var transport = new ScriptedTransport();
            transport._bodies["machines"] = OneMachine;
            // The single-machine read returns an object rather than a list.
            transport._bodies["machines/4242/"] = "{\"MachineID\":4242}";
            return transport;
        }

        /// <summary>Fails one path's first attempt with <paramref name="status"/>.</summary>
        public ScriptedTransport Script(string path, HttpStatusCode status)
        {
            _firstAttemptFailures[path] = status;
            return this;
        }

        /// <summary>How many live requests this path received.</summary>
        public int Attempts(string path) => _attempts.GetValueOrDefault(path);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = (request.RequestUri?.AbsolutePath ?? string.Empty)
                .Replace(BasePath, string.Empty, StringComparison.Ordinal);
            var attempt = _attempts.GetValueOrDefault(path) + 1;
            _attempts[path] = attempt;

            if (attempt == 1 && _firstAttemptFailures.TryGetValue(path, out var failure))
            {
                return Task.FromResult(new HttpResponseMessage(failure)
                {
                    Content = new StringContent("upstream failure", Encoding.UTF8, "application/json"),
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _bodies.GetValueOrDefault(path, "[]"), Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}
