using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The read-only Nayax connection check runner behind
/// <see cref="INayaxConnectionCheckRunner"/> (issue #536, a slice of #506).
///
/// <para><strong>It makes its calls through <see cref="NayaxLynxClient"/>, deliberately.</strong>
/// That client is the one place a Nayax request is built, authenticated, sent and then accepted or
/// rejected, and this runner needs exactly that - the same relative endpoints, the same
/// <c>Authorization: Bearer</c> header, the same operator-id path parameter built from the same
/// credential, the same rule that an upstream body is never read or logged. A second HTTP caller
/// that re-stated all of it would be a second place for those to drift. What differs is only where
/// the credential comes from, so only the credential provider differs: it is constructed over
/// <see cref="CandidateCredentialProvider"/>, which answers with the pair being tested.</para>
///
/// <para><strong>Nothing is written anywhere.</strong> The candidate provider's 401 report is a
/// no-op, so a test against an expired token cannot move a stored connection to
/// <c>NeedsAttention</c> - the credentials being tested are not the stored ones, and a business
/// whose Nayax is working must not be broken by an Owner pasting the wrong token into a form. Only
/// <c>GET</c> endpoints are called; <c>CreateMachineProductsAsync</c> is never reached from here.
/// </para>
///
/// <para><strong>A refusal is a result, not an exception.</strong> Each check is run on its own and
/// its outcome is classified by <see cref="NayaxConnectionCheckPolicy"/>, so one missing permission
/// produces one cross and leaves every other line meaningful - which is the whole reason the test
/// exists. Caller cancellation is the one thing that still propagates.</para>
/// </summary>
public sealed class NayaxConnectionCheckRunner : INayaxConnectionCheckRunner
{
    private readonly HttpClient _http;
    private readonly ILogger<NayaxLynxClient> _clientLogger;

    public NayaxConnectionCheckRunner(HttpClient http, ILogger<NayaxLynxClient> clientLogger)
    {
        _http = http;
        _clientLogger = clientLogger;
    }

    /// <inheritdoc />
    public async Task<NayaxConnectionCheckReport> RunAsync(
        NayaxConnectionCredential candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var client = new NayaxLynxClient(_http, new CandidateCredentialProvider(candidate), _clientLogger);
        var lines = new Dictionary<NayaxConnectionCheck, NayaxConnectionCheckLine>();

        lines[NayaxConnectionCheck.SignIn] = (await RunCheckAsync(
            NayaxConnectionCheck.SignIn,
            () => client.GetDevicesAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false)).Line;

        // The machine list decides the machine-scoped checks, so it is read before them and its
        // first machine is what they are run against. One machine is enough: a token either carries
        // the machine permissions or it does not, and walking the estate would turn a connection
        // test into a load test of somebody else's API.
        var machines = await RunCheckAsync(
            NayaxConnectionCheck.Machines,
            () => client.GetMachinesAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);

        lines[NayaxConnectionCheck.Machines] = machines.Line;
        int? machineCount = machines.Value?.Count;
        var machineId = machines.Value?.FirstOrDefault()?.MachineID;

        lines[NayaxConnectionCheck.ProductCatalogue] = (await RunCheckAsync(
            NayaxConnectionCheck.ProductCatalogue,
            () => client.GetProductsAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false)).Line;

        lines[NayaxConnectionCheck.ProductGroups] = (await RunCheckAsync(
            NayaxConnectionCheck.ProductGroups,
            () => client.GetProductGroupssAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false)).Line;

        if (machineId is { } id)
        {
            lines[NayaxConnectionCheck.Machine] = (await RunCheckAsync(
                NayaxConnectionCheck.Machine,
                () => client.GetMachineAsync(id, cancellationToken),
                cancellationToken).ConfigureAwait(false)).Line;

            lines[NayaxConnectionCheck.MachineProducts] = (await RunCheckAsync(
                NayaxConnectionCheck.MachineProducts,
                () => client.GetMachineProductsAsync(id, cancellationToken),
                cancellationToken).ConfigureAwait(false)).Line;

            lines[NayaxConnectionCheck.MachineLastSales] = (await RunCheckAsync(
                NayaxConnectionCheck.MachineLastSales,
                () => client.GetMachineLastSalesAsync(id, cancellationToken),
                cancellationToken).ConfigureAwait(false)).Line;

            lines[NayaxConnectionCheck.MachineLastAlerts] = (await RunCheckAsync(
                NayaxConnectionCheck.MachineLastAlerts,
                () => client.GetMachineLastAlertsAsync(id, cancellationToken),
                cancellationToken).ConfigureAwait(false)).Line;
        }

        // Every declared check is reported, in the policy's order. A machine-scoped check with no
        // machine to run against - because the business has none, or because the machine list could
        // not be read - is reported as not checked rather than omitted or assumed to pass.
        return new NayaxConnectionCheckReport(
            [.. NayaxConnectionCheckPolicy.All.Select(check =>
                lines.TryGetValue(check, out var line) ? line : NayaxConnectionCheckLine.NotChecked(check))],
            machineCount);
    }

    /// <summary>
    /// Runs one check and keeps what it returned, for the machine list alone: every other check
    /// cares only whether Nayax answered.
    /// </summary>
    private static async Task<(NayaxConnectionCheckLine Line, T? Value)> RunCheckAsync<T>(
        NayaxConnectionCheck check,
        Func<Task<T>> call,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var value = await call().ConfigureAwait(false);
            return (NayaxConnectionCheckLine.Passed(check), value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up. That is not a verdict on the credentials and must never be
            // reported as one.
            throw;
        }
        catch (Exception exception) when (IsCheckFailure(exception))
        {
            return (NayaxConnectionCheckLine.Failed(check, Classify(check, exception)), null);
        }
    }

    /// <summary>
    /// The failures a check may legitimately report. It is an allow-list: a programming error, a
    /// configuration failure or anything else this runner has not reasoned about propagates and
    /// becomes the API's logged 500, rather than being shown to an operator as a Nayax verdict.
    /// </summary>
    private static bool IsCheckFailure(Exception exception) =>
        exception is NayaxNotConnectedException
            or NayaxPermissionNotGrantedException
            or NayaxUpstreamException
            or HttpRequestException
            or JsonException
            or TimeoutRejectedException
            or BrokenCircuitException
            or OperationCanceledException;

    /// <summary>
    /// Turns one failure into the operator-facing cause, through the Domain policy.
    ///
    /// The two connection exceptions are how <see cref="NayaxLynxClient"/> reports Nayax's own
    /// <c>401</c> and <c>403</c>, so they are translated back to those status codes rather than
    /// classified separately - the policy stays the one table that says what a Nayax answer means.
    /// Everything else - a transport failure, the resilience handler's per-attempt timeout or open
    /// circuit, and an <see cref="OperationCanceledException"/> that is not the caller's, because
    /// the caller's own was rethrown above - is Nayax failing to answer, which the policy classifies
    /// from a missing status code.
    /// </summary>
    private static NayaxConnectionCheckFailure Classify(NayaxConnectionCheck check, Exception exception) =>
        exception switch
        {
            NayaxNotConnectedException => NayaxConnectionCheckPolicy.Classify(check, 401),
            NayaxPermissionNotGrantedException => NayaxConnectionCheckPolicy.Classify(check, 403),
            NayaxUpstreamException upstream => NayaxConnectionCheckPolicy.Classify(check, upstream.StatusCodeValue),
            JsonException => NayaxConnectionCheckFailure.UnexpectedResponse,
            _ => NayaxConnectionCheckPolicy.Classify(check, null),
        };

    /// <summary>
    /// The credential provider the test's own client resolves: the pasted pair, every call, with no
    /// database read, no status gate and no status write.
    ///
    /// <para>The status it reports the credential at is
    /// <see cref="NayaxConnectionStatus.PendingPermissions"/>, and that is the whole reason it is
    /// stated rather than left to a default. It is the truth about a pasted token - stored nowhere,
    /// tested by nothing - and it is what makes <see cref="NayaxLynxClient"/> treat a Nayax
    /// <c>403</c> as the per-feature "permission not granted" answer the test needs to report per
    /// line, instead of as an ordinary upstream failure.</para>
    ///
    /// <para><see cref="ReportUnauthorizedAsync"/> does nothing, equally deliberately. A 401 here is
    /// a verdict on credentials that have never been stored, so there is no revision it belongs to
    /// and no row it may touch: writing one would let a test of a bad token mark a <em>working</em>
    /// stored connection as needing attention.</para>
    /// </summary>
    private sealed class CandidateCredentialProvider : INayaxRequestCredentialProvider
    {
        private readonly NayaxRequestCredential _credential;

        public CandidateCredentialProvider(NayaxConnectionCredential candidate) =>
            _credential = new NayaxRequestCredential(candidate, NayaxConnectionStatus.PendingPermissions);

        public Task<NayaxRequestCredential> GetForOperationAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_credential);

        public Task ReportUnauthorizedAsync(int credentialRevision, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
