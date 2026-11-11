using Inventory.Application.Nayax;
using Inventory.Application.NayaxSqs;
using Inventory.Application.Providers;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.SalesSync;

/// <summary>
/// The outcome of asking for a latest-sales synchronization: it ran, this business's sales already
/// arrive live from SQS so there was nothing to read (issue #1106), its sales were synchronized
/// moments ago so there was nothing to read (issue #843), a synchronization for this business is
/// already running (issue #1104), or it has run too many too recently (issue #681).
///
/// None of the bounds is thrown as an exception, exactly as <c>NayaxConnectionTestOutcome</c>
/// reports the connection test's rate limit: each is an expected answer the HTTP boundary turns into a
/// specific status, not a failure any other caller would want to catch. A <em>refusal</em> - no usable
/// Nayax connection, or a support session - is still an exception, because it is not an answer about
/// this synchronization at all.
/// </summary>
public sealed record NayaxSalesSyncOutcome
{
    private NayaxSalesSyncOutcome(
        TimeSpan? retryAfter,
        DateTime? recentlySyncedAtUtc,
        bool isBusy,
        bool liveViaSqs = false,
        DateTime? lastSqsMessageReceivedAtUtc = null,
        NayaxSqsNotLiveReason? sqsNotLiveReason = null)
    {
        RetryAfter = retryAfter;
        RecentlySyncedAtUtc = recentlySyncedAtUtc;
        IsBusy = isBusy;
        LiveViaSqs = liveViaSqs;
        LastSqsMessageReceivedAtUtc = lastSqsMessageReceivedAtUtc;
        SqsNotLiveReason = sqsNotLiveReason;
    }

    /// <summary>
    /// How long until this business may synchronize again, or <see langword="null"/> when the
    /// synchronization ran or was skipped as recent or busy.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// When this business's last fully successful synchronization finished, when that was recent
    /// enough that this one was skipped (issue #843); otherwise <see langword="null"/>. A skip reads
    /// nothing from Nayax, writes nothing, and claims no rate-limit allowance.
    /// </summary>
    public DateTime? RecentlySyncedAtUtc { get; }

    /// <summary>
    /// Whether a synchronization for this business was already running and this one gave up waiting
    /// for it (issue #1104).
    ///
    /// <para><strong>It is a bounded answer and not a failure.</strong> The waiter made no Nayax
    /// call, wrote nothing, recorded no coverage and claimed <em>no</em> rate-limit allowance - so an
    /// attempt that only ever waited cannot spend the business's own Refresh budget. The endpoint
    /// answers <c>409 Conflict</c>, kept distinct from <c>204</c>, <c>200</c> and <c>429</c> so each
    /// of those keeps exactly the meaning it already has.</para>
    /// </summary>
    public bool IsBusy { get; }

    /// <summary>
    /// Whether this business's sales arrive live from the shared Nayax SQS queue, so this unforced
    /// call read nothing at all (issue #1106).
    ///
    /// <para><strong>It is a claim about the queue path, not about a moment.</strong> It is
    /// <see langword="true"/> only while the business has an enabled sender, the reader polled
    /// successfully inside <see cref="NayaxSqsLivePolicy.PollWindow"/> with no failures, and this
    /// business's hourly reconciliation fully succeeded inside
    /// <see cref="NayaxSqsLivePolicy.ReconciliationWindow"/>. Like the recent-sync skip it reads
    /// nothing from Nayax, writes nothing and claims <em>no</em> rate-limit allowance; unlike it, a
    /// quiet queue keeps it true, because the time since the last message is not a condition.</para>
    /// </summary>
    public bool LiveViaSqs { get; }

    /// <summary>
    /// When a message from this business's SQS sender was last accepted, or <see langword="null"/>
    /// when none ever has or when this call made no live decision at all (a forced run). It is
    /// reported so the Dashboard can say what it last received, and is never a condition of being
    /// live.
    /// </summary>
    public DateTime? LastSqsMessageReceivedAtUtc { get; }

    /// <summary>
    /// Why this business is not live via SQS, or <see langword="null"/> when it is live or when no
    /// live decision was made - which is every forced run, and every call refused before the decision.
    /// </summary>
    public NayaxSqsNotLiveReason? SqsNotLiveReason { get; }

    // Cast because a record's own copy constructor makes a bare `new(null, null, false)` ambiguous.
    public static NayaxSalesSyncOutcome Completed { get; } = new((TimeSpan?)null, null, false);

    /// <summary>
    /// A synchronization for this business is already running and the wait for it ran out (issue
    /// #1104).
    /// </summary>
    public static NayaxSalesSyncOutcome Busy { get; } = new((TimeSpan?)null, null, true);

    public static NayaxSalesSyncOutcome RateLimited(TimeSpan retryAfter) => new(retryAfter, null, false);

    /// <summary>The sales were synchronized at <paramref name="lastSyncedAtUtc"/>, so this run was skipped.</summary>
    public static NayaxSalesSyncOutcome RecentlySynced(DateTime lastSyncedAtUtc) =>
        new(null, lastSyncedAtUtc, false);

    /// <summary>
    /// This business's sales arrive live from SQS, so nothing was read, written or rationed (issue
    /// #1106).
    /// </summary>
    /// <param name="lastSqsMessageReceivedAtUtc">
    /// When a message from its sender was last accepted, or <see langword="null"/> for a queue that
    /// has delivered nothing yet - which is a healthy quiet queue and still live.
    /// </param>
    public static NayaxSalesSyncOutcome LiveFromSqs(DateTime? lastSqsMessageReceivedAtUtc) =>
        new(
            null,
            null,
            false,
            liveViaSqs: true,
            lastSqsMessageReceivedAtUtc: lastSqsMessageReceivedAtUtc);

    /// <summary>
    /// The same outcome, carrying why this business is <em>not</em> live via SQS (issue #1106).
    ///
    /// <para>It is reported beside whatever the call then did - a run, a recent-sync skip, a busy
    /// skip or the run limit's refusal - rather than changing any of them: when a business is not
    /// live, everything about this synchronization behaves exactly as it did before.</para>
    /// </summary>
    public NayaxSalesSyncOutcome NotLiveViaSqs(
        NayaxSqsNotLiveReason reason, DateTime? lastSqsMessageReceivedAtUtc) =>
        new(RetryAfter, RecentlySyncedAtUtc, IsBusy, false, lastSqsMessageReceivedAtUtc, reason);
}

/// <summary>
/// How a call to <see cref="SyncLatestNayaxSales"/> gets the business's synchronization lock (issue
/// #1104).
/// </summary>
public enum NayaxSalesSyncLockMode
{
    /// <summary>
    /// Take the lock here, waiting up to <see cref="NayaxSalesSyncLock.RequestWait"/> for a run
    /// already in progress, and answer <see cref="NayaxSalesSyncOutcome.Busy"/> when it is still held.
    /// This is every request-driven call: the Dashboard's load and its Refresh control.
    /// </summary>
    Acquire = 0,

    /// <summary>
    /// The caller already holds this business's lease and keeps it for longer than this call - which
    /// is the hourly reconciliation of issue #1104, whose evidence writes and success record are part
    /// of the same protected run. It is not a way past the lock: the caller took it with
    /// <see cref="NayaxSalesSyncLock.TryAcquireAsync"/> and releases it in its own <c>finally</c>.
    /// </summary>
    AlreadyHeld = 1,
}

/// <summary>
/// What one synchronization read, told to a caller that needs the payload itself rather than only
/// whether the run succeeded (issue #1104).
///
/// <para><strong>It exists so the hourly reconciliation reads Nayax once.</strong> The comparison
/// needs the <c>lastSales</c> items per machine, and the run already reads exactly those: one Nayax
/// call per machine plus one. A reconciliation that read them a second time would double the most
/// expensive call in the application against somebody else's API for no new information.</para>
///
/// <para>Every method is synchronous and does nothing but remember: this is called from inside the
/// read loop, and an observer that could block, write or fail would make a sales synchronization
/// depend on whoever was watching it.</para>
/// </summary>
public interface INayaxLastSalesObserver
{
    /// <summary>The instant the run began reading, before the first Nayax call.</summary>
    void RunStarted(DateTime readStartedAtUtc);

    /// <summary>
    /// One machine's <c>lastSales</c> response, exactly as it was read and before anything is
    /// persisted from it.
    /// </summary>
    void MachineRead(long machineId, IReadOnlyList<NayaxLastSalesReport> sales);

    /// <summary>
    /// How many of the batch's transactions this save created, once the whole batch has been
    /// persisted.
    /// </summary>
    void SalesPersisted(int insertedCount);
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
/// <para><strong>Every pending inventory-cost rebuild is drained, not just this batch's</strong>
/// (issue #1103). The save records a durable pending rebuild per affected product and this run then
/// replays every pending row the business has. That is what makes a failed rebuild recoverable on
/// this path: the sales were already stored, so a run that only looked at its own batch found no
/// affected products the next time and never retried. A product whose history is still unreplayable
/// no longer fails the run: it keeps its pending row and failure code, is retried by every later run,
/// and is reported by product through <see cref="Costing.GetProductsNeedingCostAttention"/>.</para>
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
///
/// <para><strong>A business whose sales arrive live from SQS is not synchronized on load at all</strong>
/// (issue #1106, child of #1100). Once the queue reader of issue #1103 is delivering a business's
/// sales, opening the Dashboard has nothing to read: an unforced call answers
/// <see cref="NayaxSalesSyncOutcome.LiveFromSqs"/> having read nothing from Nayax, written nothing
/// and claimed no rate-limit allowance. The claim is narrow on purpose -
/// <see cref="NayaxSqsLivePolicy"/> requires an enabled sender, a reader that polled successfully
/// moments ago with no failures, and a reconciliation that fully succeeded for <em>this</em> business
/// inside 150 minutes - because what is being skipped is the read whose result the operator is
/// looking at. An enabled sender alone, or a recent reconciliation alone, says nothing about whether
/// the reader works. The order is the same as every other bound here and is asserted: the connection,
/// the status gate and the support-session refusal are resolved <em>first</em>, so an unusable
/// connection gets exactly the refusal it always did and is never told its sales are live. A forced
/// Refresh makes no live decision at all and reads <c>lastSales</c> exactly as it does today, and a
/// business that is not live behaves exactly as it did before, with the reason reported beside the
/// answer.</para>
///
/// <para><strong>Only one run per business at a time</strong> (issue #1104).
/// <see cref="NayaxSalesSyncLock"/> is shared with the hourly reconciliation, so a Dashboard load
/// cannot run beside an hourly run for the same business: each would spend the business's Nayax
/// allowance on the same rolling window and replay the same products. A request waits up to
/// <see cref="NayaxSalesSyncLock.DefaultRequestWait"/> and then answers
/// <see cref="NayaxSalesSyncOutcome.Busy"/> having made <em>no</em> Nayax call, no write, no coverage
/// record and - the part that matters most - no rate-limit claim, so an attempt that only ever waited
/// does not cost the business one of its own refreshes. The order is asserted: the freshness skip
/// still happens before the lock is awaited, because a run that has nothing to read must not wait a
/// minute to say so, and the allowance is claimed after the lock is held, because that is the first
/// moment a run is actually going to happen.</para>
/// </summary>
public sealed class SyncLatestNayaxSales
{
    private readonly INayaxLynxClient _nayax;
    private readonly ILatestNayaxSalesStore _store;
    private readonly NayaxSalesSyncRateLimiter _rateLimiter;
    private readonly NayaxSalesSyncFreshness _freshness;
    private readonly NayaxSalesSyncLock _syncLock;
    private readonly INayaxRequestCredentialProvider _credentials;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IProviderDataCoverageStore _coverage;
    private readonly INayaxSqsLiveStatusStore _sqsStatus;
    private readonly NayaxSqsReaderHealth _readerHealth;
    private readonly IClock _clock;

    public SyncLatestNayaxSales(
        INayaxLynxClient nayax,
        ILatestNayaxSalesStore store,
        NayaxSalesSyncRateLimiter rateLimiter,
        NayaxSalesSyncFreshness freshness,
        NayaxSalesSyncLock syncLock,
        INayaxRequestCredentialProvider credentials,
        ICurrentBusinessProvider currentBusiness,
        IProviderDataCoverageStore coverage,
        INayaxSqsLiveStatusStore sqsStatus,
        NayaxSqsReaderHealth readerHealth,
        IClock clock)
    {
        _nayax = nayax;
        _store = store;
        _rateLimiter = rateLimiter;
        _freshness = freshness;
        _syncLock = syncLock;
        _credentials = credentials;
        _currentBusiness = currentBusiness;
        _coverage = coverage;
        _sqsStatus = sqsStatus;
        _readerHealth = readerHealth;
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
    public Task<NayaxSalesSyncOutcome> Handle(bool force, CancellationToken cancellationToken = default) =>
        Handle(force, NayaxSalesSyncLockMode.Acquire, observer: null, cancellationToken);

    /// <summary>
    /// Synchronizes this business's latest Nayax sales, with the lock taken here or held by the
    /// caller, and optionally reporting what was read.
    /// </summary>
    /// <param name="force">As above: past the freshness window and nothing else.</param>
    /// <param name="lockMode">
    /// Whether this call takes the business's synchronization lock (every request) or the caller
    /// already holds it (the hourly reconciliation of issue #1104, whose evidence writes and success
    /// record belong to the same protected run).
    /// </param>
    /// <param name="observer">
    /// Told the run's start instant, each machine's <c>lastSales</c> response and how many
    /// transactions the save created, so a caller that needs the payload does not read Nayax twice.
    /// <see langword="null"/> for every request-driven call.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads, the persistence and the cost rebuild.</param>
    public async Task<NayaxSalesSyncOutcome> Handle(
        bool force,
        NayaxSalesSyncLockMode lockMode,
        INayaxLastSalesObserver? observer,
        CancellationToken cancellationToken)
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

        // After the refusals and before everything else, because the live answer is the strongest
        // "there is nothing to read" this use case has (issue #1106): a business whose queue is
        // demonstrably working does not need the three-minute window, the lock or an allowance. A
        // forced Refresh makes this decision not at all - it is asking for a lastSales read - and
        // neither does the hourly reconciliation, which always forces.
        var live = force ? null : await FindSqsLiveStatusAsync(cancellationToken);
        if (live is { Reason: null })
        {
            return NayaxSalesSyncOutcome.LiveFromSqs(live.LastMessageReceivedAtUtc);
        }

        if (!force
            && _freshness.FindRecentSync(businessId, credential.CredentialRevision) is { } lastSyncedAtUtc)
        {
            // Before the allowance is claimed, because a skip reads nothing: rationing a run that
            // never leaves the API would let a browser in a reload loop spend the business's own
            // Refresh button on nothing.
            return NotLive(NayaxSalesSyncOutcome.RecentlySynced(lastSyncedAtUtc), live);
        }

        if (lockMode is NayaxSalesSyncLockMode.AlreadyHeld)
        {
            // The hourly reconciliation took this business's lease with TryAcquireAsync and keeps it
            // across its own evidence writes and success record, so there is nothing to take here.
            return NotLive(await RunAsync(businessId, credential, observer, cancellationToken), live);
        }

        // After the freshness skip and before the allowance: a run that has nothing to read must not
        // wait a minute to say so, and the allowance belongs to a run that is actually going to
        // happen. The lease releases only if it was acquired, so a waiter that timed out never hands
        // back the holder's lock (see NayaxSalesSyncLease).
        using var lease = await _syncLock
            .AcquireForRequestAsync(businessId, cancellationToken)
            .ConfigureAwait(false);

        if (!lease.Acquired)
        {
            // A synchronization for this business is still running. Nothing was read from Nayax,
            // nothing was written, no coverage was recorded and - deliberately - no allowance was
            // claimed: an attempt that only ever waited must not cost the business a refresh.
            return NotLive(NayaxSalesSyncOutcome.Busy, live);
        }

        return NotLive(await RunAsync(businessId, credential, observer, cancellationToken), live);
    }

    /// <summary>
    /// Whether this business counts as live via SQS, from its own sender row and the reader's
    /// process-wide health record (issue #1106).
    ///
    /// <para>Both halves are read here, together, so the answer is one reading rather than two a
    /// restart or a failed poll could land between. The row read is an ordinary tenant-owned one in
    /// the caller's own scope, and it carries no sender id: whether one is recorded is the whole of
    /// what the decision needs, and the identity itself is on no member-facing surface.</para>
    /// </summary>
    private async Task<SqsLiveStatus> FindSqsLiveStatusAsync(CancellationToken cancellationToken)
    {
        var sender = await _sqsStatus.FindCurrentAsync(cancellationToken).ConfigureAwait(false);
        var health = _readerHealth.Current();

        var reason = NayaxSqsLivePolicy.FindNotLiveReason(
            new NayaxSqsLiveState(
                // No row at all and a row whose id was cleared are the same answer: neither
                // attributes anything on the shared queue.
                SenderRecorded: sender?.SenderIdRecorded ?? false,
                IsEnabled: sender?.IsEnabled ?? false,
                health.LastSuccessfulPollUtc,
                health.ConsecutivePollFailures,
                sender?.LastReconciliationSucceededAtUtc),
            _clock.UtcNow);

        return new SqsLiveStatus(reason, sender?.LastMessageReceivedAtUtc);
    }

    /// <summary>
    /// Carries a not-live reason on <paramref name="outcome"/>, and leaves the outcome exactly as it
    /// is when no live decision was made - a forced run - or when the business is live.
    /// </summary>
    private static NayaxSalesSyncOutcome NotLive(NayaxSalesSyncOutcome outcome, SqsLiveStatus? live) =>
        live is { Reason: { } reason }
            ? outcome.NotLiveViaSqs(reason, live.LastMessageReceivedAtUtc)
            : outcome;

    /// <summary>
    /// The run itself, with this business's synchronization lock held by this call or by its caller:
    /// claim the allowance, read every machine, save the batch once, drain the pending rebuilds and
    /// record what was covered.
    /// </summary>
    private async Task<NayaxSalesSyncOutcome> RunAsync(
        BusinessId businessId,
        NayaxRequestCredential credential,
        INayaxLastSalesObserver? observer,
        CancellationToken cancellationToken)
    {
        if (!_rateLimiter.TryClaim(businessId, out var retryAfter))
        {
            // Refused before the first Nayax call, so a refused request reads nothing from the
            // upstream and writes nothing here.
            return NayaxSalesSyncOutcome.RateLimited(retryAfter);
        }

        // Taken before the first read, so the range this run may claim never reaches past an instant
        // a sale could still have been authorized at while the run was reading.
        var readStartedAtUtc = _clock.UtcNow;
        observer?.RunStarted(readStartedAtUtc);

        // Fresh, never the cached list (issue #840): a successful run records business-wide sales
        // coverage for the range it read (issue #730), and a machine missing from a ten-minute-old
        // list would be claimed as covered without ever having been read.
        var machines = await _nayax.GetMachinesFreshAsync(cancellationToken);

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

            // Exactly what this machine returned, before anything is persisted from it: the hourly
            // reconciliation compares these items with the stored queue rows, and reading them a
            // second time would double the most expensive call in the application.
            observer?.MachineRead(machine.MachineID, machineSales);

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
        observer?.SalesPersisted(persisted.InsertedCount);

        // Unconditionally, and that is the change issue #1103 makes here. The save above recorded a
        // durable pending rebuild per affected product, and this drains every pending row this
        // business has - including one an earlier run left behind. Before, the rebuild ran only for
        // the products this batch's completed sales touched, so a rebuild that failed was never
        // retried: the sales were already stored, the next sync therefore reported no affected
        // products, and that product's costing position stayed stale until its next sale.
        //
        // A product whose history still cannot be replayed does not fail the synchronization: the
        // sales are stored, every other product was rebuilt, and the failed product keeps its
        // durable pending row with its attempt count and failure code, which every later run of
        // either sales path retries and the Dashboard's "costs need attention" read reports by
        // product. Before, one product's missing purchase failed the whole business's sync on every
        // visit and hid every fresh figure behind a "could not be synchronized" warning. The hourly
        // reconciliation still records no success while a failure is pending; it checks its own
        // drain for that.
        await _store.RebuildInventoryCostsAsync(cancellationToken);

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

    /// <summary>
    /// One live decision: why the business is not live, or <see langword="null"/> when it is, and
    /// what its queue last delivered (issue #1106).
    /// </summary>
    private sealed record SqsLiveStatus(
        NayaxSqsNotLiveReason? Reason, DateTime? LastMessageReceivedAtUtc);
}
