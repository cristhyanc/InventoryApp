using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Gst;
using Inventory.Domain.Reporting.Gst;
using InventoryApi.Tests.Application.Reporting.Shared;

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
        int? transactionCount = null, IReadOnlyList<SalesByGstRule>? salesByProductGstRule = null) => new(
        GrossSales: sales,
        // Every sale is a taxable product's unless a test about issue #1255's GST-on-sales rule
        // says otherwise, which is what the whole period used to be assumed to be. The groups
        // always add up to GrossSales, as the real adapter's do.
        SalesByProductGstRule: salesByProductGstRule ?? [new SalesByGstRule(GstClassification.Taxable, sales)],
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
        // An imported payout covering these dates in full recorded this net settlement, so the
        // report has an actual reimbursement to compare (issue #1253). The payout *periods* behind
        // it are presentation detail and are left empty here: no figure is derived from either list
        // except whether a partly covered payout exists at all, and the tests about those lists set
        // them themselves.
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
            // A complete scope is one every provider source accounted for end to end (issue #730):
            // the decided period and its coverage are deliberately far wider than any period these
            // tests ask for, so a test about something else is never incidentally about coverage.
            // The tests that are about coverage replace this record.
            Coverage = TestReportCoverage.Complete(),
        };
}
