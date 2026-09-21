using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.Daily;

namespace Inventory.Application.Reporting.Daily;

/// <summary>
/// The daily report use case: resolves the requested date range/machine scope, retrieves report
/// facts through the narrow <see cref="IDailyReportFactsProvider"/> port, applies the Domain
/// per-day profit/margin/reconciliation-status policy, and builds the authoritative
/// <see cref="DailyReportDto"/> consumed by both the API response and the CSV/XLSX export.
/// </summary>
public sealed class GetDailyReport
{
    private const decimal ReconciliationTolerance = 0.01m;

    private readonly IDailyReportFactsProvider _facts;

    public GetDailyReport(IDailyReportFactsProvider facts) => _facts = facts;

    public async Task<DailyReportDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        var range = ReportingRangeResolver.Resolve(filter.From, filter.To, filter.StartDate, filter.EndDate, filter.FinancialYear);
        var machineId = filter.MachineId ?? filter.MachineID;

        var facts = await _facts.GetFactsAsync(range.From, range.ToDate, machineId, cancellationToken);

        var rows = facts.Days.Select(BuildRow).ToList();

        var totalsFacts = facts.Totals;
        var totalGrossSales = rows.Sum(x => x.GrossSales);
        var totalTransactionCount = rows.Sum(x => x.TransactionCount);
        var totals = new DailyReportTotalsDto(
            totalGrossSales, rows.Sum(x => x.CardSales), rows.Sum(x => x.CashSales),
            rows.Sum(x => x.Quantity), totalsFacts.IsCogsComplete ? totalsFacts.PartialCostOfGoods : null,
            totalsFacts.IsCogsComplete ? ReportingCalculations.GrossProfit(totalGrossSales, totalsFacts.PartialCostOfGoods) : null,
            totalTransactionCount, ReportingCalculations.Average(totalGrossSales, totalTransactionCount),
            totalsFacts.IsCogsComplete, rows.Sum(x => x.UncostedTransactionCount), rows.Sum(x => x.UncostedSalesAmount),
            totalsFacts.IsCogsComplete ? ReportingCalculations.MarginPercent(totalGrossSales, totalsFacts.PartialCostOfGoods) : null,
            totalsFacts.ProcessingFees.TotalFeeExGst, totalsFacts.ProcessingFees.TotalFeeIncGst,
            totalsFacts.ImportedContainsRows ? totalsFacts.ImportedReimbursement : rows.Sum(x => x.ImportedReimbursement),
            totalsFacts.ImportedContainsRows ? totalsFacts.ImportedNetReimbursement : rows.Sum(x => x.NetReimbursement),
            totalsFacts.CompletedTransactionCount, totalsFacts.PendingTransactionCount,
            totalsFacts.DeclinedOrCancelledTransactionCount, totalsFacts.RefundedTransactionCount,
            totalsFacts.UnknownStatusTransactionCount)
        {
            PartialCostOfGoods = totalsFacts.PartialCostOfGoods,
            NayaxProcessingFees = totalsFacts.ProcessingFees
        };

        var note = facts.ImportedFeesMachineFilterLimited
            ? "Imported fees are account-level amounts and are not allocated to a selected machine."
            : facts.HasAnyPeriodOnlyImportedData
                ? "Some reimbursements cover a period longer than one day and are not allocated to daily rows."
                : null;
        var qualityNotes = new List<string>();
        if (note is not null) qualityNotes.Add(note);
        if (totalsFacts.ProcessingFees.HasMissingRates)
            qualityNotes.Add($"{totalsFacts.ProcessingFees.MissingRateTransactionCount} card transaction(s) have no effective Nayax processing fee rate; fee totals are provisional.");
        if (!totals.IsCogsComplete)
            qualityNotes.Add("One or more completed sales have no persisted COGS; profit is incomplete.");
        AddStatusQualityNotes(qualityNotes, totalsFacts);

        var quality = ReportingQuality.Quality(facts.ImportedContainsRows, facts.ImportedContainsGstClassification, false,
            qualityNotes.Count == 0 ? null : string.Join(" ", qualityNotes));

        return new DailyReportDto(range.From, range.ToDate, rows, quality, totals);
    }

    private static DailyReportRowDto BuildRow(DailyReportDayFacts day)
    {
        var result = DailyRowPolicy.Calculate(new DailyRowInputs(
            GrossSales: day.GrossSales,
            TransactionCount: day.TransactionCount,
            PartialCostOfGoods: day.PartialCostOfGoods,
            IsCogsComplete: day.IsCogsComplete,
            CardSales: day.CardSales,
            ImportedReimbursement: day.ImportedReimbursement,
            HasImportedReimbursement: day.HasImportedReimbursement,
            HasPeriodOnlyImportedData: day.HasPeriodOnlyImportedData,
            HasDataQualityWarning: day.HasDataQualityWarning,
            Tolerance: ReconciliationTolerance));

        var feeSource = day.ProcessingFees.HasEstimatedFees
            ? (day.ProcessingFees.ActualFeeExGst != 0m ? "Actual and Estimated" : "Estimated")
            : day.ProcessingFees.ActualFeeExGst != 0m ? "Actual" : "None";

        return new DailyReportRowDto(
            day.Date, day.GrossSales, day.TransactionCount, result.CostOfGoods, result.GrossProfit, day.TransactionCount,
            day.GrossSales, day.CardSales, day.CashSales, result.AverageSale,
            day.UncostedTransactionCount == 0, day.UncostedTransactionCount, day.UncostedSalesAmount,
            result.GrossMarginPercent, day.ProcessingFees.TotalFeeExGst, day.ProcessingFees.TotalFeeIncGst,
            day.ImportedReimbursement, day.NetReimbursement, result.IsReconciled, result.ReconciliationStatus,
            day.CompletedTransactionCount, day.PendingTransactionCount, day.DeclinedOrCancelledTransactionCount,
            day.RefundedTransactionCount, day.UnknownStatusTransactionCount, feeSource)
        {
            PartialCostOfGoods = day.PartialCostOfGoods
        };
    }

    private static void AddStatusQualityNotes(List<string> notes, DailyReportTotalsFacts totals)
    {
        if (totals.PendingTransactionCount > 0)
            notes.Add($"{totals.PendingTransactionCount} pending Nayax transaction(s) are excluded from completed sales.");
        if (totals.RefundedTransactionCount > 0)
            notes.Add($"{totals.RefundedTransactionCount} refunded Nayax transaction(s) are excluded from completed sales.");
        if (totals.DeclinedOrCancelledTransactionCount > 0)
            notes.Add($"{totals.DeclinedOrCancelledTransactionCount} cancelled or declined Nayax transaction(s) are excluded from completed sales.");
        if (totals.UnknownStatusTransactionCount > 0)
            notes.Add($"{totals.UnknownStatusTransactionCount} Nayax transaction(s) have unrecognised status IDs.");
        if (totals.NullStatusTransactionCount > 0)
            notes.Add($"{totals.NullStatusTransactionCount} Nayax transaction(s) have no status ID and are excluded from completed sales.");
    }
}
