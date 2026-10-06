using Inventory.Application.Reporting.Reconciliation;

namespace InventoryApi.Tests.Application.Reporting.Reconciliation;

/// <summary>
/// In-memory fake of the report facts port, so the reconciliation use case's orchestration and
/// quality-note assembly can be tested without EF Core, SQLite, or navigating EF entity graphs.
/// </summary>
public sealed class FakeReconciliationReportFactsProvider : IReconciliationReportFactsProvider
{
    private readonly ReconciliationReportFacts _facts;

    public FakeReconciliationReportFactsProvider(ReconciliationReportFacts facts) => _facts = facts;

    public (DateTime From, DateTime To, long? MachineId)? LastRequest { get; private set; }

    public Task<ReconciliationReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        LastRequest = (from, to, machineId);
        return Task.FromResult(_facts);
    }

    public static ReconciliationPeriodFacts Period(
        DateTime? from = null,
        DateTime? to = null,
        DateTime? payoutDate = null,
        bool hasImported = true,
        decimal totalVendingSales = 100m,
        decimal cardSales = 80m,
        decimal cashSales = 20m,
        int cardTransactionCount = 8,
        int totalTransactionCount = 10,
        int cashTransactionCount = 2,
        decimal reportedGross = 80m,
        int reportedCount = 8,
        decimal processingFeesExGst = 4m,
        decimal feeGst = 0.4m,
        decimal otherFees = 0m,
        bool hasGstClassification = true,
        bool warning = false,
        bool paymentDetailMissing = false,
        decimal actualNetReimbursement = 75.6m) => new(
        from ?? new DateTime(2025, 8, 1), to ?? new DateTime(2025, 8, 1), payoutDate, hasImported,
        totalVendingSales, cardSales, cashSales, cardTransactionCount, totalTransactionCount, cashTransactionCount,
        reportedGross, reportedCount, processingFeesExGst, feeGst, otherFees, hasGstClassification, warning,
        paymentDetailMissing, actualNetReimbursement);

    public static ReconciliationReportFacts SinglePeriod(
        ReconciliationPeriodFacts? period = null,
        bool hasMatchedReimbursement = true,
        int unknownPaymentTransactionCount = 0,
        int pendingTransactionCount = 0,
        int refundedTransactionCount = 0,
        int declinedOrCancelledTransactionCount = 0,
        int unknownStatusTransactionCount = 0,
        int nullStatusTransactionCount = 0,
        bool isMachineFiltered = false)
    {
        var resolvedPeriod = period ?? Period();
        return new ReconciliationReportFacts(
            new List<ReconciliationPeriodFacts> { resolvedPeriod },
            hasMatchedReimbursement,
            resolvedPeriod.TotalVendingSales, resolvedPeriod.CardSales, resolvedPeriod.CashSales,
            resolvedPeriod.TotalTransactionCount, resolvedPeriod.CardTransactionCount, resolvedPeriod.CashTransactionCount,
            unknownPaymentTransactionCount, pendingTransactionCount, refundedTransactionCount,
            declinedOrCancelledTransactionCount, unknownStatusTransactionCount, nullStatusTransactionCount,
            isMachineFiltered);
    }
}
