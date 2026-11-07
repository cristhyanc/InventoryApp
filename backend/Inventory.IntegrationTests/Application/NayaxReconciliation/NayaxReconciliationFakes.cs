using Inventory.Application.NayaxReconciliation;
using Inventory.Domain.NayaxSqs;

namespace InventoryApi.Tests.Application.NayaxReconciliation;

/// <summary>
/// An in-memory <see cref="INayaxSaleProviderEvidenceStore"/> for the cases that are about the
/// reconciliation <em>use case</em> rather than about persistence (issue #1104).
///
/// <para>It applies the real comparison policy, the real transitions and the real "a repeated
/// identical observation writes nothing" rule, because all three are what the use case's counts are
/// derived from - a fake that recorded everything would make the counts meaningless. It applies the
/// comparison where the real store applies it too: to the sale it holds at the moment of the write,
/// not to a result the use case computed. What it does not do is take a write lock or touch a
/// database; <c>EfNayaxSaleProviderEvidenceStoreTests</c> covers that on a real one.</para>
/// </summary>
internal sealed class FakeNayaxSaleProviderEvidenceStore : INayaxSaleProviderEvidenceStore
{
    private readonly Dictionary<long, NayaxSqsVerification> _stateBySaleId = [];
    private readonly Dictionary<long, NayaxVerifiableSale> _salesById = [];
    private readonly Dictionary<(long SaleId, NayaxSaleProviderEvidenceKind Kind), object> _latest = [];
    private readonly Dictionary<long, NayaxSaleFieldComparisons> _fieldsBySaleId = [];

    private long _nextEvidenceId = 1;

    /// <summary>Every comparison and absence row this fake "wrote", in order.</summary>
    public List<object> Written { get; } = [];

    /// <summary>The comparison rows among them.</summary>
    public List<NayaxSaleComparisonEvidence> Comparisons =>
        [.. Written.OfType<NayaxSaleComparisonEvidence>()];

    /// <summary>The absence rows among them.</summary>
    public List<NayaxSaleAbsenceEvidence> Absences => [.. Written.OfType<NayaxSaleAbsenceEvidence>()];

    /// <summary>What <see cref="FindQueueSalesBracketedByAsync"/> was asked, in order.</summary>
    public List<(long MachineId, DateTime EarliestExclusiveUtc, DateTime LatestExclusiveUtc)> Bracketing
    { get; } = [];

    /// <summary>Set to make either read fail, for the cases about a run that did not finish.</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>Set to make an evidence write fail, for the same reason.</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>The state a sale holds now, as this fake has applied the real transitions to it.</summary>
    public NayaxSqsVerification? StateOf(long saleId) =>
        _stateBySaleId.TryGetValue(saleId, out var state) ? state : null;

    /// <summary>Adds one queue-delivered sale in <paramref name="state"/>.</summary>
    public FakeNayaxSaleProviderEvidenceStore WithQueueSale(
        long saleId,
        long transactionId,
        NayaxStoredSaleValues values,
        DateTime? receivedViaSqsAtUtc,
        NayaxSqsVerification state = NayaxSqsVerification.Unverified)
    {
        _stateBySaleId[saleId] = state;
        _salesById[saleId] = new NayaxVerifiableSale(
            saleId, transactionId, state, receivedViaSqsAtUtc, values);
        return this;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<NayaxVerifiableSale>> FindQueueSalesByTransactionIdsAsync(
        IReadOnlyCollection<long> transactionIds, CancellationToken cancellationToken)
    {
        if (ReadFailure is { } failure)
        {
            throw failure;
        }

        IReadOnlyList<NayaxVerifiableSale> found =
        [
            .. _salesById.Values
                .Where(sale => transactionIds.Contains(sale.TransactionId))
                .Select(Current)
                .OrderBy(sale => sale.SaleId),
        ];

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<NayaxVerifiableSale>> FindQueueSalesBracketedByAsync(
        long machineId,
        DateTime earliestExclusiveUtc,
        DateTime latestExclusiveUtc,
        CancellationToken cancellationToken)
    {
        if (ReadFailure is { } failure)
        {
            throw failure;
        }

        Bracketing.Add((machineId, earliestExclusiveUtc, latestExclusiveUtc));

        IReadOnlyList<NayaxVerifiableSale> found =
        [
            .. _salesById.Values
                .Where(sale => sale.Values.MachineId == machineId
                    && sale.Values.AuthorizationInstantUtc > earliestExclusiveUtc
                    && sale.Values.AuthorizationInstantUtc < latestExclusiveUtc)
                .Select(Current)
                .OrderBy(sale => sale.SaleId),
        ];

        return Task.FromResult(found);
    }

    /// <summary>
    /// The field results the real policy decided for a sale's latest comparison, which the real
    /// store decides inside its own write and therefore nothing hands to it.
    /// </summary>
    public NayaxSaleFieldComparisons? FieldsOf(long saleId) =>
        _fieldsBySaleId.TryGetValue(saleId, out var fields) ? fields : null;

    /// <inheritdoc />
    public Task<NayaxSaleEvidenceWriteResult> RecordComparisonAsync(
        NayaxSaleComparisonEvidence evidence, CancellationToken cancellationToken)
    {
        if (WriteFailure is { } failure)
        {
            throw failure;
        }

        if (!_salesById.TryGetValue(evidence.SaleId, out var sale))
        {
            return Task.FromResult(new NayaxSaleEvidenceWriteResult(false, null, null, false));
        }

        // The real policy, applied where the real store applies it: inside the write, to the sale
        // the write itself reads, rather than to a result a caller computed earlier.
        var comparison = NayaxSaleProviderComparisonPolicy.Compare(sale.Values, evidence.Provider);
        _fieldsBySaleId[evidence.SaleId] = comparison.Fields;

        return RecordAsync(
            evidence,
            evidence.SaleId,
            NayaxSaleProviderEvidenceKind.Comparison,
            comparison.ObservedState,
            (evidence.Provider, comparison.Fields));
    }

    /// <inheritdoc />
    public Task<NayaxSaleEvidenceWriteResult> RecordAbsenceAsync(
        NayaxSaleAbsenceEvidence evidence, CancellationToken cancellationToken) =>
        RecordAsync(
            evidence,
            evidence.SaleId,
            NayaxSaleProviderEvidenceKind.Absence,
            evidence.Observation.ObservedState,
            (evidence.Window,
                evidence.SaleAuthorizationInstantUtc,
                evidence.SaleReceivedViaSqsAtUtc,
                evidence.Observation.Bracketed,
                evidence.Observation.OutsideReceiptGrace));

    private Task<NayaxSaleEvidenceWriteResult> RecordAsync(
        object evidence,
        long saleId,
        NayaxSaleProviderEvidenceKind kind,
        NayaxSqsVerification? observedState,
        object observation)
    {
        if (WriteFailure is { } failure)
        {
            throw failure;
        }

        if (!_stateBySaleId.TryGetValue(saleId, out var current))
        {
            return Task.FromResult(new NayaxSaleEvidenceWriteResult(false, null, null, false));
        }

        var next = NayaxSqsVerificationTransitions.Apply(current, observedState);
        var stateChanged = next != current;

        var repeats = _latest.TryGetValue((saleId, kind), out var latest) && latest.Equals(observation);
        if (!stateChanged && repeats)
        {
            return Task.FromResult(new NayaxSaleEvidenceWriteResult(false, null, current, false));
        }

        _latest[(saleId, kind)] = observation;
        Written.Add(evidence);

        if (stateChanged && next is { } applied)
        {
            _stateBySaleId[saleId] = applied;
        }

        return Task.FromResult(
            new NayaxSaleEvidenceWriteResult(true, _nextEvidenceId++, next, stateChanged));
    }

    private NayaxVerifiableSale Current(NayaxVerifiableSale sale) =>
        sale with { Verification = _stateBySaleId[sale.SaleId] };
}

/// <summary>
/// An in-memory <see cref="INayaxReconciliationSuccessStore"/> that remembers every instant it was
/// asked to record, so a test can assert both "it was set to the run's start" and "it was never
/// touched" (issue #1104).
/// </summary>
internal sealed class FakeNayaxReconciliationSuccessStore : INayaxReconciliationSuccessStore
{
    /// <summary>Every instant recorded, in order. Empty means the value was never written.</summary>
    public List<DateTime> Recorded { get; } = [];

    /// <summary>Set to make the record fail, for the case about a run that could not finish.</summary>
    public Exception? Failure { get; set; }

    /// <inheritdoc />
    public Task RecordSucceededAsync(DateTime runStartedAtUtc, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        Recorded.Add(runStartedAtUtc);
        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="INayaxSalesReconciliationSchedule"/> that records what it was asked to wait and
/// returns immediately, so the runner's cadence is assertable without any test waiting (issue #1104).
///
/// <para>It can also end the loop after a stated number of delays, which is how a test drives
/// "the loop keeps sweeping" without leaving one running.</para>
/// </summary>
internal sealed class FakeNayaxSalesReconciliationSchedule : INayaxSalesReconciliationSchedule
{
    private readonly CancellationTokenSource? _stopAfter;
    private readonly int _delaysBeforeStopping;

    public FakeNayaxSalesReconciliationSchedule(
        CancellationTokenSource? stopAfter = null, int delaysBeforeStopping = 0)
    {
        _stopAfter = stopAfter;
        _delaysBeforeStopping = delaysBeforeStopping;
    }

    /// <summary>Every delay asked for, in order.</summary>
    public List<TimeSpan> Delays { get; } = [];

    /// <inheritdoc />
    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);

        if (_stopAfter is not null && Delays.Count >= _delaysBeforeStopping)
        {
            await _stopAfter.CancelAsync();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// An <see cref="INayaxSalesReconciliationLog"/> that remembers its lines, so the one log line per
/// business the issue asks for is assertable (issue #1104).
/// </summary>
internal sealed class FakeNayaxSalesReconciliationLog : INayaxSalesReconciliationLog
{
    public int Started { get; private set; }

    public int Stopped { get; private set; }

    public List<int> SweepSizes { get; } = [];

    public List<(int BusinessId, NayaxSalesReconciliationResult Result)> Reconciled { get; } = [];

    public List<(int BusinessId, string FailureType)> Failed { get; } = [];

    public List<string> EligibilityFailures { get; } = [];

    public void ReconciliationStarted() => Started++;

    public void ReconciliationStopped() => Stopped++;

    public void SweepStarted(int eligibleBusinesses) => SweepSizes.Add(eligibleBusinesses);

    public void BusinessReconciled(int businessId, NayaxSalesReconciliationResult result) =>
        Reconciled.Add((businessId, result));

    public void BusinessFailed(int businessId, string failureType) =>
        Failed.Add((businessId, failureType));

    public void EligibilityReadFailed(string failureType) => EligibilityFailures.Add(failureType);
}

/// <summary>
/// An <see cref="INayaxSalesReconciliationScopes"/> whose per-business scope runs whatever the test
/// gave it, so the runner's own rules - one business at a time, in order, and one business's failure
/// never stopping the others - are assertable without a container (issue #1104).
/// </summary>
internal sealed class FakeNayaxSalesReconciliationScopes : INayaxSalesReconciliationScopes
{
    private readonly Func<int, Task<NayaxSalesReconciliationResult>> _reconcile;
    private readonly Func<Task<IReadOnlyList<int>>> _eligible;

    public FakeNayaxSalesReconciliationScopes(
        IReadOnlyList<int> eligibleBusinessIds,
        Func<int, Task<NayaxSalesReconciliationResult>> reconcile)
        : this(() => Task.FromResult(eligibleBusinessIds), reconcile)
    {
    }

    public FakeNayaxSalesReconciliationScopes(
        Func<Task<IReadOnlyList<int>>> eligible,
        Func<int, Task<NayaxSalesReconciliationResult>> reconcile)
    {
        _eligible = eligible;
        _reconcile = reconcile;
    }

    /// <summary>The businesses a scope was opened for, in order.</summary>
    public List<int> ScopesOpened { get; } = [];

    /// <summary>How many scopes are open right now, which is how "one at a time" is asserted.</summary>
    public int OpenScopes { get; private set; }

    /// <summary>The highest number of scopes that were ever open at once.</summary>
    public int MaxConcurrentScopes { get; private set; }

    /// <summary>How many scopes were disposed.</summary>
    public int ScopesDisposed { get; private set; }

    public Task<IReadOnlyList<int>> ListEligibleBusinessIdsAsync(CancellationToken cancellationToken) =>
        _eligible();

    public INayaxSalesReconciliationBusinessScope CreateForBusiness(int businessId)
    {
        ScopesOpened.Add(businessId);
        OpenScopes++;
        MaxConcurrentScopes = Math.Max(MaxConcurrentScopes, OpenScopes);

        return new Scope(this, businessId);
    }

    private sealed class Scope : INayaxSalesReconciliationBusinessScope
    {
        private readonly FakeNayaxSalesReconciliationScopes _owner;
        private readonly int _businessId;

        public Scope(FakeNayaxSalesReconciliationScopes owner, int businessId)
        {
            _owner = owner;
            _businessId = businessId;
        }

        public Task<NayaxSalesReconciliationResult> ReconcileAsync(CancellationToken cancellationToken) =>
            _owner._reconcile(_businessId);

        public ValueTask DisposeAsync()
        {
            _owner.OpenScopes--;
            _owner.ScopesDisposed++;
            return ValueTask.CompletedTask;
        }
    }
}
