using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.NayaxSqs;

namespace Inventory.Application.NayaxReconciliation;

/// <summary>Why one business's hourly reconciliation ended the way it did (issue #1104).</summary>
public enum NayaxSalesReconciliationOutcome
{
    /// <summary>Every step succeeded, and the business's success instant was recorded.</summary>
    Succeeded = 10,

    /// <summary>
    /// A synchronization for this business was already running, so the hour was skipped: no Nayax
    /// call, no write, nothing changed.
    /// </summary>
    SkippedSyncRunning = 20,

    /// <summary>
    /// The business has synchronized too many times too recently (issue #681), so the hour was
    /// skipped. No Nayax call was made.
    /// </summary>
    SkippedRateLimited = 30,

    /// <summary>
    /// The business has no usable Nayax connection, its plan does not include the sales provider, or
    /// the scope was a support session. Nothing was read and no token was decrypted.
    /// </summary>
    SkippedGateRefused = 40,

    /// <summary>
    /// Something failed: a machine read, the save, a cost rebuild or an evidence write. Whatever
    /// committed stays committed, and the success instant is left exactly as it was.
    /// </summary>
    Failed = 50,
}

/// <summary>
/// What one business's hourly reconciliation did, as counts (issue #1104).
///
/// <para>Counts and an outcome, and deliberately nothing else: this record becomes one log line, and
/// a transaction id, an amount or a machine name in a log is a business's sales data in a place it
/// does not belong.</para>
/// </summary>
/// <param name="Outcome">How the run ended.</param>
/// <param name="ItemsRead">How many <c>lastSales</c> items the run read, across every machine.</param>
/// <param name="SalesAdded">How many transactions the save created - sales the queue never delivered.</param>
/// <param name="Verified">How many sales this run moved to <see cref="NayaxSqsVerification.Verified"/>.</param>
/// <param name="Conflicts">How many it moved to <see cref="NayaxSqsVerification.Conflict"/>.</param>
/// <param name="NeedsInvestigation">
/// How many it moved to <see cref="NayaxSqsVerification.NeedsInvestigation"/>.
/// </param>
/// <param name="AbsenceRows">How many <see cref="NayaxSaleProviderEvidenceKind.Absence"/> rows it wrote.</param>
/// <param name="EvidenceSkipped">
/// How many observations wrote nothing because they repeated what that sale's latest evidence of the
/// same kind already said and changed no state.
/// </param>
/// <param name="Failure">
/// The <em>type name</em> of the failure the run ended on, or <see langword="null"/>. Never the
/// exception and never its message: an upstream error can quote what it was given.
/// </param>
public sealed record NayaxSalesReconciliationResult(
    NayaxSalesReconciliationOutcome Outcome,
    int ItemsRead,
    int SalesAdded,
    int Verified,
    int Conflicts,
    int NeedsInvestigation,
    int AbsenceRows,
    int EvidenceSkipped,
    string? Failure)
{
    /// <summary>A run that did nothing, for one of the three skip reasons.</summary>
    public static NayaxSalesReconciliationResult Skipped(NayaxSalesReconciliationOutcome outcome) =>
        new(outcome, 0, 0, 0, 0, 0, 0, 0, null);

    /// <summary>Whether this run counted as succeeded, which is the only thing #1106 may trust.</summary>
    public bool Succeeded => Outcome is NayaxSalesReconciliationOutcome.Succeeded;
}

/// <summary>
/// One business's hourly reconciliation of what SQS delivered against what the business's own
/// <c>lastSales</c> says (issue #1104, child of #1100, under the repository owner's decision of
/// 5 November 2026: read <c>lastSales</c> every 60 minutes and compare).
///
/// <para><strong>It is one use case for one business, in that business's own service scope.</strong>
/// Nothing here takes a business id: the scope was resolved to the business before this was built, so
/// the credential read, the plan check, the Nayax call, the tenant query filters and the ownership
/// stamp are all the ones a member of that business would get. Which businesses get a scope at all is
/// <see cref="NayaxSalesReconciliationRunner"/>'s question.</para>
///
/// <para><strong>The order is the design, and every step of it is a rule from the issue.</strong></para>
/// <list type="number">
/// <item>Take this business's synchronization lease with <em>try</em>-acquire. A sync already running
/// means the hour is skipped with no Nayax call and no write: an hourly run has nobody waiting on it,
/// and the next hour is a better answer than a queue of background runs behind one slow sync.</item>
/// <item>Resolve the credential, which applies the connection, support-session and plan gates exactly
/// as a request does. A refused business is skipped and logged rather than raised.</item>
/// <item>Drain every pending inventory-cost rebuild this business has, <em>first</em> (issue #1103).
/// This is recovery work: a replay that failed hours ago is remembered as a durable row, and the one
/// thing that retries it is a run like this one. It makes no Nayax call, so it is correct to do before
/// the allowance is even considered.</item>
/// <item>Run the ordinary <see cref="SyncLatestNayaxSales"/> path with <c>force: true</c> and the
/// lease already held, observing what it read. That is where the machine list, the per-machine
/// <c>lastSales</c>, the write-locked save, the pending rebuilds and the coverage record all happen -
/// one implementation, not a second one that could disagree with the Dashboard's.</item>
/// <item>Compare, and record the evidence, over exactly what was read.</item>
/// <item>Only then record the success instant, and only if every one of those succeeded.</item>
/// </list>
///
/// <para><strong>It changes nothing about what counts.</strong> A <see cref="NayaxSqsVerification"/>
/// is evidence about a sale, never a reason to include or exclude it: stock consumption, costing and
/// revenue still follow the stored <c>TransactionStatusId</c>, so a conflicting or flagged row counts
/// exactly as it did before. Excluding a row, and the Owner decisions that could, are issue
/// #1105's.</para>
///
/// <para><strong>It never claims an absence is proof.</strong> A sale <c>lastSales</c> did not return
/// is flagged with a record of what the response <em>did</em> return, and nothing more - see
/// <see cref="NayaxSaleProviderAbsencePolicy"/> for why the provider's own contract cannot support
/// anything stronger.</para>
/// </summary>
public sealed class ReconcileNayaxSalesWithProvider
{
    private readonly SyncLatestNayaxSales _sync;
    private readonly ILatestNayaxSalesStore _sales;
    private readonly INayaxSaleProviderEvidenceStore _evidence;
    private readonly INayaxReconciliationSuccessStore _success;
    private readonly INayaxRequestCredentialProvider _credentials;
    private readonly NayaxSalesSyncLock _syncLock;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IClock _clock;

    public ReconcileNayaxSalesWithProvider(
        SyncLatestNayaxSales sync,
        ILatestNayaxSalesStore sales,
        INayaxSaleProviderEvidenceStore evidence,
        INayaxReconciliationSuccessStore success,
        INayaxRequestCredentialProvider credentials,
        NayaxSalesSyncLock syncLock,
        ICurrentBusinessProvider currentBusiness,
        IClock clock)
    {
        _sync = sync;
        _sales = sales;
        _evidence = evidence;
        _success = success;
        _credentials = credentials;
        _syncLock = syncLock;
        _currentBusiness = currentBusiness;
        _clock = clock;
    }

    /// <summary>
    /// Reconciles this scope's business once.
    ///
    /// <para>It does not throw for anything that is about this business: a refusal, a spent
    /// allowance, a held lock and a failure are all outcomes, because the runner's rule is that one
    /// business's trouble must not stop the others. Cancellation is the one exception and is allowed
    /// to propagate, because that is the host shutting down rather than a fact about a business.</para>
    /// </summary>
    public async Task<NayaxSalesReconciliationResult> Handle(CancellationToken cancellationToken)
    {
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken);

        // First, and with no waiting: a request-driven sync for this business holding the lock means
        // this hour is skipped entirely. Nothing below runs, so there is no Nayax call and no write.
        using var lease = await _syncLock
            .TryAcquireAsync(businessId, cancellationToken)
            .ConfigureAwait(false);

        if (!lease.Acquired)
        {
            return NayaxSalesReconciliationResult.Skipped(
                NayaxSalesReconciliationOutcome.SkippedSyncRunning);
        }

        try
        {
            // The same gates the request-driven sync applies, resolved here so a refused business is
            // a skip in the run's report rather than an exception the runner has to classify. The
            // sync applies them again itself; this read decrypts nothing a refusal would not have.
            await _credentials.GetForOperationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NayaxNotConnectedException)
        {
            return NayaxSalesReconciliationResult.Skipped(
                NayaxSalesReconciliationOutcome.SkippedGateRefused);
        }
        catch (SupportSessionNayaxUnavailableException)
        {
            // Unreachable from the hourly runner, whose scopes resolve no support session at all.
            // Handled because the refusal is part of the credential contract and a use case that
            // ignored half of it would be relying on its caller.
            return NayaxSalesReconciliationResult.Skipped(
                NayaxSalesReconciliationOutcome.SkippedGateRefused);
        }

        var observer = new ReadObserver();

        try
        {
            // Every pending inventory-cost rebuild this business has, before anything else. A replay
            // that failed earlier is a durable row and this is what retries it; it makes no Nayax
            // call, and its failures reappear in the sync's own drain below, which is what decides
            // whether this run counted as a success.
            await _sales.RebuildInventoryCostsAsync(cancellationToken).ConfigureAwait(false);

            var outcome = await _sync
                .Handle(force: true, NayaxSalesSyncLockMode.AlreadyHeld, observer, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.RetryAfter is not null)
            {
                return NayaxSalesReconciliationResult.Skipped(
                    NayaxSalesReconciliationOutcome.SkippedRateLimited);
            }

            var counts = await CompareAsync(observer, cancellationToken).ConfigureAwait(false);

            // Last, and only here: the machine list and every machine's lastSales were read, the save
            // committed, every rebuild drained and every observation recorded. A run that threw
            // anywhere above never reaches this line, which is exactly the rule that a partial run
            // records no success.
            await _success
                .RecordSucceededAsync(observer.ReadStartedAtUtc, cancellationToken)
                .ConfigureAwait(false);

            return counts with { Outcome = NayaxSalesReconciliationOutcome.Succeeded };
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            // One business's failure is one business's failure. Whatever committed stays committed -
            // the sales, the rebuilds, the evidence rows already written - and the success instant is
            // untouched, so the next hour tries again against the state that really exists.
            return new NayaxSalesReconciliationResult(
                NayaxSalesReconciliationOutcome.Failed,
                observer.ItemsRead,
                observer.SalesAdded,
                0,
                0,
                0,
                0,
                0,
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Compares everything the run read, machine by machine, and records what it saw.
    ///
    /// <para><strong>Each machine is answered on its own.</strong> The provider returns "the most
    /// recent sales" per machine, so the range its response demonstrably reached is per machine too -
    /// pooling the ranges would invent a window nobody read.</para>
    /// </summary>
    private async Task<NayaxSalesReconciliationResult> CompareAsync(
        ReadObserver observer, CancellationToken cancellationToken)
    {
        var observedAtUtc = _clock.UtcNow;
        var counters = new Counters();

        foreach (var machine in observer.Machines)
        {
            await CompareReturnedAsync(machine, observer.RunId, observedAtUtc, counters, cancellationToken)
                .ConfigureAwait(false);

            await ObserveAbsencesAsync(
                    machine, observer.RunId, observedAtUtc, observer.ReadStartedAtUtc, counters, cancellationToken)
                .ConfigureAwait(false);
        }

        return new NayaxSalesReconciliationResult(
            NayaxSalesReconciliationOutcome.Succeeded,
            observer.ItemsRead,
            observer.SalesAdded,
            counters.Verified,
            counters.Conflicts,
            counters.NeedsInvestigation,
            counters.AbsenceRows,
            counters.EvidenceSkipped,
            null);
    }

    /// <summary>
    /// The comparison half, for one machine: every transaction the response returned that this
    /// business's queue also delivered.
    /// </summary>
    private async Task CompareReturnedAsync(
        MachineObservation machine,
        Guid runId,
        DateTime observedAtUtc,
        Counters counters,
        CancellationToken cancellationToken)
    {
        if (machine.ByTransactionId.Count == 0)
        {
            return;
        }

        var sales = await _evidence
            .FindQueueSalesByTransactionIdsAsync(machine.ByTransactionId.Keys, cancellationToken)
            .ConfigureAwait(false);

        foreach (var sale in sales)
        {
            if (!machine.ByTransactionId.TryGetValue(sale.TransactionId, out var provider))
            {
                continue;
            }

            var comparison = NayaxSaleProviderComparisonPolicy.Compare(sale.Values, provider);

            var written = await _evidence
                .RecordComparisonAsync(
                    new NayaxSaleComparisonEvidence(
                        sale.SaleId,
                        runId,
                        observedAtUtc,
                        provider,
                        comparison.Fields,
                        comparison.ObservedState),
                    cancellationToken)
                .ConfigureAwait(false);

            counters.Record(written);
        }
    }

    /// <summary>
    /// The absence half, for one machine: the queue-delivered sales inside the range this machine's
    /// response reached that the response did not include.
    ///
    /// <para>Nothing is read at all unless the response had a range - two different instants - which
    /// is the whole of what "bracketed" can be judged against. An empty or single-item response
    /// therefore reads no sale and writes nothing, rather than asking a question with no evidence
    /// behind it.</para>
    /// </summary>
    private async Task ObserveAbsencesAsync(
        MachineObservation machine,
        Guid runId,
        DateTime observedAtUtc,
        DateTime readStartedAtUtc,
        Counters counters,
        CancellationToken cancellationToken)
    {
        if (machine.Window.EarliestReturnedInstantUtc is not { } earliest
            || machine.Window.LatestReturnedInstantUtc is not { } latest
            || earliest >= latest)
        {
            return;
        }

        var candidates = await _evidence
            .FindQueueSalesBracketedByAsync(machine.Window.MachineId, earliest, latest, cancellationToken)
            .ConfigureAwait(false);

        foreach (var sale in candidates)
        {
            if (machine.ByTransactionId.ContainsKey(sale.TransactionId))
            {
                // The response did return this one; it was compared above.
                continue;
            }

            var observation = NayaxSaleProviderAbsencePolicy.Observe(
                machine.Window,
                sale.Values.AuthorizationInstantUtc,
                sale.ReceivedViaSqsAtUtc,
                readStartedAtUtc);

            if (!observation.RecordsEvidence)
            {
                // Not bracketed, or still inside the receipt grace. Nothing is written and the sale
                // keeps its state: a rolling window rolling is not a finding.
                continue;
            }

            var written = await _evidence
                .RecordAbsenceAsync(
                    new NayaxSaleAbsenceEvidence(
                        sale.SaleId,
                        runId,
                        observedAtUtc,
                        machine.Window,
                        sale.Values.AuthorizationInstantUtc,
                        sale.ReceivedViaSqsAtUtc,
                        observation),
                    cancellationToken)
                .ConfigureAwait(false);

            counters.Record(written, absence: true);
        }
    }

    private static bool NotCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    /// <summary>
    /// What the run's one log line counts, accumulated as the evidence is written.
    ///
    /// <para>The counts measure different things on purpose. <see cref="Verified"/>,
    /// <see cref="Conflicts"/> and <see cref="NeedsInvestigation"/> count <em>state changes</em>, so
    /// a sale that was already verified and was compared again is not counted as newly verified.
    /// <see cref="AbsenceRows"/> counts rows appended. <see cref="EvidenceSkipped"/> counts
    /// observations that wrote nothing because they repeated what that sale's latest evidence of the
    /// same kind already said and changed no state.</para>
    /// </summary>
    private sealed class Counters
    {
        public int Verified { get; private set; }

        public int Conflicts { get; private set; }

        public int NeedsInvestigation { get; private set; }

        public int AbsenceRows { get; private set; }

        public int EvidenceSkipped { get; private set; }

        public void Record(NayaxSaleEvidenceWriteResult written, bool absence = false)
        {
            if (!written.Written)
            {
                EvidenceSkipped++;
                return;
            }

            if (absence)
            {
                AbsenceRows++;
            }

            if (!written.StateChanged)
            {
                return;
            }

            switch (written.State)
            {
                case NayaxSqsVerification.Verified:
                    Verified++;
                    break;
                case NayaxSqsVerification.Conflict:
                    Conflicts++;
                    break;
                case NayaxSqsVerification.NeedsInvestigation:
                    NeedsInvestigation++;
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>One machine's response, folded into what the comparison and the absence rule need.</summary>
    private sealed class MachineObservation
    {
        public MachineObservation(long machineId, IReadOnlyList<NayaxLastSalesReport> sales)
        {
            var instants = new List<DateTime>(sales.Count);
            ByTransactionId = new Dictionary<long, NayaxProviderSaleValues>(sales.Count);

            foreach (var item in sales)
            {
                if (item.AuthorizationInstantUtc is { } instant)
                {
                    instants.Add(instant);
                }

                // First occurrence wins. A response that listed one transaction twice says the same
                // thing twice, and the second copy must not quietly become the compared one.
                ByTransactionId.TryAdd(
                    item.TransactionID,
                    new NayaxProviderSaleValues(
                        MachineId: item.MachineID,
                        SettlementValue: item.SettlementValue,
                        AuthorizationInstantUtc: item.AuthorizationInstantUtc,
                        CurrencyCode: item.CurrencyCode,
                        NayaxProductId: item.NayaxProductId,
                        ProductName: item.ProductName));
            }

            Window = new NayaxProviderMachineWindow(
                MachineId: machineId,
                ReturnedItemCount: sales.Count,
                EarliestReturnedInstantUtc: instants.Count > 0 ? instants.Min() : null,
                LatestReturnedInstantUtc: instants.Count > 0 ? instants.Max() : null);
        }

        /// <summary>What the response returned, by transaction id, exactly as received.</summary>
        public Dictionary<long, NayaxProviderSaleValues> ByTransactionId { get; }

        /// <summary>The three facts the absence rule is judged against, and no completeness claim.</summary>
        public NayaxProviderMachineWindow Window { get; }
    }

    /// <summary>
    /// Remembers what the synchronization read, so the comparison runs over the one read the run
    /// already made rather than reading Nayax a second time.
    ///
    /// <para>It only remembers. Every method is synchronous and touches nothing but its own fields -
    /// a sales synchronization must not start depending on whoever happens to be watching it.</para>
    /// </summary>
    private sealed class ReadObserver : INayaxLastSalesObserver
    {
        /// <summary>
        /// This run's own identity, which every evidence row it writes carries, so the rows one run
        /// produced are readable as one run.
        /// </summary>
        public Guid RunId { get; } = Guid.NewGuid();

        public List<MachineObservation> Machines { get; } = [];

        public DateTime ReadStartedAtUtc { get; private set; }

        public int ItemsRead { get; private set; }

        public int SalesAdded { get; private set; }

        public void RunStarted(DateTime readStartedAtUtc) => ReadStartedAtUtc = readStartedAtUtc;

        public void MachineRead(long machineId, IReadOnlyList<NayaxLastSalesReport> sales)
        {
            ItemsRead += sales.Count;
            Machines.Add(new MachineObservation(machineId, sales));
        }

        public void SalesPersisted(int insertedCount) => SalesAdded = insertedCount;
    }
}
