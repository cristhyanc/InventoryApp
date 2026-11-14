using Inventory.Application.Commissions;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IBookkeepingReportFactsProvider"/>. It lives in
/// Inventory.Infrastructure with the <see cref="AppDbContext"/> and persistence models it depends on
/// (issue #307 moved those, issue #308 this adapter family), and is registered by
/// <c>AddInfrastructureServices()</c>. It also composes the Application-owned
/// <see cref="IGetNayaxProcessingFees"/> and <see cref="IGetSiteCommissionReport"/> use cases.
///
/// Its completed-sale cost query, period-level imported-reimbursement summary, and site-commission
/// resolution are shared with <see cref="EfDailyReportFactsProvider"/>,
/// <see cref="EfReconciliationReportFactsProvider"/>, and
/// <see cref="EfMachineProfitabilityReportFactsProvider"/> through <see cref="EfReportingSharedQueries"/>.
/// Its remaining private EF query helpers (receipts, operating expenses) compute the business-wide
/// delivery/package cost and operating-expense totals bookkeeping's net-profit rule needs; audited
/// against every other migrated adapter (issue #92), they have no equivalent to share: machine
/// profitability's own operating-expense query is a per-machine breakdown with different grouping,
/// not the same shape (see the reporting migration track in docs/architecture.md).
/// </summary>
public sealed class EfBookkeepingReportFactsProvider : IBookkeepingReportFactsProvider
{
    private readonly AppDbContext _db;
    private readonly IGetNayaxProcessingFees _nayaxProcessingFees;
    private readonly IGetSiteCommissionReport _siteCommissions;
    private readonly IBusinessCalendar _businessCalendar;

    public EfBookkeepingReportFactsProvider(
        AppDbContext db, IGetNayaxProcessingFees nayaxProcessingFees, IGetSiteCommissionReport siteCommissions,
        IBusinessCalendar businessCalendar)
    {
        _db = db;
        _nayaxProcessingFees = nayaxProcessingFees;
        _siteCommissions = siteCommissions;
        _businessCalendar = businessCalendar;
    }

    public async Task<BookkeepingReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        // Two different kinds of boundary (issue #1250). Sales are selected by instant, over the
        // business days the requested dates name - business-day midnight at the start of the first
        // date to, exclusively, business-day midnight at the start of the day after the last, which
        // is 23, 24 or 25 hours per day. Reimbursement coverage, expense and purchase dates are
        // date-only values, so they keep plain calendar-date bounds and are never timezone-shifted.
        var window = EfReportingSharedQueries.SalesWindow(_businessCalendar, from, to);
        var endExclusive = to.Date.AddDays(1);
        var isMachineFiltered = machineId.HasValue;

        var paymentRows = await EfReportingSharedQueries.SalesQuery(_db, window, machineId)
            .Select(x => new { x.PaymentMethod, x.SettlementValue })
            .ToListAsync(cancellationToken);
        var classified = paymentRows.GroupBy(x => PaymentMethodClassifier.Classify(x.PaymentMethod))
            .ToDictionary(x => x.Key, x => new { Sales = x.Sum(y => y.SettlementValue), Count = x.Count() });
        var card = classified.GetValueOrDefault(NayaxPaymentType.Card);
        var cash = classified.GetValueOrDefault(NayaxPaymentType.Cash);
        var unknown = classified.GetValueOrDefault(NayaxPaymentType.Unknown);
        var grossSales = paymentRows.Sum(x => x.SettlementValue);

        // Read every status in the same business/date/machine scope before the completed-sale filter
        // removes them (issue #476), so the report can state what it excluded and why. The financial
        // queries above and below stay on the completed-sale predicate: this list only produces counts.
        var statusRows = await EfReportingSharedQueries.AllSalesQuery(_db, window, machineId)
            .Select(x => new { x.TransactionStatusId, x.SqsVerification })
            .ToListAsync(cancellationToken);
        var statuses = statusRows.ConvertAll(x => x.TransactionStatusId);

        var saleCosts = await EfReportingSharedQueries.CostQuery(_db, window, machineId).ToListAsync(cancellationToken);
        var partialCost = saleCosts.Sum(x => x.CostOfGoodsSold ?? 0m);
        var uncosted = saleCosts.Where(x => !x.HasCost).ToList();
        var isCogsComplete = uncosted.Count == 0;

        var (receiptDelivery, receiptPackage) = isMachineFiltered
            ? (0m, 0m)
            : await ReceiptCostsAsync(from, endExclusive, cancellationToken);

        var imported = await EfReportingSharedQueries.ImportedSummaryAsync(_db, from, endExclusive, machineId, cancellationToken);
        // The fees are charged to exactly the sales this report's revenue counts, and each one is
        // estimated at the rate effective on the business date that counted it: the fee use case is
        // asked for a business-day period over the same instants, not for the whole UTC dates those
        // instants fall on (issue #1250, on issue #310's contract).
        var processingFees = await _nayaxProcessingFees.HandleBusinessPeriod(window.FeePeriod, machineId, cancellationToken);
        var operatingExpenses = await OperatingExpenseSummaryAsync(from, endExclusive, machineId, cancellationToken);
        var commissions = await EfReportingSharedQueries.GetMachineCommissionsAsync(_db, _siteCommissions, window, machineId, cancellationToken);
        var siteCommission = await EfReportingSharedQueries.GetSiteCommissionAsync(_db, window, machineId, commissions, cancellationToken);

        var commissionCompleteForScope = isMachineFiltered
            ? commissions.Machines.GetValueOrDefault(machineId!.Value).IsComplete
            : commissions.IsComplete;

        // Every stored sale in scope, whatever its status, counted from the same list the status
        // diagnostics below use rather than with a second query. A period holding only cancelled
        // rows has sales data and a gross total of zero, so this must not be derived from an
        // amount (issue #730). Measured over the same business-day instants the sales above were
        // selected between, never the plain calendar range (issue #1250): coverage may only be
        // compared with the window the figures were measured over, and the carrier is what takes
        // those instants to the use case that decides the two states.
        var coverage = await EfReportingSharedQueries.CoverageFactsAsync(
            _db, window, statuses.Count > 0, imported.ContainsRows, cancellationToken);

        return new BookkeepingReportFacts(
            GrossSales: grossSales,
            CardSales: card?.Sales ?? 0m,
            CardTransactions: card?.Count ?? 0,
            CashSales: cash?.Sales ?? 0m,
            CashTransactions: cash?.Count ?? 0,
            UnknownTransactions: unknown?.Count ?? 0,
            // The completed sales every amount here is summed from, counted from the rows already
            // read above rather than with a second query (issue #804). It is not the card and cash
            // counts added together: a completed sale whose payment method classifies as neither
            // is still one of these rows and still in GrossSales.
            TransactionCount: paymentRows.Count,
            PartialCostOfGoods: partialCost,
            IsCogsComplete: isCogsComplete,
            UncostedTransactionCount: uncosted.Count,
            UncostedSalesAmount: uncosted.Sum(x => x.SettlementValue),
            ReceiptDeliveryCost: receiptDelivery,
            ReceiptPackageCost: receiptPackage,
            ImportedHasNetSettlement: imported.HasNetSettlement,
            ImportedNetSettlement: imported.NetSettlement,
            ImportedContainsRows: imported.ContainsRows,
            ImportedContainsGstClassification: imported.ContainsGstClassification,
            ImportedMachineFilterMatched: imported.MachineFilterMatched,
            ImportedFeesMachineFilterLimited: imported.FeesMachineFilterLimited,
            OperatingExpensesTotal: operatingExpenses.Total,
            OperatingExpensesGst: operatingExpenses.Gst,
            OperatingExpensesByCategory: operatingExpenses.ByCategory,
            SiteCommission: siteCommission,
            CommissionCompleteForScope: commissionCompleteForScope,
            CommissionIsComplete: commissions.IsComplete,
            CommissionWarnings: commissions.Warnings,
            ProcessingFees: processingFees,
            PendingTransactionCount: statuses.Count(x => NayaxTransactionStatusClassifier.Classify(x) == NayaxTransactionStatus.Pending),
            RefundedTransactionCount: statuses.Count(x => NayaxTransactionStatusClassifier.Classify(x) == NayaxTransactionStatus.Refunded),
            DeclinedOrCancelledTransactionCount: statuses.Count(x => NayaxTransactionStatusClassifier.Classify(x) == NayaxTransactionStatus.CancelledOrDeclined),
            UnknownStatusTransactionCount: statuses.Count(x => x is not null && NayaxTransactionStatusClassifier.Classify(x) == NayaxTransactionStatus.Unknown),
            MissingStatusTransactionCount: statuses.Count(x => x is null))
        {
            Coverage = coverage,
            ExcludedByReviewTransactionCount = statusRows.Count(x =>
                NayaxSaleCountingPolicy.Classify(x.TransactionStatusId, x.SqsVerification) == NayaxTransactionStatus.ExcludedByReview),
        };
    }

    private async Task<(decimal Delivery, decimal Package)> ReceiptCostsAsync(DateTime from, DateTime endExclusive, CancellationToken cancellationToken)
    {
        var costs = await _db.Receipts.AsNoTracking()
            .Where(x => x.PurchaseDate >= from && x.PurchaseDate < endExclusive)
            .GroupBy(_ => 1)
            .Select(g => new { Delivery = g.Sum(x => x.DeliveryCost ?? 0m), Package = g.Sum(x => x.PackageCost ?? 0m) })
            .SingleOrDefaultAsync(cancellationToken);
        return (costs?.Delivery ?? 0m, costs?.Package ?? 0m);
    }

    /// <summary>
    /// The period's operating expenses, optionally narrowed to one machine.
    /// <paramref name="machineId"/> is the <em>provider's</em> machine number, because that is what
    /// this report's filter speaks and issue #780 deliberately left it unchanged; the expenses
    /// themselves record the local machine since that issue, so the number is resolved to a local
    /// machine id through the one lookup (<see cref="EfMachineLinkQueries"/>) first.
    ///
    /// <para>A provider machine this business has recorded no machine for narrows the report to
    /// <strong>no</strong> expenses - never silently to the whole business's, which would report
    /// another machine's costs against the one that was asked about.</para>
    /// </summary>
    private async Task<OperatingExpenseSummary> OperatingExpenseSummaryAsync(DateTime from, DateTime endExclusive, long? machineId, CancellationToken cancellationToken)
    {
        long? localMachineId = null;
        if (machineId.HasValue)
        {
            var localMachineIds = await EfMachineLinkQueries.LocalMachineIdsByNayaxIdAsync(
                _db, [machineId.Value], cancellationToken);
            if (!localMachineIds.TryGetValue(machineId.Value, out var resolved))
                return new OperatingExpenseSummary(0m, 0m, new Dictionary<string, decimal>());
            localMachineId = resolved;
        }

        var rows = await _db.OperatingExpenses.AsNoTracking()
            .Where(x => x.ExpenseDate >= from && x.ExpenseDate < endExclusive &&
                (!localMachineId.HasValue || x.MachineId == localMachineId.Value))
            .Select(x => new { x.Category, x.TotalAmount, x.GstAmount })
            .ToListAsync(cancellationToken);
        return new OperatingExpenseSummary(rows.Sum(x => x.TotalAmount), rows.Sum(x => x.GstAmount),
            rows.GroupBy(x => x.Category.ToString()).ToDictionary(g => g.Key, g => g.Sum(x => x.TotalAmount)));
    }

    private sealed record OperatingExpenseSummary(decimal Total, decimal Gst, IReadOnlyDictionary<string, decimal> ByCategory);
}
