using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;

namespace InventoryApi.Tests.Application.Reporting.Bookkeeping;

/// <summary>
/// In-memory fake of the report facts port, so the bookkeeping use case's orchestration and
/// quality-note assembly can be tested without EF Core, SQLite, or the Nayax/commission services.
/// </summary>
public sealed class FakeBookkeepingReportFactsProvider : IBookkeepingReportFactsProvider
{
    private readonly BookkeepingReportFacts _facts;

    public FakeBookkeepingReportFactsProvider(BookkeepingReportFacts facts) => _facts = facts;

    public (DateTime From, DateTime To, long? MachineId)? LastRequest { get; private set; }

    public Task<BookkeepingReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        LastRequest = (from, to, machineId);
        return Task.FromResult(_facts);
    }

    public static BookkeepingReportFacts Complete(decimal sales = 100m, decimal cost = 40m, decimal cardSales = 80m,
        int cardTransactions = 8, decimal cashSales = 20m, int cashTransactions = 2,
        int? transactionCount = null) => new(
        GrossSales: sales,
        CardSales: cardSales,
        CardTransactions: cardTransactions,
        CashSales: cashSales,
        CashTransactions: cashTransactions,
        UnknownTransactions: 0,
        // The completed sales these card and cash rows are, unless a test states another volume.
        TransactionCount: transactionCount ?? cardTransactions + cashTransactions,
        PartialCostOfGoods: cost,
        IsCogsComplete: true,
        UncostedTransactionCount: 0,
        UncostedSalesAmount: 0m,
        ReceiptDeliveryCost: 2m,
        ReceiptPackageCost: 1m,
        ImportedHasNetSettlement: true,
        ImportedNetSettlement: 74m,
        ImportedContainsRows: true,
        ImportedContainsGstClassification: true,
        ImportedMachineFilterMatched: true,
        ImportedFeesMachineFilterLimited: false,
        OperatingExpensesTotal: 1m,
        OperatingExpensesGst: 0.1m,
        OperatingExpensesByCategory: new Dictionary<string, decimal> { ["Utilities"] = 1m },
        SiteCommission: 3m,
        CommissionCompleteForScope: true,
        CommissionIsComplete: true,
        CommissionWarnings: [],
        ProcessingFees: new NayaxProcessingFeeResult(4m, 0.4m, 4.4m, 0m, 0m, 0m, 0, DateTime.UtcNow, null),
        PendingTransactionCount: 0,
        RefundedTransactionCount: 0,
        DeclinedOrCancelledTransactionCount: 0,
        UnknownStatusTransactionCount: 0,
        MissingStatusTransactionCount: 0)
        {
            // A complete scope is one every provider source accounted for end to end (issue #730): the
            // range is deliberately far wider than any period these tests ask for, so a test about
            // something else is never incidentally about coverage. The tests that are about coverage
            // replace these two lists.
            SalesCoverage = [FullCoverage],
            SettlementCoverage = [FullCoverage],
            ContainsSalesRows = true,
        };

    /// <summary>A range wider than every period these tests report on.</summary>
    internal static readonly ReportCoverageRange FullCoverage = new(
        new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc));
}
