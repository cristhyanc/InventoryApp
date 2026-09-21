using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Shared;

namespace InventoryApi.Tests.Application.Reporting.Daily;

/// <summary>
/// In-memory fake of the report facts port, so the daily use case's orchestration and
/// quality-note assembly can be tested without EF Core, SQLite, or the Nayax fee service.
/// </summary>
public sealed class FakeDailyReportFactsProvider : IDailyReportFactsProvider
{
    private readonly DailyReportFacts _facts;

    public FakeDailyReportFactsProvider(DailyReportFacts facts) => _facts = facts;

    public (DateTime From, DateTime To, long? MachineId)? LastRequest { get; private set; }

    public Task<DailyReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        LastRequest = (from, to, machineId);
        return Task.FromResult(_facts);
    }

    public static DailyReportFacts SingleDay(
        DateTime? date = null,
        decimal grossSales = 100m,
        int transactionCount = 10,
        decimal partialCostOfGoods = 40m,
        bool isCogsComplete = true,
        decimal cardSales = 80m,
        decimal cashSales = 20m,
        int uncostedTransactionCount = 0,
        decimal uncostedSalesAmount = 0m,
        NayaxProcessingFeeResult processingFees = null,
        decimal importedReimbursement = 80m,
        decimal netReimbursement = 78m,
        bool hasImportedReimbursement = true,
        bool importedContainsRows = true,
        bool hasPeriodOnlyImportedData = false,
        bool hasDataQualityWarning = false,
        int completedTransactionCount = 10,
        int pendingTransactionCount = 0,
        int declinedOrCancelledTransactionCount = 0,
        int refundedTransactionCount = 0,
        int unknownStatusTransactionCount = 0,
        int nullStatusTransactionCount = 0,
        bool importedFeesMachineFilterLimited = false,
        bool hasAnyPeriodOnlyImportedData = false)
    {
        var fees = processingFees ?? new NayaxProcessingFeeResult(4m, 0.4m, 4.4m, 0m, 0m, 0m, 0, DateTime.UtcNow, null);
        var day = new DailyReportDayFacts(
            date ?? new DateTime(2025, 8, 1), grossSales, transactionCount, partialCostOfGoods, isCogsComplete,
            cardSales, cashSales, uncostedTransactionCount, uncostedSalesAmount, fees,
            importedReimbursement, netReimbursement, hasImportedReimbursement, hasPeriodOnlyImportedData,
            hasDataQualityWarning, completedTransactionCount, pendingTransactionCount,
            declinedOrCancelledTransactionCount, refundedTransactionCount, unknownStatusTransactionCount);
        var totals = new DailyReportTotalsFacts(
            partialCostOfGoods, isCogsComplete, fees, importedContainsRows, importedReimbursement, netReimbursement,
            completedTransactionCount, pendingTransactionCount, declinedOrCancelledTransactionCount,
            refundedTransactionCount, unknownStatusTransactionCount, nullStatusTransactionCount);
        return new DailyReportFacts(new List<DailyReportDayFacts> { day }, totals, importedContainsRows, true,
            importedFeesMachineFilterLimited, hasAnyPeriodOnlyImportedData);
    }
}
