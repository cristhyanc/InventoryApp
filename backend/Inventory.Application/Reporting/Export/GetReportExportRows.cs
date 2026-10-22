using System.Globalization;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;

namespace Inventory.Application.Reporting.Export;

/// <summary>
/// Builds export rows for every report family from the same authoritative use-case results the API
/// responses use (each is already documented as "consumed by both the API response and the
/// CSV/XLSX export"), so export never reimplements a financial formula. This is the use case the
/// removed legacy reporting service's export row-building delegated to before that service was
/// decommissioned (issue #92).
/// </summary>
public sealed class GetReportExportRows
{
    private readonly GetBookkeepingReport _getBookkeepingReport;
    private readonly GetDailyReport _getDailyReport;
    private readonly GetReconciliationReport _getReconciliationReport;
    private readonly GetMachineProfitabilityReport _getMachineProfitabilityReport;
    private readonly GetProductProfitabilityReport _getProductProfitabilityReport;
    private readonly GetGstAccountingAid _getGstAccountingAid;
    private readonly GetDashboardReport _getDashboardReport;
    private readonly GetTransactionSalesReport _getTransactionSalesReport;

    public GetReportExportRows(GetBookkeepingReport getBookkeepingReport, GetDailyReport getDailyReport,
        GetReconciliationReport getReconciliationReport, GetMachineProfitabilityReport getMachineProfitabilityReport,
        GetProductProfitabilityReport getProductProfitabilityReport, GetGstAccountingAid getGstAccountingAid,
        GetDashboardReport getDashboardReport, GetTransactionSalesReport getTransactionSalesReport)
    {
        _getBookkeepingReport = getBookkeepingReport;
        _getDailyReport = getDailyReport;
        _getReconciliationReport = getReconciliationReport;
        _getMachineProfitabilityReport = getMachineProfitabilityReport;
        _getProductProfitabilityReport = getProductProfitabilityReport;
        _getGstAccountingAid = getGstAccountingAid;
        _getDashboardReport = getDashboardReport;
        _getTransactionSalesReport = getTransactionSalesReport;
    }

    /// <summary>
    /// What the bookkeeping report's GST-on-sales figure actually is (issue #476). GST
    /// classification is not persisted per sale, so this figure stays the indicative inclusive
    /// calculation and the export says so in its own column: moving the on-screen explanation into
    /// the report's calculation help must never leave a downloaded file implying a verified figure.
    /// It is a label for one figure, not a blanket statement about GST on fees or expenses, which
    /// come from imported and recorded amounts.
    /// </summary>
    private const string BookkeepingGstOnSalesBasis =
        "Estimated GST on sales - assumes all included sales are taxable at 10% (GST-inclusive).";

    // The availability column headers and date format repeat across every report's row shape
    // below, so they are named once here rather than retyped per report (issue #731).
    private const string DateFormat = "yyyy-MM-dd";
    private const string SalesDataAvailabilityHeader = "SalesDataAvailability";
    private const string SalesDataAsOfUtcHeader = "SalesDataAsOfUtc";
    private const string FeeDataAvailabilityHeader = "FeeDataAvailability";
    private const string FeeDataAsOfUtcHeader = "FeeDataAsOfUtc";
    private const string CombinedDataAvailabilityHeader = "CombinedDataAvailability";

    private static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public async Task<ReportExportTable> Handle(string report, TransactionSalesFilterDto filter, CancellationToken cancellationToken)
    {
        if (report.Trim().ToLowerInvariant() is not ("transactions" or "transaction-sales"))
            throw new ArgumentException("Unsupported transaction report.", nameof(report));

        var value = await _getTransactionSalesReport.Handle(filter, paginate: false, cancellationToken);
        var rows = new List<List<string>>
        {
            new()
            {
                "TransactionDate", "TransactionId", "MachineId", "Machine", "SiteId", "Site",
                "ProductId", "Product", "PaymentType", "RawPaymentMethod", "Sale", "UnitCostAtSale",
                "NayaxProductCostPrice", "CostOfGoods", "CostingStatus", "CostSource",
                "GrossProfit", "GrossMarginPercent", "DirectProfit",
                "DirectMarginPercent", "FeeExGst", "FeeGST", "FeeIncGST", "FeeSource",
                "CommissionRate", "CommissionBasis", "CommissionAmount", "TransactionStatusId",
                "TransactionStatus", "IsCompleted", SalesDataAvailabilityHeader, SalesDataAsOfUtcHeader
            }
        };
        // Every row here is one stored transaction, so no amount on it is a zero nothing measured -
        // the row's existence is the measurement. What the file cannot otherwise say is whether
        // these are *all* the transactions of the period, which is exactly what the two state
        // columns report (issue #731). They are repeated per row because a downloaded row is read
        // on its own, and they carry only the sales state: this report has no settlement figure.
        var availability = value.SalesAvailability;
        rows.AddRange(value.Rows.Select(x => new List<string>
        {
            x.TransactionDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            x.TransactionId.ToString(CultureInfo.InvariantCulture), x.MachineId.ToString(CultureInfo.InvariantCulture),
            x.MachineName, Number(x.SiteId), x.SiteName ?? string.Empty, Number(x.ProductId), x.ProductName,
            x.PaymentType, x.RawPaymentMethod ?? string.Empty, Number(x.Sale), Number(x.UnitCostAtSale),
            Number(x.NayaxProductCostPrice), Number(x.CostOfGoods), x.CostingStatus, x.CostSource,
            Number(x.GrossProfit), Number(x.GrossMarginPercent),
            Number(x.DirectProfit), Number(x.DirectMarginPercent), Number(x.FeeExGst), Number(x.FeeGst),
            Number(x.FeeIncGst), x.FeeSource, Number(x.CommissionRate), x.CommissionBasis ?? string.Empty,
            Number(x.CommissionAmount), Number(x.TransactionStatusId), x.TransactionStatus, x.IsCompleted.ToString(),
            AvailabilityExportCells.State(availability), AvailabilityExportCells.AsOfUtc(availability)
        }));
        // With no transaction at all there is no data row to carry those columns, and a download of
        // headings alone cannot say whether the period was unaccounted for or genuinely sold
        // nothing. One marked metadata row states it instead, fabricating no transaction.
        if (value.Rows.Count == 0)
            rows.Add(AvailabilityExportCells.NoRowsRow(rows[0],
                AvailabilityExportCells.State(availability), AvailabilityExportCells.AsOfUtc(availability)));
        return new ReportExportTable(rows, "Transactions");
    }

    public async Task<ReportExportTable> Handle(string report, ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        report = report.Trim().ToLowerInvariant();
        if (report is "daily")
        {
            var value = await _getDailyReport.Handle(filter, cancellationToken);
            // Every provider-sourced figure is spelled with the availability of the data that feeds
            // it, and the state columns report every family in full (issue #731, the same rule
            // issue #730 wrote for bookkeeping - see AvailabilityExportCells). The state is the
            // period's, so it is repeated on every row: a downloaded row is read on its own, where
            // nothing else can say whether its zero was measured.
            var sales = value.ProviderAvailability.Sales;
            var fees = value.ProviderAvailability.Fees;
            var combined = value.ProviderAvailability.Combined;
            List<string> AvailabilityCells() =>
            [
                AvailabilityExportCells.State(sales), AvailabilityExportCells.AsOfUtc(sales),
                AvailabilityExportCells.State(fees), AvailabilityExportCells.AsOfUtc(fees),
                AvailabilityExportCells.State(combined)
            ];
            var rows = new[] { new List<string>
                {
                    "Date", "GrossSales", "CardSales", "CashSales", "AverageSale", "Quantity",
                    "CostOfGoods", "GrossProfit", "GrossMarginPercent", "Transactions",
                    "IsCogsComplete", "UncostedTransactionCount", "UncostedSalesAmount",
                    "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "FeeSource", "ImportedReimbursement",
                    "NetReimbursement", "ReconciliationStatus",
                    SalesDataAvailabilityHeader, SalesDataAsOfUtcHeader, FeeDataAvailabilityHeader, FeeDataAsOfUtcHeader,
                    CombinedDataAvailabilityHeader
                } }
                .Concat(value.Rows.Select(x => new List<string>
                {
                    x.Date.ToString(DateFormat), AvailabilityExportCells.Amount(x.GrossSales, sales),
                    AvailabilityExportCells.Amount(x.CardSales, sales), AvailabilityExportCells.Amount(x.CashSales, sales),
                    AvailabilityExportCells.Amount(x.AverageSale, sales), AvailabilityExportCells.Amount(x.Quantity, sales),
                    AvailabilityExportCells.Amount(x.CostOfGoods, sales), AvailabilityExportCells.Amount(x.GrossProfit, sales),
                    AvailabilityExportCells.Amount(x.GrossMarginPercent, sales), AvailabilityExportCells.Amount(x.TransactionCount, sales),
                    x.IsCogsComplete.ToString(), x.UncostedTransactionCount.ToString(),
                    x.UncostedSalesAmount.ToString(CultureInfo.InvariantCulture),
                    AvailabilityExportCells.Amount(x.NayaxFeesExGst, fees), AvailabilityExportCells.Amount(x.NayaxFeesGst, fees),
                    AvailabilityExportCells.Amount(x.NayaxFeesIncludingGst, fees), x.NayaxFeeSource,
                    AvailabilityExportCells.Amount(x.ImportedReimbursement, fees),
                    AvailabilityExportCells.Amount(x.NetReimbursement, fees),
                    x.ReconciliationStatus
                }.Concat(AvailabilityCells()).ToList())).ToList();
            if (value.Totals is not null)
                rows.Add(new List<string>
                {
                    "TOTAL", AvailabilityExportCells.Amount(value.Totals.GrossSales, sales),
                    AvailabilityExportCells.Amount(value.Totals.CardSales, sales), AvailabilityExportCells.Amount(value.Totals.CashSales, sales),
                    AvailabilityExportCells.Amount(value.Totals.AverageSale, sales), AvailabilityExportCells.Amount(value.Totals.Quantity, sales),
                    AvailabilityExportCells.Amount(value.Totals.CostOfGoods, sales), AvailabilityExportCells.Amount(value.Totals.GrossProfit, sales),
                    AvailabilityExportCells.Amount(value.Totals.GrossMarginPercent, sales), AvailabilityExportCells.Amount(value.Totals.TransactionCount, sales),
                    value.Totals.IsCogsComplete.ToString(), value.Totals.UncostedTransactionCount.ToString(),
                    value.Totals.UncostedSalesAmount.ToString(CultureInfo.InvariantCulture),
                    AvailabilityExportCells.Amount(value.Totals.NayaxFeesExGst, fees),
                    AvailabilityExportCells.Amount(value.Totals.NayaxProcessingFees.TotalFeeGst, fees),
                    AvailabilityExportCells.Amount(value.Totals.NayaxFeesIncludingGst, fees),
                    string.Empty,
                    AvailabilityExportCells.Amount(value.Totals.ImportedReimbursement, fees),
                    AvailabilityExportCells.Amount(value.Totals.NetReimbursement, fees),
                    string.Empty
                }.Concat(AvailabilityCells()).ToList());
            return new ReportExportTable(rows, "Report");
        }
        if (report is "bookkeeping")
        {
            var value = await _getBookkeepingReport.Handle(filter, cancellationToken);
            var isMachineFiltered = MachineId(filter).HasValue;
            // Each provider-sourced figure is spelled with the availability of the data that feeds
            // it, and the three state columns report every family in full (issue #730, see
            // AvailabilityExportCells). Delivery, package and operating-expense figures are this
            // business's own records and stay exact; the status counts stay plain counts, as they are
            // statements about the stored rows themselves and the report's own UI shows them so.
            var sales = value.ProviderAvailability.Sales;
            var fees = value.ProviderAvailability.Fees;
            var combined = value.ProviderAvailability.Combined;
            var rows = new List<List<string>>
            {
                new() { "From", "To", "FinancialYear", "GrossSales", "CardSales", "CashSales", "Transactions", "AverageSale", "CardTransactions", "CashTransactions", "COGS", "GrossProfit",                 "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "ActualNayaxFee", "EstimatedNayaxFee", "EstimatedCardTransactionCount", "HasEstimates", "DeliveryCosts", "PackageCosts", "OtherOperatingExpenses", "NetSettlement", "SiteCommission", isMachineFiltered ? "DirectProfit" : "NetProfit", isMachineFiltered ? "DirectMarginPercent" : "NetMargin", "GstOnSales", "GstOnFees", "GstOnSalesBasis", "IsCogsComplete", "UncostedTransactionCount", "UncostedSalesAmount", "PendingTransactionCount", "RefundedTransactionCount", "DeclinedOrCancelledTransactionCount", "UnknownStatusTransactionCount", "MissingStatusTransactionCount", SalesDataAvailabilityHeader, SalesDataAsOfUtcHeader, FeeDataAvailabilityHeader, FeeDataAsOfUtcHeader, CombinedDataAvailabilityHeader, "DataQualityNotes" },
                new() { value.From.ToString(DateFormat), value.To.ToString(DateFormat), value.FinancialYear, AvailabilityExportCells.Amount(value.Sales, sales), AvailabilityExportCells.Amount(value.CardSales, sales), AvailabilityExportCells.Amount(value.CashSales, sales), AvailabilityExportCells.Amount(value.TransactionCount, sales), AvailabilityExportCells.Amount(value.AverageSale, sales), AvailabilityExportCells.Amount(value.CardTransactionCount, sales), AvailabilityExportCells.Amount(value.CashTransactionCount, sales), AvailabilityExportCells.Amount(value.CostOfGoods, sales), AvailabilityExportCells.Amount(value.GrossProfit, sales), AvailabilityExportCells.Amount(value.NayaxProcessingFees.TotalFeeExGst, fees), AvailabilityExportCells.Amount(value.NayaxProcessingFees.TotalFeeGst, fees), AvailabilityExportCells.Amount(value.NayaxProcessingFees.TotalFeeIncGst, fees), AvailabilityExportCells.Amount(value.NayaxProcessingFees.ActualFeeIncGst, fees), AvailabilityExportCells.Amount(value.NayaxProcessingFees.EstimatedFeeIncGst, combined), AvailabilityExportCells.Amount(value.NayaxProcessingFees.EstimatedCardTransactionCount, combined), value.NayaxProcessingFees.HasEstimatedFees.ToString(), value.DeliveryCosts.ToString(CultureInfo.InvariantCulture), value.PackageCosts.ToString(CultureInfo.InvariantCulture), value.OtherOperatingExpenses.ToString(CultureInfo.InvariantCulture), AvailabilityExportCells.Amount(value.NetSettlement, combined), AvailabilityExportCells.Amount(value.SiteCommission, sales), AvailabilityExportCells.Amount(isMachineFiltered ? value.DirectProfit : value.NetProfit, combined), AvailabilityExportCells.Amount(isMachineFiltered ? value.DirectMarginPercent : value.NetMarginPercent, combined), AvailabilityExportCells.Amount(value.GstOnSales, sales), AvailabilityExportCells.Amount(value.GstOnFees, fees), BookkeepingGstOnSalesBasis, value.IsCogsComplete.ToString(), value.UncostedTransactionCount.ToString(CultureInfo.InvariantCulture), value.UncostedSalesAmount.ToString(CultureInfo.InvariantCulture), value.PendingTransactionCount.ToString(CultureInfo.InvariantCulture), value.RefundedTransactionCount.ToString(CultureInfo.InvariantCulture), value.DeclinedOrCancelledTransactionCount.ToString(CultureInfo.InvariantCulture), value.UnknownStatusTransactionCount.ToString(CultureInfo.InvariantCulture), value.MissingStatusTransactionCount.ToString(CultureInfo.InvariantCulture), AvailabilityExportCells.State(sales), AvailabilityExportCells.AsOfUtc(sales), AvailabilityExportCells.State(fees), AvailabilityExportCells.AsOfUtc(fees), AvailabilityExportCells.State(combined), string.Join(" ", value.DataQuality.Notes ?? []) }
            };
            return new ReportExportTable(rows, "Report");
        }
        if (report is "reconciliation")
        {
            var value = await _getReconciliationReport.Handle(filter, 0.01m, cancellationToken);
            // The recorded side carries the sales state, the imported side the settlement state,
            // and every difference between them the combined one (issue #731): a difference of
            // 0.00 between two sides that were never both measured is the most misleading zero in
            // this report. The assumed-zero Adjustments is not provider data and stays exact, with
            // its own AdjustmentsBasis column explaining it.
            var sales = value.ProviderAvailability.Sales;
            var settlement = value.ProviderAvailability.Fees;
            var combined = value.ProviderAvailability.Combined;
            List<string> AvailabilityCells() =>
            [
                AvailabilityExportCells.State(sales), AvailabilityExportCells.AsOfUtc(sales),
                AvailabilityExportCells.State(settlement), AvailabilityExportCells.AsOfUtc(settlement),
                AvailabilityExportCells.State(combined)
            ];
            var rows = new List<List<string>>
            {
                new() { "From", "To", "TotalVendingSales", "CardSales", "CashSales", "CardTransactionSales", "NayaxReportedGrossCardSales", "CardTransactionCount", "NayaxReportedCardTransactionCount", "CountDifference", "GrossDifference", "GrossStatus", "ProcessingFeesExGst", "FeeGst", "OtherFees", "Adjustments", "AdjustmentsSupported", "AdjustmentsBasis", "ExpectedNetReimbursement", "ActualNetReimbursement", "SettlementDifference", "SettlementStatus", "Status", "PayoutDate", "PendingTransactionCount", "RefundedTransactionCount", "DeclinedOrCancelledTransactionCount", "UnknownStatusTransactionCount", "MissingStatusTransactionCount", SalesDataAvailabilityHeader, SalesDataAsOfUtcHeader, "SettlementDataAvailability", "SettlementDataAsOfUtc", CombinedDataAvailabilityHeader, "DataQualityNotes" }
            };
            rows.AddRange(value.PeriodRows.Select(x => new List<string>
            {
                x.From.ToString(DateFormat), x.To.ToString(DateFormat),
                AvailabilityExportCells.Amount(x.TotalVendingSales, sales), AvailabilityExportCells.Amount(x.CardSales, sales),
                AvailabilityExportCells.Amount(x.CashSales, sales), AvailabilityExportCells.Amount(x.CardTransactionSales, sales),
                AvailabilityExportCells.Amount(x.NayaxReportedGrossCardSales, settlement), AvailabilityExportCells.Amount(x.CardTransactionCount, sales),
                AvailabilityExportCells.Amount(x.NayaxReportedCardTransactionCount, settlement), AvailabilityExportCells.Amount(x.CountDifference, combined),
                AvailabilityExportCells.Amount(x.GrossDifference, combined), x.GrossStatus,
                AvailabilityExportCells.Amount(x.ProcessingFeesExGst, settlement), AvailabilityExportCells.Amount(x.FeeGst, settlement),
                AvailabilityExportCells.Amount(x.OtherFees, settlement), x.Adjustments.ToString(CultureInfo.InvariantCulture),
                x.AdjustmentsSupported.ToString(), GetReconciliationReport.AdjustmentsAssumption,
                AvailabilityExportCells.Amount(x.ExpectedNetReimbursement, combined), AvailabilityExportCells.Amount(x.ActualNetReimbursement, settlement),
                AvailabilityExportCells.Amount(x.SettlementDifference, combined), x.SettlementStatus, x.Status,
                x.PayoutDate?.ToString(DateFormat) ?? string.Empty,
                x.PendingTransactionCount.ToString(CultureInfo.InvariantCulture),
                x.RefundedTransactionCount.ToString(CultureInfo.InvariantCulture),
                x.DeclinedOrCancelledTransactionCount.ToString(CultureInfo.InvariantCulture),
                x.UnknownStatusTransactionCount.ToString(CultureInfo.InvariantCulture),
                x.MissingStatusTransactionCount.ToString(CultureInfo.InvariantCulture)
            }.Concat(AvailabilityCells()).Append(string.Join(" ", x.DataQuality.Notes ?? [])).ToList()));
            if (value.Totals is not null)
            {
                var x = value.Totals;
                rows.Add(new List<string>
                {
                    "TOTAL", string.Empty, AvailabilityExportCells.Amount(x.TotalVendingSales, sales),
                    AvailabilityExportCells.Amount(x.CardSales, sales), AvailabilityExportCells.Amount(x.CashSales, sales),
                    AvailabilityExportCells.Amount(x.CardTransactionSales, sales), AvailabilityExportCells.Amount(x.NayaxReportedGrossCardSales, settlement),
                    AvailabilityExportCells.Amount(x.CardTransactionCount, sales), AvailabilityExportCells.Amount(x.NayaxReportedCardTransactionCount, settlement),
                    AvailabilityExportCells.Amount(x.CountDifference, combined),
                    AvailabilityExportCells.Amount(x.GrossDifference, combined), x.GrossStatus,
                    AvailabilityExportCells.Amount(x.ProcessingFeesExGst, settlement), AvailabilityExportCells.Amount(x.FeeGst, settlement),
                    AvailabilityExportCells.Amount(x.OtherFees, settlement), x.Adjustments.ToString(CultureInfo.InvariantCulture),
                    x.AdjustmentsSupported.ToString(), GetReconciliationReport.AdjustmentsAssumption,
                    AvailabilityExportCells.Amount(x.ExpectedNetReimbursement, combined), AvailabilityExportCells.Amount(x.ActualNetReimbursement, settlement),
                    AvailabilityExportCells.Amount(x.SettlementDifference, combined), x.SettlementStatus, x.Status, string.Empty,
                    // The totals row carries the report's own range-wide counts and notes, never a
                    // sum of the period rows' scoped ones.
                    value.PendingTransactionCount.ToString(CultureInfo.InvariantCulture),
                    value.RefundedTransactionCount.ToString(CultureInfo.InvariantCulture),
                    value.DeclinedOrCancelledTransactionCount.ToString(CultureInfo.InvariantCulture),
                    value.UnknownStatusTransactionCount.ToString(CultureInfo.InvariantCulture),
                    value.MissingStatusTransactionCount.ToString(CultureInfo.InvariantCulture)
                }.Concat(AvailabilityCells()).Append(string.Join(" ", value.DataQuality.Notes ?? [])).ToList());
            }
            return new ReportExportTable(rows, "Report");
        }
        if (report is "machine-profitability" or "machines")
        {
            var value = await _getMachineProfitabilityReport.Handle(filter, cancellationToken);
            // Each machine's sales figures carry the sales state, the actual fees the settlement
            // state, and direct profit the combined one (issue #731). The per-machine direct
            // operating expenses are not exported here; where they are, they carry no state, being
            // this business's own records.
            var machineSales = value.ProviderAvailability.Sales;
            var machineFees = value.ProviderAvailability.Fees;
            var machineCombined = value.ProviderAvailability.Combined;
            var rows = new[] { new List<string> { "MachineId", "MachineName", "Sales", "CardSales", "CashSales", "Quantity", "CostOfGoods", "GrossProfit", "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "ActualNayaxFee", "EstimatedNayaxFee", "EstimatedCardTransactionCount", "HasEstimates", "CommissionPercent", "SiteCommission", "DirectProfit", "DirectMarginPercent", "Transactions", SalesDataAvailabilityHeader, SalesDataAsOfUtcHeader, FeeDataAvailabilityHeader, FeeDataAsOfUtcHeader, CombinedDataAvailabilityHeader } }
                .Concat(value.Rows.Select(x => new List<string> { x.MachineId.ToString(), x.MachineName, AvailabilityExportCells.Amount(x.Sales, machineSales), AvailabilityExportCells.Amount(x.CardSales, machineSales), AvailabilityExportCells.Amount(x.CashSales, machineSales), AvailabilityExportCells.Amount(x.Quantity, machineSales), AvailabilityExportCells.Amount(x.CostOfGoods, machineSales), AvailabilityExportCells.Amount(x.GrossProfit, machineSales), AvailabilityExportCells.Amount(x.NayaxProcessingFees.TotalFeeExGst, machineFees), AvailabilityExportCells.Amount(x.NayaxProcessingFees.TotalFeeGst, machineFees), AvailabilityExportCells.Amount(x.NayaxProcessingFees.TotalFeeIncGst, machineFees), AvailabilityExportCells.Amount(x.NayaxProcessingFees.ActualFeeIncGst, machineFees), AvailabilityExportCells.Amount(x.NayaxProcessingFees.EstimatedFeeIncGst, machineCombined), AvailabilityExportCells.Amount(x.NayaxProcessingFees.EstimatedCardTransactionCount, machineCombined), x.NayaxProcessingFees.HasEstimatedFees.ToString(), AvailabilityExportCells.Amount(x.CommissionPercent, machineSales), AvailabilityExportCells.Amount(x.SiteCommission, machineSales), AvailabilityExportCells.Amount(x.DirectProfit, machineCombined), AvailabilityExportCells.Amount(x.DirectMarginPercent, machineCombined), AvailabilityExportCells.Amount(x.TransactionCount, machineSales), AvailabilityExportCells.State(machineSales), AvailabilityExportCells.AsOfUtc(machineSales), AvailabilityExportCells.State(machineFees), AvailabilityExportCells.AsOfUtc(machineFees), AvailabilityExportCells.State(machineCombined) })).ToList();
            // An absent machine is the figure a reader is most likely to misread as "nothing
            // happened", so a result with no machine row still states its three families.
            if (value.Rows.Count == 0)
                rows.Add(AvailabilityExportCells.NoRowsRow(rows[0],
                    AvailabilityExportCells.State(machineSales), AvailabilityExportCells.AsOfUtc(machineSales),
                    AvailabilityExportCells.State(machineFees), AvailabilityExportCells.AsOfUtc(machineFees),
                    AvailabilityExportCells.State(machineCombined)));
            return new ReportExportTable(rows, "Report");
        }
        if (report is "gst" or "gst-accounting")
        {
            var value = await _getGstAccountingAid.Handle(filter, cancellationToken);
            // The provider-sourced figures carry the availability of the data that feeds them, and
            // the state columns report it in full; the purchase and operating-expense figures are
            // this business's own records and stay exact (issue #730, see AvailabilityExportCells).
            var sales = value.ProviderAvailability.Sales;
            var gstFees = value.ProviderAvailability.Fees;
            var rows = new List<List<string>>
            {
                new() { "From", "To", "TaxableSales", "GstOnSales", "TaxableFees", "GstOnFees", "PurchaseLineGst", "PurchaseChargeGst", "PurchaseInputGst", "PurchaseUnresolvedComponents", "PurchaseUnresolvedAmount", "PurchaseGstIncomplete", "NetGst", SalesDataAvailabilityHeader, SalesDataAsOfUtcHeader, FeeDataAvailabilityHeader, FeeDataAsOfUtcHeader, "NetGstAvailability", "DataQualityNotes" },
                new() { value.From.ToString(DateFormat), value.To.ToString(DateFormat), AvailabilityExportCells.Amount(value.TaxableSales, sales), AvailabilityExportCells.Amount(value.GstOnSales, sales), AvailabilityExportCells.Amount(value.TaxableFees, gstFees), AvailabilityExportCells.Amount(value.GstOnFees, gstFees), value.PurchaseLineGst.ToString(CultureInfo.InvariantCulture), value.PurchaseChargeGst.ToString(CultureInfo.InvariantCulture), value.InventoryPurchaseGst.ToString(CultureInfo.InvariantCulture), value.PurchaseUnresolvedComponentCount.ToString(CultureInfo.InvariantCulture), value.PurchaseUnresolvedAmount.ToString(CultureInfo.InvariantCulture), value.PurchaseGstIncomplete.ToString(), AvailabilityExportCells.Amount(value.NetGst, value.NetGstAvailability), AvailabilityExportCells.State(sales), AvailabilityExportCells.AsOfUtc(sales), AvailabilityExportCells.State(gstFees), AvailabilityExportCells.AsOfUtc(gstFees), AvailabilityExportCells.State(value.NetGstAvailability), string.Join(" ", value.DataQuality.Notes ?? []) }
            };
            return new ReportExportTable(rows, "Report");
        }
        if (report is "dashboard")
        {
            var value = await _getDashboardReport.Handle(filter, cancellationToken);
            // Every figure in this summary is counted from the completed sales the provider
            // supplied - including the machine, product and unmapped-product counts, which are
            // distinct counts over those sales and not a catalogue size - so all of them carry the
            // sales state (issue #731). The fee state is reported beside them because the summary's
            // own API response shows the Nayax fees and the actual reimbursement.
            var dashboardSales = value.ProviderAvailability.Sales;
            var dashboardFees = value.ProviderAvailability.Fees;
            var dashboardCombined = value.ProviderAvailability.Combined;
            var rows = new List<List<string>>
            {
                new() { "From", "To", "Sales", "GrossProfit", "Transactions", "Quantity", "MachineCount", "ProductCount", "UnmappedProductCount", SalesDataAvailabilityHeader, SalesDataAsOfUtcHeader, FeeDataAvailabilityHeader, FeeDataAsOfUtcHeader, CombinedDataAvailabilityHeader },
                new() { value.From.ToString(DateFormat), value.To.ToString(DateFormat), AvailabilityExportCells.Amount(value.Sales, dashboardSales), AvailabilityExportCells.Amount(value.GrossProfit, dashboardSales), AvailabilityExportCells.Amount(value.Transactions, dashboardSales), AvailabilityExportCells.Amount(value.Quantity, dashboardSales), AvailabilityExportCells.Amount(value.MachineCount, dashboardSales), AvailabilityExportCells.Amount(value.ProductCount, dashboardSales), AvailabilityExportCells.Amount(value.UnmappedProductCount, dashboardSales), AvailabilityExportCells.State(dashboardSales), AvailabilityExportCells.AsOfUtc(dashboardSales), AvailabilityExportCells.State(dashboardFees), AvailabilityExportCells.AsOfUtc(dashboardFees), AvailabilityExportCells.State(dashboardCombined) }
            };
            return new ReportExportTable(rows, "Report");
        }
        var products = await _getProductProfitabilityReport.Handle(filter, cancellationToken);
        // One state, because nothing in this report comes from a settlement import (issue #731).
        var productSales = products.SalesAvailability;
        var productRows = new[] { new List<string> { "Product", "Sales", "Quantity", "CostOfGoods", "GrossProfit", "MarginPercent", "Transactions", "Unmapped", SalesDataAvailabilityHeader, SalesDataAsOfUtcHeader } }
            .Concat(products.Rows.Select(x => new List<string> { x.ProductName, AvailabilityExportCells.Amount(x.Sales, productSales), AvailabilityExportCells.Amount(x.Quantity, productSales), AvailabilityExportCells.Amount(x.CostOfGoods, productSales), AvailabilityExportCells.Amount(x.GrossProfit, productSales), AvailabilityExportCells.Amount(x.MarginPercent, productSales), AvailabilityExportCells.Amount(x.TransactionCount, productSales), x.IsUnmapped.ToString(), AvailabilityExportCells.State(productSales), AvailabilityExportCells.AsOfUtc(productSales) })).ToList();
        // The same for an absent product: an empty product list is reported with the state behind
        // it, never left to read as "nothing sold".
        if (products.Rows.Count == 0)
            productRows.Add(AvailabilityExportCells.NoRowsRow(productRows[0],
                AvailabilityExportCells.State(productSales), AvailabilityExportCells.AsOfUtc(productSales)));
        return new ReportExportTable(productRows, "Report");
    }

    private static long? MachineId(ReportingFilterDto filter) => filter.MachineId ?? filter.MachineID;
}
