using Inventory.Application.Nayax;
using Inventory.Application.Providers;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Providers;

namespace Inventory.Application.SalesSync;

/// <summary>
/// The outcome of asking for a latest-sales synchronization: it ran, this business's sales were
/// synchronized moments ago so there was nothing to read (issue #843), or it has run too many too
/// recently (issue #681).
///
/// Neither bound is thrown as an exception, exactly as <c>NayaxConnectionTestOutcome</c> reports the
/// connection test's rate limit: both are expected answers the HTTP boundary turns into a specific
/// status, not failures any other caller would want to catch. A <em>refusal</em> - no usable Nayax
/// connection, or a support session - is still an exception, because it is not an answer about this
/// synchronization at all.
/// </summary>
public sealed record NayaxSalesSyncOutcome
{
    private NayaxSalesSyncOutcome(TimeSpan? retryAfter, DateTime? recentlySyncedAtUtc)
    {
        RetryAfter = retryAfter;
        RecentlySyncedAtUtc = recentlySyncedAtUtc;
    }

    /// <summary>
    /// How long until this business may synchronize again, or <see langword="null"/> when the
    /// synchronization ran or was skipped as recent.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// When this business's last fully successful synchronization finished, when that was recent
    /// enough that this one was skipped (issue #843); otherwise <see langword="null"/>. A skip reads
    /// nothing from Nayax, writes nothing, and claims no rate-limit allowance.
    /// </summary>
    public DateTime? RecentlySyncedAtUtc { get; }

    // Cast because a record's own copy constructor makes a bare `new(null, null)` ambiguous.
    public static NayaxSalesSyncOutcome Completed { get; } = new((TimeSpan?)null, null);

    public static NayaxSalesSyncOutcome RateLimited(TimeSpan retryAfter) => new(retryAfter, null);

    /// <summary>The sales were synchronized at <paramref name="lastSyncedAtUtc"/>, so this run was skipped.</summary>
    public static NayaxSalesSyncOutcome RecentlySynced(DateTime lastSyncedAtUtc) =>
        new(null, lastSyncedAtUtc);
}

/// <summary>
/// The explicit latest-Nayax-sales synchronization use case (issue #187), extracted from the former
/// hidden <c>MachineService.SaveMachinesLastSalesAsync</c> side effect. It discovers the machines
/// through the <see cref="INayaxLynxClient"/> port, reads each machine's last sales through that same
/// port, and hands the whole batch to <see cref="ILatestNayaxSalesStore"/> to deduplicate, match,
/// classify, cost and persist, rebuilding inventory costs only for the products a completed sale
/// actually affected.
///
/// The home dashboard invokes it once before loading Sites and Machines, so both sections calculate
/// from the same freshness boundary; it never calculates machine or site figures itself.
///
/// <para><strong>It is rate-limited per business</strong> (issue #681). One run is one Nayax call
/// per machine plus one, and it writes - so it is both the most expensive call in the application
/// and the one a browser repeats most readily. <see cref="NayaxSalesSyncRateLimiter"/> holds the
/// rule; the allowance is claimed before the first Nayax call, so a refused run leaves the upstream
/// and the database untouched. The business is resolved from the authenticated actor's membership,
/// never from the request, because the allowance belongs to the business whose Nayax account and
/// rows the run would spend.</para>
///
/// <para><strong>A fully successful run records what it covered</strong> (issue #730, parent #329
/// decision P9), through <see cref="IProviderDataCoverageStore"/>. That record is the only thing
/// that lets a report tell "no sales" apart from "no sales data", so it is written last and only
/// once everything above succeeded: a failed, cancelled or partly read run leaves no coverage, and
/// the gap it leaves is reported as unverified rather than as a measured zero.</para>
///
/// <para><strong>A run that happened moments ago is skipped, not repeated</strong> (issue #843, part
/// of parent #839). The Dashboard calls this on every load, so a visit, a back-navigation or a
/// refresh seconds after the last one used to repeat the whole run for sales nobody could have made
/// yet. <see cref="NayaxSalesSyncFreshness"/> remembers when the business's last fully successful
/// run finished, per credential revision; inside its window an unforced call answers
/// <see cref="NayaxSalesSyncOutcome.RecentlySynced"/> having read nothing, written nothing and
/// claimed no rate-limit allowance. The order matters and is asserted: the connection, the status
/// gate and the support-session refusal are resolved <em>first</em>, so a business whose connection
/// is unusable gets exactly the refusal it got before and is never told its sales are current. The
/// Refresh control on the Dashboard forces a run past the skip, and a forced run is still rationed
/// by the limiter.</para>
/// </summary>
public sealed class SyncLatestNayaxSales
{
    private readonly INayaxLynxClient _nayax;
    private readonly ILatestNayaxSalesStore _store;
    private readonly NayaxSalesSyncRateLimiter _rateLimiter;
    private readonly NayaxSalesSyncFreshness _freshness;
    private readonly INayaxRequestCredentialProvider _credentials;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IProviderDataCoverageStore _coverage;
    private readonly IClock _clock;

    public SyncLatestNayaxSales(
        INayaxLynxClient nayax,
        ILatestNayaxSalesStore store,
        NayaxSalesSyncRateLimiter rateLimiter,
        NayaxSalesSyncFreshness freshness,
        INayaxRequestCredentialProvider credentials,
        ICurrentBusinessProvider currentBusiness,
        IProviderDataCoverageStore coverage,
        IClock clock)
    {
        _nayax = nayax;
        _store = store;
        _rateLimiter = rateLimiter;
        _freshness = freshness;
        _credentials = credentials;
        _currentBusiness = currentBusiness;
        _coverage = coverage;
        _clock = clock;
    }

    /// <summary>
    /// Synchronizes this business's latest Nayax sales, skipping the run when it finished moments
    /// ago. This is the Dashboard's ordinary on-load call.
    /// </summary>
    public Task<NayaxSalesSyncOutcome> Handle(CancellationToken cancellationToken = default) =>
        Handle(force: false, cancellationToken: cancellationToken);

    /// <summary>
    /// Synchronizes this business's latest Nayax sales.
    /// </summary>
    /// <param name="force">
    /// <see langword="true"/> for the Dashboard's Refresh control: the recent-sync skip is bypassed
    /// and the run happens, still subject to <see cref="NayaxSalesSyncRateLimiter"/>. A forced call
    /// is not a way past the connection refusals or the run limit, only past the freshness window.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads, the persistence and the cost rebuild.</param>
    public async Task<NayaxSalesSyncOutcome> Handle(bool force, CancellationToken cancellationToken = default)
    {
        // Resolved before anything else, and never from the request: the allowance, the freshness
        // and the credentials all belong to the business whose Nayax account and rows a run spends.
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken);

        // Before the skip decision, on purpose. This is the one place the connection, the status
        // gate and the support-session refusal are applied for this use case, and a business whose
        // connection is missing, disconnected or NeedsAttention - or a request inside a support
        // session, which resolves no Nayax credentials at all (issue #551) - must get that refusal
        // rather than a reassuring "synchronized moments ago" about sales nobody can read. The Nayax
        // client resolves its own credentials per call as it always has; this read is what makes the
        // refusal, and the credential revision the skip is keyed by, precede the decision.
        var credential = await _credentials.GetForOperationAsync(cancellationToken);

        if (!force
            && _freshness.FindRecentSync(businessId, credential.CredentialRevision) is { } lastSyncedAtUtc)
        {
            // Before the allowance is claimed, because a skip reads nothing: rationing a run that
            // never leaves the API would let a browser in a reload loop spend the business's own
            // Refresh button on nothing.
            return NayaxSalesSyncOutcome.RecentlySynced(lastSyncedAtUtc);
        }

        if (!_rateLimiter.TryClaim(businessId, out var retryAfter))
        {
            // Refused before the first Nayax call, so a refused request reads nothing from the
            // upstream and writes nothing here.
            return NayaxSalesSyncOutcome.RateLimited(retryAfter);
        }

        // Taken before the first read, so the range this run may claim never reaches past an instant
        // a sale could still have been authorized at while the run was reading.
        var readStartedAtUtc = _clock.UtcNow;

        var machines = await _nayax.GetMachinesAsync(cancellationToken);

        // Every machine's latest sales are read before anything is persisted, so the batch is stored
        // in one save: a machine read that fails part-way through does not leave half a refresh
        // imported, exactly as the single save at the end of the former private method did. It also
        // means a machine that fails leaves no coverage at all (issue #730): the exception
        // propagates from here, before the coverage write below.
        var sales = new List<NayaxLastSalesReport>();
        var windowStarts = new List<DateTime>();
        var everyReadSaleHasAnInstant = true;
        foreach (var machine in machines)
        {
            var machineSales = await _nayax.GetMachineLastSalesAsync(machine.MachineID, cancellationToken);
            sales.AddRange(machineSales);

            var instants = machineSales.Select(sale => sale.AuthorizationInstantUtc).ToList();
            everyReadSaleHasAnInstant &= instants.TrueForAll(instant => instant.HasValue);

            // The earliest sale this machine's rolling window returned is the furthest back the
            // window demonstrably reaches for it. A machine that returned nothing constrains the
            // claim not at all: "the last N sales" being empty is the provider saying this machine
            // has no sales to miss.
            var earliest = instants.Where(instant => instant.HasValue).Select(instant => instant!.Value).ToList();
            if (earliest.Count > 0)
                windowStarts.Add(earliest.Min());
        }

        var persisted = await _store.PersistLatestSalesAsync(sales, cancellationToken);
        if (persisted.EarliestCompletedSaleByProductId.Count > 0)
            await _store.RebuildInventoryCostsAsync(
                persisted.EarliestCompletedSaleByProductId, cancellationToken);

        await RecordCoverageAsync(windowStarts, everyReadSaleHasAnInstant, readStartedAtUtc, cancellationToken);

        // Last, and only here: everything above has succeeded, so this is the one point at which the
        // sales in front of the operator really are current. A run that threw, was cancelled or was
        // refused never reaches this line and therefore never makes the next visit skip.
        _freshness.RecordSuccessfulSync(businessId, credential.CredentialRevision);

        return NayaxSalesSyncOutcome.Completed;
    }

    /// <summary>
    /// Records what this run accounted for (issue #730), and only ever after every machine was read
    /// and the whole batch was persisted and replayed - a run that threw anywhere above never gets
    /// here, which is exactly the rule that a partial run records no coverage.
    ///
    /// <para>The range claimed is <c>[the latest machine window start, the instant reading began)</c>.
    /// Every machine's window has to reach back at least that far for the business-wide claim "every
    /// sale in this range is now stored" to hold, so the <em>latest</em> of the per-machine window
    /// starts is the only defensible lower bound. Successive runs overlap, because each window
    /// normally reaches back further than the previous run's end, and the availability policy merges
    /// them into one contiguous coverage.</para>
    ///
    /// <para>Two cases claim nothing rather than guessing. A payload item carrying no authoritative
    /// instant is not imported at all by the store, so a batch containing one did not account for
    /// its own range. And a run in which no machine returned a single dated sale has no evidence of
    /// how far any window reaches - it knows only that the windows were empty, not what period that
    /// covers - so it records nothing and the period stays unverified rather than falsely complete.</para>
    /// </summary>
    private async Task RecordCoverageAsync(
        List<DateTime> windowStarts,
        bool everyReadSaleHasAnInstant,
        DateTime readStartedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!everyReadSaleHasAnInstant || windowStarts.Count == 0)
            return;

        var from = windowStarts.Max();
        if (from >= readStartedAtUtc)
            return;

        await _coverage.RecordAsync(
            ExternalProvider.Nayax, ProviderDataSource.SalesSync, from, readStartedAtUtc, cancellationToken);
    }
}
