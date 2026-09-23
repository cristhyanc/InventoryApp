using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.Profitability;

namespace Inventory.Application.Reporting.MachineProfitability;

/// <summary>
/// The machine profitability report use case: resolves the requested date range/machine scope,
/// retrieves report facts through the narrow <see cref="IMachineProfitabilityReportFactsProvider"/>
/// port, applies the shared Domain profit/margin/completeness policy per machine, and builds the
/// authoritative <see cref="MachineProfitabilityReportDto"/> consumed by both the API response and
/// the CSV/XLSX export.
/// </summary>
public sealed class GetMachineProfitabilityReport
{
    private readonly IMachineProfitabilityReportFactsProvider _facts;

    public GetMachineProfitabilityReport(IMachineProfitabilityReportFactsProvider facts)
    {
        _facts = facts;
    }

    public async Task<MachineProfitabilityReportDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        var range = ReportingRangeResolver.Resolve(filter.From, filter.To, filter.StartDate, filter.EndDate, filter.FinancialYear);
        var machineId = filter.MachineId ?? filter.MachineID;

        var facts = await _facts.GetFactsAsync(range.From, range.ToDate, machineId, cancellationToken);

        var rows = facts.Machines.Select(machine =>
        {
            var row = ProfitabilityRowPolicy.Calculate(new ProfitabilityRowInputs(
                machine.Sales, machine.PartialCostOfGoods, machine.IsCogsComplete));
            var direct = MachineDirectProfitPolicy.Calculate(new MachineDirectProfitInputs(
                Sales: machine.Sales,
                PartialCostOfGoods: machine.PartialCostOfGoods,
                IsCogsComplete: machine.IsCogsComplete,
                FeesIncludingGst: machine.ProcessingFees.TotalFeeIncGst,
                HasMissingFeeRates: machine.ProcessingFees.HasMissingRates,
                SiteCommission: machine.CommissionDue,
                CommissionComplete: machine.CommissionComplete,
                OperatingExpenses: machine.OperatingExpenses));

            return new MachineProfitabilityRowDto(machine.MachineId, machine.MachineName, machine.Sales,
                machine.Quantity, row.CostOfGoods, row.GrossProfit, row.MarginPercent, machine.TransactionCount,
                machine.CommissionDue, direct.DirectProfit, direct.DirectMarginPercent, machine.CommissionPercent,
                machine.CardSales, machine.CashSales)
            {
                PartialCostOfGoods = machine.PartialCostOfGoods,
                IsCogsComplete = machine.IsCogsComplete,
                UncostedTransactionCount = machine.UncostedTransactionCount,
                UncostedSalesAmount = machine.UncostedSalesAmount,
                DirectOperatingExpenses = machine.OperatingExpenses,
                NayaxProcessingFees = machine.ProcessingFees
            };
        }).ToList();

        var qualityNotes = new List<string>();
        if (facts.MissingFeeRateTransactionCount > 0)
            qualityNotes.Add($"{facts.MissingFeeRateTransactionCount} card transaction(s) have no effective Nayax processing fee rate; machine profit is provisional.");
        if (rows.Any(x => !x.IsCogsComplete))
            qualityNotes.Add("One or more machines have completed sales with no persisted COGS; profit is incomplete.");
        if (!facts.CommissionIsComplete)
            qualityNotes.Add("Commission configuration is incomplete; profit is unavailable.");
        qualityNotes.AddRange(facts.CommissionWarnings);

        var quality = ReportingQuality.Quality(
            missingStatus: true,
            historicalCostUnavailable: rows.Any(x => !x.IsCogsComplete),
            gstClassificationMissing: true,
            commissionNotPersisted: !facts.CommissionIsComplete,
            containsUnmappedProducts: false,
            notes: qualityNotes);

        return new MachineProfitabilityReportDto(range.From, range.ToDate, rows, quality);
    }
}
