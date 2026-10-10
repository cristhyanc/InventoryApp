using System.Globalization;
using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Nayax;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.SaleTimestampRepair;

/// <summary>
/// Builds a Nayax sale timestamp repair plan (issue #472): it collects authoritative
/// <c>AuthorizationDateTimeGMT</c> values from the supported sources, lets
/// <see cref="NayaxSaleTimestampRepairPolicy"/> decide every stored sale against them, and projects
/// those decisions onto what an operator has to see before approving a change to historical
/// financial records - business dates, revenue movement between business days, the products whose
/// costing would be replayed and from when, the transactions no source covered, the transactions the
/// source carries that this business holds no sale for, and the fixed-cutoff reconciliation.
///
/// It writes nothing at all. The preview use case persists the plan it returns as a draft; this type
/// has no transaction and no write path.
///
/// <b>Normalization is not repeated here.</b> Both sources hand their values through the one
/// integration-boundary parser established by issues #380 and #471 -
/// <see cref="NayaxLastSalesReport.AuthorizationInstantUtc"/> for the live window and the export
/// reader's own <c>AuthorizationDateTimeGMT</c> column reading for an export - so this code never
/// parses a timestamp, never applies an offset, and never derives one instant from another. A source
/// that carried no readable value yields evidence with no instant, which the policy reports as
/// explicitly unresolved.
/// </summary>
internal sealed class NayaxSaleTimestampRepairProjection
{
    private readonly INayaxSaleTimestampRepairStore _store;
    private readonly INayaxLynxClient _nayax;
    private readonly INayaxSalesWorkbookReader _workbook;
    private readonly IBusinessCalendar _calendar;
    private readonly IClock _clock;

    public NayaxSaleTimestampRepairProjection(
        INayaxSaleTimestampRepairStore store,
        INayaxLynxClient nayax,
        INayaxSalesWorkbookReader workbook,
        IBusinessCalendar calendar,
        IClock clock)
    {
        _store = store;
        _nayax = nayax;
        _workbook = workbook;
        _calendar = calendar;
        _clock = clock;
    }

    public async Task<(NayaxSaleTimestampRepairPreview Preview, NayaxSaleTimestampRepairPlan Plan)> BuildAsync(
        NayaxSaleTimestampRepairPreviewRequest request,
        NayaxSalesFileInput? evidenceExport,
        Guid previewId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.IncludeLatestSalesApiEvidence && evidenceExport is null)
            throw new DomainValidationException(
                "A sale timestamp repair needs at least one authoritative source: the live Nayax last-sales "
                    + "window, an operator export carrying the AuthorizationDateTimeGMT column, or both.");
        if (request.Reconciliation is { } window && window.ToBusinessDate < window.FromBusinessDate)
            throw new DomainValidationException(
                "The reconciliation window's last business date cannot be before its first.");

        var evidence = new List<NayaxSaleTimestampEvidence>();
        var exportRows = new List<NayaxSalesImportRow>();
        if (request.IncludeLatestSalesApiEvidence)
            evidence.AddRange(await ReadLastSalesEvidenceAsync(cancellationToken));
        if (evidenceExport is not null)
        {
            exportRows.AddRange(ReadExport(evidenceExport));
            evidence.AddRange(ExportEvidence(exportRows, evidenceExport.FileName));
        }

        // The examined set is derived from the evidence, never from an assumed period: the range it
        // covers is read off the evidence instants and the stored instants of the sales it names, and
        // every stored sale inside that range is examined so the ones no source covered stay visible.
        var transactionIds = evidence.Select(item => item.TransactionId).Distinct().ToArray();
        var named = await _store.ListSalesByTransactionIdAsync(transactionIds, cancellationToken);
        var (examinedFrom, examinedTo) = NayaxSaleTimestampRepairSales.Range(
            evidence, named, ReconciliationWindowUtc(request.Reconciliation));
        var sales = await NayaxSaleTimestampRepairSales.ReadAsync(
            _store, transactionIds, examinedFrom, examinedTo, cancellationToken);
        var decisions = NayaxSaleTimestampRepairPolicy.Decide(sales, evidence);

        var candidates = await _store.GetProductCandidatesAsync(cancellationToken);
        var affectedProducts = await BuildAffectedProductsAsync(decisions, candidates, cancellationToken);
        var rows = decisions.Select(ToRow).ToList();
        var missing = BuildMissingSales(sales, evidence, exportRows);
        var reconciliation = request.Reconciliation is null
            ? null
            : await BuildReconciliationAsync(request.Reconciliation, decisions, missing, cancellationToken);

        var createdAt = _clock.UtcNow;
        var preview = new NayaxSaleTimestampRepairPreview(
            previewId,
            evidence.Count,
            examinedFrom,
            examinedTo,
            decisions.Count,
            decisions.Count(x => x.Outcome == NayaxSaleTimestampRepairOutcome.Repairable),
            decisions.Count(x => x.Outcome == NayaxSaleTimestampRepairOutcome.AlreadyCorrect),
            decisions.Count(x => x.Outcome == NayaxSaleTimestampRepairOutcome.Unresolved),
            rows,
            BuildRevenueMovement(decisions),
            affectedProducts,
            missing,
            reconciliation,
            createdAt,
            createdAt.Add(NayaxSaleTimestampRepairPolicy.PreviewLifetime));
        return (preview, new(previewId, examinedFrom, examinedTo, decisions, affectedProducts));
    }

    /// <summary>
    /// The requested reconciliation window as UTC instants, or <c>null</c> when none was requested.
    /// The examined range always covers it: a reconciliation that reported a business day without
    /// examining every stored sale on it would hide exactly the rows it exists to surface - the ones
    /// no source covered - so the window widens the range rather than filtering it.
    /// </summary>
    private (DateTime FromUtc, DateTime ToUtc)? ReconciliationWindowUtc(
        NayaxSaleTimestampReconciliationRequest? request) =>
        request is null
            ? null
            : (_calendar.StartOfBusinessDayUtc(request.FromBusinessDate),
                _calendar.StartOfBusinessDayUtc(request.ToBusinessDate.AddDays(1)).AddTicks(-1));

    /// <summary>
    /// The live rolling window, machine by machine, exactly as the latest-sales synchronization reads
    /// it. A payload item without usable identifiers is no evidence about anything; one whose GMT
    /// value could not be read is evidence with no instant, so the stored sale stays unresolved
    /// instead of being moved to a guessed time.
    /// </summary>
    private async Task<List<NayaxSaleTimestampEvidence>> ReadLastSalesEvidenceAsync(CancellationToken cancellationToken)
    {
        var readAt = _clock.UtcNow;
        var evidence = new List<NayaxSaleTimestampEvidence>();
        foreach (var machine in await _nayax.GetMachinesAsync(cancellationToken))
            foreach (var sale in await _nayax.GetMachineLastSalesAsync(machine.MachineID, cancellationToken))
            {
                if (sale.TransactionID <= 0 || sale.MachineID <= 0)
                    continue;
                evidence.Add(new(
                    sale.TransactionID,
                    sale.MachineID,
                    sale.AuthorizationInstantUtc,
                    sale.SettlementValue,
                    NayaxSaleTimestampEvidenceSource.NayaxLastSalesApi,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"lastSales machine {sale.MachineID} read {readAt:yyyy-MM-ddTHH:mm:ssZ}")));
            }
        return evidence;
    }

    private IReadOnlyList<NayaxSalesImportRow> ReadExport(NayaxSalesFileInput export)
    {
        if (!NayaxSalesExportFormats.IsSupported(export.FileName))
            throw new DomainValidationException(NayaxSalesExportFormats.UnsupportedMessage);

        using var content = export.OpenReadStream();
        return _workbook.Read(content, export.FileName);
    }

    /// <summary>
    /// An operator export's evidence. The export is read for its authorization instants only: it
    /// imports nothing, creates no sale and updates no amount, status or product mapping.
    ///
    /// An export that does not carry the <c>AuthorizationDateTimeGMT</c> column at all is refused
    /// outright rather than quietly producing no evidence. That column is the authoritative field;
    /// the workbook the operator supplied on 8 October 2026 carried <c>Updated Date and Time (GMT)</c>
    /// instead, which is an update time and not an authorization time, and a machine-local column
    /// cannot be converted without a source-timezone contract Nayax does not publish for the export
    /// (docs/architecture.md § Nayax sale timestamps). Refusing is what stops an export like that
    /// from being mistaken for a source.
    /// </summary>
    private static List<NayaxSaleTimestampEvidence> ExportEvidence(
        IReadOnlyList<NayaxSalesImportRow> rows,
        string fileName)
    {
        if (rows.Count == 0)
            throw new DomainValidationException("The uploaded export carries no data rows.");
        if (rows.All(row => row.AuthorizationDateTimeGmtInput == NayaxSalesGmtInput.NotProvided))
            throw new DomainValidationException(
                "The uploaded export has no AuthorizationDateTimeGMT column, so it carries no authorization "
                    + "times. Export the transactions again with that column included; an update time or a "
                    + "machine-local time is not an authorization time and is never substituted for one.");

        var reference = string.Create(CultureInfo.InvariantCulture, $"operator export {fileName}");
        return rows
            .Where(row => row.TransactionId > 0
                && row.MachineId > 0
                && row.AuthorizationDateTimeGmtInput != NayaxSalesGmtInput.NotProvided)
            .Select(row => new NayaxSaleTimestampEvidence(
                row.TransactionId,
                row.MachineId,
                row.AuthorizationDateTimeGmtInput == NayaxSalesGmtInput.Valid ? row.AuthorizationDateTimeGmt : null,
                row.SettlementValue,
                NayaxSaleTimestampEvidenceSource.OperatorExport,
                reference))
            .ToList();
    }

    private NayaxSaleTimestampRepairRow ToRow(NayaxSaleTimestampDecision decision) =>
        new(
            decision.Sale.TransactionId,
            decision.Sale.MachineId,
            decision.Sale.MachineName,
            decision.Sale.SettlementValue,
            decision.Sale.TransactionStatusId,
            NayaxTransactionStatusClassifier.IsCompletedSale(decision.Sale.TransactionStatusId),
            decision.Sale.StoredInstantUtc,
            _calendar.ToBusinessDate(decision.Sale.StoredInstantUtc),
            decision.RepairedInstantUtc,
            decision.RepairedInstantUtc is { } repaired ? _calendar.ToBusinessDate(repaired) : null,
            decision.Outcome,
            decision.UnresolvedReason,
            decision.Evidence?.Source,
            decision.Evidence?.Reference);

    /// <summary>
    /// Revenue the repair moves between business days, per day, from the completed repairable
    /// sales alone. A sale that does not change its business date contributes nothing even when its
    /// instant moves within the day.
    /// </summary>
    private List<NayaxSaleTimestampRevenueMovement> BuildRevenueMovement(
        IReadOnlyList<NayaxSaleTimestampDecision> decisions)
    {
        var leaving = new Dictionary<DateTime, decimal>();
        var arriving = new Dictionary<DateTime, decimal>();
        foreach (var decision in decisions)
        {
            if (decision.RepairedInstantUtc is not { } repaired
                || !NayaxTransactionStatusClassifier.IsCompletedSale(decision.Sale.TransactionStatusId))
                continue;

            var from = _calendar.ToBusinessDate(decision.Sale.StoredInstantUtc);
            var to = _calendar.ToBusinessDate(repaired);
            if (from == to)
                continue;

            leaving[from] = leaving.GetValueOrDefault(from) + decision.Sale.SettlementValue;
            arriving[to] = arriving.GetValueOrDefault(to) + decision.Sale.SettlementValue;
        }

        return leaving.Keys.Concat(arriving.Keys)
            .Distinct()
            .OrderBy(date => date)
            .Select(date => new NayaxSaleTimestampRevenueMovement(
                date,
                leaving.GetValueOrDefault(date),
                arriving.GetValueOrDefault(date),
                arriving.GetValueOrDefault(date) - leaving.GetValueOrDefault(date)))
            .ToList();
    }

    /// <summary>
    /// The products whose costing the repair would replay, each from the earlier of its affected
    /// sales' old and new instants. Only a completed sale consumes costed inventory, and only a sale
    /// matched to a local product has inventory to replay; an unmatched one stays visible through its
    /// own row and its costing provenance instead.
    /// </summary>
    private async Task<IReadOnlyList<NayaxSaleTimestampRepairProduct>> BuildAffectedProductsAsync(
        IReadOnlyList<NayaxSaleTimestampDecision> decisions,
        IReadOnlyList<ProductMatchCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var earliest = new Dictionary<long, DateTime>();
        foreach (var decision in decisions)
        {
            if (decision.Outcome != NayaxSaleTimestampRepairOutcome.Repairable
                || !NayaxTransactionStatusClassifier.IsCompletedSale(decision.Sale.TransactionStatusId))
                continue;

            var productId = ProductMatcher.Match(candidates, decision.Sale.NayaxProductId, decision.Sale.ProductName);
            if (productId is null)
                continue;

            var from = NayaxSaleTimestampRepairPolicy.EarliestAffectedInstant(decision);
            if (!earliest.TryGetValue(productId.Value, out var existing) || from < existing)
                earliest[productId.Value] = from;
        }

        if (earliest.Count == 0)
            return [];

        var cutoffs = await _store.GetTransitionCutoffsAsync(earliest.Keys.ToList(), cancellationToken);
        var names = candidates.ToDictionary(candidate => candidate.Id, candidate => candidate.Name);
        return earliest
            .OrderBy(item => item.Key)
            .Select(item =>
            {
                var hasCutoff = cutoffs.TryGetValue(item.Key, out var cutoff);
                return new NayaxSaleTimestampRepairProduct(
                    item.Key,
                    names.GetValueOrDefault(item.Key, string.Empty),
                    item.Value,
                    hasCutoff,
                    hasCutoff ? cutoff : null,
                    hasCutoff && item.Value > cutoff);
            })
            .ToList();
    }

    /// <summary>
    /// Transactions an export carries that this business holds no sale for. They are missing sales,
    /// not timestamp defects: a repair cannot create a sale, and counting them as repaired rows would
    /// claim a timestamp change explains revenue that was never imported.
    /// </summary>
    private List<NayaxSaleTimestampMissingSale> BuildMissingSales(
        IReadOnlyList<StoredNayaxSale> sales,
        IReadOnlyList<NayaxSaleTimestampEvidence> evidence,
        IReadOnlyList<NayaxSalesImportRow> exportRows)
    {
        var stored = sales.Select(sale => sale.TransactionId).ToHashSet();
        var statuses = exportRows
            .Where(row => row.TransactionId > 0)
            .GroupBy(row => row.TransactionId)
            .ToDictionary(group => group.Key, group => group.First().TransactionStatusId);

        return evidence
            .Where(item => !stored.Contains(item.TransactionId))
            .GroupBy(item => item.TransactionId)
            .Select(group => group.First())
            .OrderBy(item => item.AuthorizationInstantUtc)
            .ThenBy(item => item.TransactionId)
            .Select(item =>
            {
                var status = statuses.GetValueOrDefault(item.TransactionId);
                return new NayaxSaleTimestampMissingSale(
                    item.TransactionId,
                    item.MachineId,
                    item.AuthorizationInstantUtc,
                    item.AuthorizationInstantUtc is { } at ? _calendar.ToBusinessDate(at) : null,
                    item.SettlementValue ?? 0m,
                    status,
                    NayaxTransactionStatusClassifier.IsCompletedSale(status),
                    item.Source);
            })
            .ToList();
    }

    /// <summary>
    /// The fixed-cutoff reconciliation. "Before" is the completed sales the window already holds;
    /// "after" re-dates the repairable ones and re-selects the window from their new instants, so a
    /// sale moving into the window is counted and one moving out of it is not. Both sides exclude
    /// everything authorized at or after the cutoff, because a sale the compared export was taken too
    /// early to contain is not a discrepancy.
    /// </summary>
    private async Task<NayaxSaleTimestampReconciliation> BuildReconciliationAsync(
        NayaxSaleTimestampReconciliationRequest request,
        IReadOnlyList<NayaxSaleTimestampDecision> decisions,
        IReadOnlyList<NayaxSaleTimestampMissingSale> missing,
        CancellationToken cancellationToken)
    {
        var fromUtc = _calendar.StartOfBusinessDayUtc(request.FromBusinessDate);
        var toUtc = _calendar.StartOfBusinessDayUtc(request.ToBusinessDate.AddDays(1));
        var windowed = await _store.ListCompletedSalesAsync(fromUtc, toUtc, cancellationToken);

        // The examined sales are unioned in by transaction so a completed sale currently outside the
        // window, but re-dated into it, is part of the "after" position.
        var repaired = decisions
            .Where(decision => decision.Outcome == NayaxSaleTimestampRepairOutcome.Repairable)
            .ToDictionary(decision => decision.Sale.TransactionId, decision => decision.RepairedInstantUtc!.Value);
        var all = windowed
            .Concat(decisions.Select(decision => decision.Sale))
            .Where(sale => NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId))
            .GroupBy(sale => sale.TransactionId)
            .Select(group => group.First())
            .ToList();

        var before = new Dictionary<DateTime, (int Count, decimal Amount)>();
        var after = new Dictionary<DateTime, (int Count, decimal Amount)>();
        var excludedCount = 0;
        var excludedAmount = 0m;
        foreach (var sale in all)
        {
            var effective = repaired.GetValueOrDefault(sale.TransactionId, sale.StoredInstantUtc);
            if (InWindow(sale.StoredInstantUtc) && sale.StoredInstantUtc < request.CutoffUtc)
                Add(before, _calendar.ToBusinessDate(sale.StoredInstantUtc), sale.SettlementValue);
            if (!InWindow(effective))
                continue;
            if (effective >= request.CutoffUtc)
            {
                excludedCount++;
                excludedAmount += sale.SettlementValue;
                continue;
            }
            Add(after, _calendar.ToBusinessDate(effective), sale.SettlementValue);
        }

        // Unresolved completed sales do not move, so they sit on the same business day before and
        // after. Reporting them per day is what makes a day's figure comparable with a source
        // export that does not account for them.
        var unresolved = decisions
            .Where(decision => decision.Outcome == NayaxSaleTimestampRepairOutcome.Unresolved
                && NayaxTransactionStatusClassifier.IsCompletedSale(decision.Sale.TransactionStatusId)
                && InWindow(decision.Sale.StoredInstantUtc)
                && decision.Sale.StoredInstantUtc < request.CutoffUtc)
            .ToList();
        var unresolvedByDay = unresolved
            .GroupBy(decision => _calendar.ToBusinessDate(decision.Sale.StoredInstantUtc))
            .ToDictionary(
                group => group.Key,
                group => (Count: group.Count(), Amount: group.Sum(x => x.Sale.SettlementValue)));
        var missingInWindow = missing
            .Where(item => item.CompletedSale
                && item.AuthorizationInstantUtc is { } at
                && InWindow(at)
                && at < request.CutoffUtc)
            .ToList();

        var days = before.Keys.Concat(after.Keys)
            .Distinct()
            .OrderBy(date => date)
            .Select(date =>
            {
                var afterDay = after.GetValueOrDefault(date);
                var unresolvedDay = unresolvedByDay.GetValueOrDefault(date);
                return new NayaxSaleTimestampReconciliationDay(
                    date,
                    before.GetValueOrDefault(date).Count,
                    before.GetValueOrDefault(date).Amount,
                    afterDay.Count,
                    afterDay.Amount,
                    unresolvedDay.Count,
                    unresolvedDay.Amount,
                    afterDay.Count - unresolvedDay.Count,
                    afterDay.Amount - unresolvedDay.Amount);
            })
            .ToList();
        return new(
            request.CutoffUtc,
            request.FromBusinessDate,
            request.ToBusinessDate,
            days,
            days.Sum(day => day.CompletedCountBefore),
            days.Sum(day => day.CompletedSalesBefore),
            days.Sum(day => day.CompletedCountAfter),
            days.Sum(day => day.CompletedSalesAfter),
            days.Sum(day => day.SourceVerifiedCountAfter),
            days.Sum(day => day.SourceVerifiedAfter),
            excludedCount,
            excludedAmount,
            unresolved.Count,
            unresolved.Sum(decision => decision.Sale.SettlementValue),
            missingInWindow.Count,
            missingInWindow.Sum(item => item.SettlementValue));

        bool InWindow(DateTime instant) => instant >= fromUtc && instant < toUtc;

        static void Add(Dictionary<DateTime, (int Count, decimal Amount)> target, DateTime date, decimal amount)
        {
            var current = target.GetValueOrDefault(date);
            target[date] = (current.Count + 1, current.Amount + amount);
        }
    }
}
