using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Nayax;

namespace Inventory.Application.Nayax;

/// <summary>
/// What one completed re-test of the stored credentials produced (issue #537).
///
/// <paramref name="Summary"/> is the connection as it stands <em>after</em> the re-test, read back
/// from the row rather than assembled from what this process intended to write - which is what makes
/// <paramref name="StatusUpdated"/> honest when nothing was concluded.
///
/// A result exists only for the credential revision the run tested: one produced for credentials
/// that were replaced or removed in the meantime is refused rather than returned, so this summary
/// can never describe a credential pair the report did not test (issue #607).
/// </summary>
/// <param name="Summary">The connection's status and machine-check state after the re-test.</param>
/// <param name="StatusUpdated">
/// Whether the re-test concluded anything and wrote it. <see langword="false"/> means Nayax never
/// answered the sign-in, so the stored status, its last-tested instant and its machine-check state
/// are exactly as they were and the caller should try again later.
/// </param>
/// <param name="Report">Every check, in order, with its result.</param>
public sealed record NayaxConnectionRetestResult(
    NayaxConnectionStatusSummary Summary,
    bool StatusUpdated,
    NayaxConnectionCheckReport Report);

/// <summary>
/// The outcome of asking for a re-test (issue #537): it ran, or the business has run too many tests
/// too recently.
///
/// The rate limit is reported as a result rather than thrown, exactly as in
/// <see cref="NayaxConnectionTestOutcome"/>: it is an expected answer the HTTP boundary turns into a
/// specific status and <c>Retry-After</c> header, not a failure another caller would catch.
/// </summary>
public sealed record NayaxConnectionRetestOutcome
{
    private NayaxConnectionRetestOutcome(NayaxConnectionRetestResult? result, TimeSpan? retryAfter)
    {
        Result = result;
        RetryAfter = retryAfter;
    }

    /// <summary>The completed re-test, or <see langword="null"/> when it was refused.</summary>
    public NayaxConnectionRetestResult? Result { get; }

    /// <summary>
    /// How long until this business may test again, or <see langword="null"/> when it ran.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    public static NayaxConnectionRetestOutcome Completed(NayaxConnectionRetestResult result) =>
        new(result, null);

    public static NayaxConnectionRetestOutcome RateLimited(TimeSpan retryAfter) => new(null, retryAfter);
}

/// <summary>
/// Runs the read-only connection checks again with the business's <em>stored</em> token and applies
/// what they conclude (issue #537, a slice of #506 under #328).
///
/// <para><strong>It is the one ordinary consumer of the gate-free credential read</strong>
/// (<see cref="INayaxConnectionRetestCredentialProvider"/>, issue #520), and that is deliberate: a
/// connection in <see cref="NayaxConnectionStatus.NeedsAttention"/> is exactly the one an operator
/// needs to re-test, and the status gate that refuses ordinary calls in that state would make
/// recovery impossible. It is listed in <c>NayaxRetestCredentialBoundaryTests</c>' allow-list as the
/// reviewed exception the boundary always anticipated; nothing else may join it.</para>
///
/// <para><strong>Every result is bound to the revision it tested - whatever it concluded.</strong>
/// The credential read reports the revision the token was read at. A concluded status is written by
/// the store's conditional update against that revision; a run that concluded nothing compares that
/// revision with the stored one before its report is accepted (issue #607). An Owner who saves a
/// replacement token while a re-test is in flight therefore keeps the replacement's own status
/// either way: the older run's answer describes credentials that no longer exist, so it is
/// discarded - never returned beside the replacement's summary - and the caller is told to run the
/// test again. A connection that is gone by then is the same answer as a replaced one.</para>
///
/// <para><strong>A run that learned nothing writes nothing.</strong> When Nayax never answered the
/// sign-in - a <c>5xx</c>, a timeout, an open circuit - the stored status, the last-tested instant
/// and the machine-check state all stay exactly as they were, because a connection that is working
/// must not be marked broken by somebody else's outage, and a broken one must not be blessed by it
/// either. The refusal above writes nothing either: there is no stale result to discard, because
/// such a run has nothing to write in the first place.</para>
///
/// <para><strong>It shares the test's rate limit</strong>, per business: a re-test is the same eight
/// calls against the same upstream, so test and re-test runs count together and the allowance is
/// claimed before any call is made.</para>
/// </summary>
public sealed class RetestNayaxConnection
{
    /// <summary>
    /// What the caller is told when the result was produced for credentials that have since been
    /// replaced. The fixed sentence agreed in #506's contract corrections.
    /// </summary>
    public const string ConnectionChangedMessage =
        "The connection changed while testing. Run the test again.";

    private readonly INayaxConnectionRetestCredentialProvider _credentials;
    private readonly INayaxConnectionCheckRunner _runner;
    private readonly INayaxConnectionStore _connections;
    private readonly NayaxConnectionTestRateLimiter _rateLimiter;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IClock _clock;

    public RetestNayaxConnection(
        INayaxConnectionRetestCredentialProvider credentials,
        INayaxConnectionCheckRunner runner,
        INayaxConnectionStore connections,
        NayaxConnectionTestRateLimiter rateLimiter,
        ICurrentBusinessProvider currentBusiness,
        IClock clock)
    {
        _credentials = credentials;
        _runner = runner;
        _connections = connections;
        _rateLimiter = rateLimiter;
        _currentBusiness = currentBusiness;
        _clock = clock;
    }

    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <exception cref="NayaxNotConnectedException">
    /// The business has stored no credentials, so there is nothing to re-test. Unlike an ordinary
    /// call, every stored status may be re-tested - including the two the gate refuses.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The credentials were replaced - or removed - while the re-test ran, so its result was
    /// discarded, whether it concluded a status or nothing at all; see
    /// <see cref="ConnectionChangedMessage"/>.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">The caller has no resolvable business.</exception>
    public async Task<NayaxConnectionRetestOutcome> Handle(CancellationToken cancellationToken = default)
    {
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken);

        // Gate-free on purpose: NeedsAttention is the state this endpoint exists to recover from.
        // A stored ciphertext that cannot be decrypted still fails closed, as it does everywhere.
        var credential = await _credentials.GetForRetestAsync(cancellationToken)
            ?? throw new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured);

        if (!_rateLimiter.TryClaim(businessId, out var retryAfter))
        {
            // Refused before the runner, so a refused request makes no Nayax call at all.
            return NayaxConnectionRetestOutcome.RateLimited(retryAfter);
        }

        var report = await _runner.RunAsync(credential, cancellationToken);
        var concluded = NayaxConnectionCheckOutcomePolicy.ConcludedStatus(report.Outcome);

        if (concluded is not { } status)
        {
            // Nothing was learned, so nothing is written - not even a last-tested instant, which
            // would claim the current credentials had been checked. The revision is still compared,
            // because a report about credentials that have since been replaced is not a report about
            // this connection, whether it concluded anything or not (issue #607).
            //
            // One read answers both questions: the revision this report is compared against and the
            // status reported beside it come from the same row at the same instant, so no save can
            // land between two reads and pair this run's report with the replacement's state.
            // Reporting what is stored - rather than what this run would have concluded - is also
            // what makes "the status is unchanged" a statement about the database.
            var current = await _connections.FindAsync(cancellationToken);

            if (current is null || current.CredentialRevision != credential.CredentialRevision)
            {
                // A row that is gone and a row at another revision are the same answer: neither is
                // the connection this run tested, so its result is discarded exactly as a concluded
                // one is, and nothing about the replacement is touched.
                throw new DomainConflictException(ConnectionChangedMessage);
            }

            return NayaxConnectionRetestOutcome.Completed(new NayaxConnectionRetestResult(
                NayaxConnectionStatusSummary.From(current),
                StatusUpdated: false,
                report));
        }

        var applied = await _connections.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(credential.CredentialRevision, status, _clock.UtcNow)
            {
                MachineChecksPending = report.MachineChecksPending,
            },
            cancellationToken);

        if (!applied)
        {
            // The store's conditional update matched no row: the revision this run tested is no
            // longer the stored one. The replacement keeps its own status, and the caller re-tests.
            throw new DomainConflictException(ConnectionChangedMessage);
        }

        return NayaxConnectionRetestOutcome.Completed(new NayaxConnectionRetestResult(
            new NayaxConnectionStatusSummary(status, report.MachineChecksPending),
            StatusUpdated: true,
            report));
    }
}
