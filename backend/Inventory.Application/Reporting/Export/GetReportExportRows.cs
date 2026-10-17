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
                "TransactionStatus", "IsCompleted"
            }
        };
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
            Number(x.CommissionAmount), Number(x.TransactionStatusId), x.TransactionStatus, x.IsCompleted.ToString()
        }));
        return new ReportExportTable(rows, "Transactions");
    }

    public async Task<ReportExportTable> Handle(string report, ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        report = report.Trim().ToLowerInvariant();
        if (report is "daily")
        {
            var value = await _getDailyReport.Handle(filter, cancellationToken);
            var rows = new[] { new List<string>
                {
                    "Date", "GrossSales", "CardSales", "CashSales", "AverageSale", "Quantity",
                    "CostOfGoods", "GrossProfit", "GrossMarginPercent", "Transactions",
                    "IsCogsComplete", "UncostedTransactionCount", "UncostedSalesAmount",
                    "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "FeeSource", "ImportedReimbursement",
                    "NetReimbursement", "ReconciliationStatus"
                } }
                .Concat(value.Rows.Select(x => new List<string>
                {
                    x.Date.ToString("yyyy-MM-dd"), x.GrossSales.ToString(CultureInfo.InvariantCulture),
                    x.CardSales.ToString(CultureInfo.InvariantCulture), x.CashSales.ToString(CultureInfo.InvariantCulture),
                    x.AverageSale.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture),
                    Number(x.CostOfGoods), Number(x.GrossProfit),
                    Number(x.GrossMarginPercent), x.TransactionCount.ToString(),
                    x.IsCogsComplete.ToString(), x.UncostedTransactionCount.ToString(),
                    x.UncostedSalesAmount.ToString(CultureInfo.InvariantCulture),
                    x.NayaxFeesExGst.ToString(CultureInfo.InvariantCulture), x.NayaxFeesGst.ToString(CultureInfo.InvariantCulture), x.NayaxFeesIncludingGst.ToString(CultureInfo.InvariantCulture), x.NayaxFeeSource,
                    x.ImportedReimbursement.ToString(CultureInfo.InvariantCulture), x.NetReimbursement.ToString(CultureInfo.InvariantCulture),
                    x.ReconciliationStatus
                })).ToList();
            if (value.Totals is not null)
                rows.Add(new List<string>
                {
                    "TOTAL", value.Totals.GrossSales.ToString(CultureInfo.InvariantCulture),
                    value.Totals.CardSales.ToString(CultureInfo.InvariantCulture), value.Totals.CashSales.ToString(CultureInfo.InvariantCulture),
                    value.Totals.AverageSale.ToString(CultureInfo.InvariantCulture), value.Totals.Quantity.ToString(CultureInfo.InvariantCulture),
                    Number(value.Totals.CostOfGoods), Number(value.Totals.GrossProfit),
                    Number(value.Totals.GrossMarginPercent), value.Totals.TransactionCount.ToString(),
                    value.Totals.IsCogsComplete.ToString(), value.Totals.UncostedTransactionCount.ToString(),
                    value.Totals.UncostedSalesAmount.ToString(CultureInfo.InvariantCulture),
                    value.Totals.NayaxFeesExGst.ToString(CultureInfo.InvariantCulture),
                    value.Totals.NayaxProcessingFees.TotalFeeGst.ToString(CultureInfo.InvariantCulture),
                    value.Totals.NayaxFeesIncludingGst.ToString(CultureInfo.InvariantCulture),
                    string.Empty,
                    value.Totals.ImportedReimbursement.ToString(CultureInfo.InvariantCulture), value.Totals.NetReimbursement.ToString(CultureInfo.InvariantCulture),
                    string.Empty
                });
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
                new() { "From", "To", "FinancialYear", "GrossSales", "CardSales", "CashSales", "CardTransactions", "CashTransactions", "COGS", "GrossProfit",                 "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "ActualNayaxFee", "EstimatedNayaxFee", "EstimatedCardTransactionCount", "HasEstimates", "DeliveryCosts", "PackageCosts", "OtherOperatingExpenses", "NetSettlement", "SiteCommission", isMachineFiltered ? "DirectProfit" : "NetProfit", isMachineFiltered ? "DirectMarginPercent" : "NetMargin", "GstOnSales", "GstOnFees", "GstOnSalesBasis", "IsCogsComplete", "UncostedTransactionCount", "UncostedSalesAmount", "PendingTransactionCount", "RefundedTransactionCount", "DeclinedOrCancelledTransactionCount", "UnknownStatusTransactionCount", "MissingStatusTransactionCount", "SalesDataAvailability", "SalesDataAsOfUtc", "FeeDataAvailability", "FeeDataAsOfUtc", "CombinedDataAvailability", "DataQualityNotes" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.FinancialYear, AvailabilityExportCells.Amount(value.Sales, sales), AvailabilityExportCells.Amount(value.CardSales, sales), AvailabilityExportCells.Amount(value.CashSales, sales), AvailabilityExportCells.Amount(value.CardTransactionCount, sales), AvailabilityExportCells.Amount(value.CashTransactionCount, sales), AvailabilityExportCells.Amount(value.CostOfGoods, sales), AvailabilityExportCells.Amount(value.GrossProfit, sales), AvailabilityExportCells.Amount(value.NayaxProcessingFees.TotalFeeExGst, fees), AvailabilityExportCells.Amount(value.NayaxProcessingFees.TotalFeeGst, fees), AvailabilityExportCells.Amount(value.NayaxProcessingFees.TotalFeeIncGst, fees), AvailabilityExportCells.Amount(value.NayaxProcessingFees.ActualFeeIncGst, fees), AvailabilityExportCells.Amount(value.NayaxProcessingFees.EstimatedFeeIncGst, combined), AvailabilityExportCells.Amount(value.NayaxProcessingFees.EstimatedCardTransactionCount, combined), value.NayaxProcessingFees.HasEstimatedFees.ToString(), value.DeliveryCosts.ToString(CultureInfo.InvariantCulture), value.PackageCosts.ToString(CultureInfo.InvariantCulture), value.OtherOperatingExpenses.ToString(CultureInfo.InvariantCulture), AvailabilityExportCells.Amount(value.NetSettlement, combined), AvailabilityExportCells.Amount(value.SiteCommission, sales), AvailabilityExportCells.Amount(isMachineFiltered ? value.DirectProfit : value.NetProfit, combined), AvailabilityExportCells.Amount(isMachineFiltered ? value.DirectMarginPercent : value.NetMarginPercent, combined), AvailabilityExportCells.Amount(value.GstOnSales, sales), AvailabilityExportCells.Amount(value.GstOnFees, fees), BookkeepingGstOnSalesBasis, value.IsCogsComplete.ToString(), value.UncostedTransactionCount.ToString(CultureInfo.InvariantCulture), value.UncostedSalesAmount.ToString(CultureInfo.InvariantCulture), value.PendingTransactionCount.ToString(CultureInfo.InvariantCulture), value.RefundedTransactionCount.ToString(CultureInfo.InvariantCulture), value.DeclinedOrCancelledTransactionCount.ToString(CultureInfo.InvariantCulture), value.UnknownStatusTransactionCount.ToString(CultureInfo.InvariantCulture), value.MissingStatusTransactionCount.ToString(CultureInfo.InvariantCulture), AvailabilityExportCells.State(sales), AvailabilityExportCells.AsOfUtc(sales), AvailabilityExportCells.State(fees), AvailabilityExportCells.AsOfUtc(fees), AvailabilityExportCells.State(combined), string.Join(" ", value.DataQuality.Notes ?? []) }
            };
            return new ReportExportTable(rows, "Report");
        }
        if (report is "reconciliation")
        {
            var value = await _getReconciliationReport.Handle(filter, 0.01m, cancellationToken);
            var rows = new List<List<string>>
            {
                new() { "From", "To", "TotalVendingSales", "CardSales", "CashSales", "CardTransactionSales", "NayaxReportedGrossCardSales", "CardTransactionCount", "NayaxReportedCardTransactionCount", "CountDifference", "GrossDifference", "GrossStatus", "ProcessingFeesExGst", "FeeGst", "OtherFees", "Adjustments", "AdjustmentsSupported", "AdjustmentsBasis", "ExpectedNetReimbursement", "ActualNetReimbursement", "SettlementDifference", "SettlementStatus", "Status", "PayoutDate", "PendingTransactionCount", "RefundedTransactionCount", "DeclinedOrCancelledTransactionCount", "UnknownStatusTransactionCount", "MissingStatusTransactionCount", "DataQualityNotes" }
            };
            rows.AddRange(value.PeriodRows.Select(x => new List<string>
            {
                x.From.ToString("yyyy-MM-dd"), x.To.ToString("yyyy-MM-dd"),
                x.TotalVendingSales.ToString(CultureInfo.InvariantCulture), x.CardSales.ToString(CultureInfo.InvariantCulture),
                x.CashSales.ToString(CultureInfo.InvariantCulture), x.CardTransactionSales.ToString(CultureInfo.InvariantCulture),
                x.NayaxReportedGrossCardSales.ToString(CultureInfo.InvariantCulture), x.CardTransactionCount.ToString(),
                x.NayaxReportedCardTransactionCount.ToString(), x.CountDifference.ToString(),
                x.GrossDifference.ToString(CultureInfo.InvariantCulture), x.GrossStatus,
                x.ProcessingFeesExGst.ToString(CultureInfo.InvariantCulture), x.FeeGst.ToString(CultureInfo.InvariantCulture),
                x.OtherFees.ToString(CultureInfo.InvariantCulture), x.Adjustments.ToString(CultureInfo.InvariantCulture),
                x.AdjustmentsSupported.ToString(), GetReconciliationReport.AdjustmentsAssumption,
                x.ExpectedNetReimbursement.ToString(CultureInfo.InvariantCulture), x.ActualNetReimbursement.ToString(CultureInfo.InvariantCulture),
                x.SettlementDifference.ToString(CultureInfo.InvariantCulture), x.SettlementStatus, x.Status,
                x.PayoutDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                x.PendingTransactionCount.ToString(CultureInfo.InvariantCulture),
                x.RefundedTransactionCount.ToString(CultureInfo.InvariantCulture),
                x.DeclinedOrCancelledTransactionCount.ToString(CultureInfo.InvariantCulture),
                x.UnknownStatusTransactionCount.ToString(CultureInfo.InvariantCulture),
                x.MissingStatusTransactionCount.ToString(CultureInfo.InvariantCulture),
                string.Join(" ", x.DataQuality.Notes ?? [])
            }));
            if (value.Totals is not null)
            {
                var x = value.Totals;
                rows.Add(new List<string>
                {
                    "TOTAL", string.Empty, x.TotalVendingSales.ToString(CultureInfo.InvariantCulture),
                    x.CardSales.ToString(CultureInfo.InvariantCulture), x.CashSales.ToString(CultureInfo.InvariantCulture),
                    x.CardTransactionSales.ToString(CultureInfo.InvariantCulture), x.NayaxReportedGrossCardSales.ToString(CultureInfo.InvariantCulture),
                    x.CardTransactionCount.ToString(), x.NayaxReportedCardTransactionCount.ToString(), x.CountDifference.ToString(),
                    x.GrossDifference.ToString(CultureInfo.InvariantCulture), x.GrossStatus,
                    x.ProcessingFeesExGst.ToString(CultureInfo.InvariantCulture), x.FeeGst.ToString(CultureInfo.InvariantCulture),
                    x.OtherFees.ToString(CultureInfo.InvariantCulture), x.Adjustments.ToString(CultureInfo.InvariantCulture),
                    x.AdjustmentsSupported.ToString(), GetReconciliationReport.AdjustmentsAssumption,
                    x.ExpectedNetReimbursement.ToString(CultureInfo.InvariantCulture), x.ActualNetReimbursement.ToString(CultureInfo.InvariantCulture),
                    x.SettlementDifference.ToString(CultureInfo.InvariantCulture), x.SettlementStatus, x.Status, string.Empty,
                    // The totals row carries the report's own range-wide counts and notes, never a
                    // sum of the period rows' scoped ones.
                    value.PendingTransactionCount.ToString(CultureInfo.InvariantCulture),
                    value.RefundedTransactionCount.ToString(CultureInfo.InvariantCulture),
                    value.DeclinedOrCancelledTransactionCount.ToString(CultureInfo.InvariantCulture),
                    value.UnknownStatusTransactionCount.ToString(CultureInfo.InvariantCulture),
                    value.MissingStatusTransactionCount.ToString(CultureInfo.InvariantCulture),
                    string.Join(" ", value.DataQuality.Notes ?? [])
                });
            }
            return new ReportExportTable(rows, "Report");
        }
        if (report is "machine-profitability" or "machines")
        {
            var value = await _getMachineProfitabilityReport.Handle(filter, cancellationToken);
            var rows = new[] { new List<string> { "MachineId", "MachineName", "Sales", "CardSales", "CashSales", "Quantity", "CostOfGoods", "GrossProfit", "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "ActualNayaxFee", "EstimatedNayaxFee", "EstimatedCardTransactionCount", "HasEstimates", "CommissionPercent", "SiteCommission", "DirectProfit", "DirectMarginPercent", "Transactions" } }
                .Concat(value.Rows.Select(x => new List<string> { x.MachineId.ToString(), x.MachineName, x.Sales.ToString(CultureInfo.InvariantCulture), x.CardSales.ToString(CultureInfo.InvariantCulture), x.CashSales.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture), Number(x.CostOfGoods), Number(x.GrossProfit), x.NayaxProcessingFees.TotalFeeExGst.ToString(CultureInfo.InvariantCulture), x.NayaxProcessingFees.TotalFeeGst.ToString(CultureInfo.InvariantCulture), x.NayaxProcessingFees.TotalFeeIncGst.ToString(CultureInfo.InvariantCulture), x.NayaxProcessingFees.ActualFeeIncGst.ToString(CultureInfo.InvariantCulture), x.NayaxProcessingFees.EstimatedFeeIncGst.ToString(CultureInfo.InvariantCulture), x.NayaxProcessingFees.EstimatedCardTransactionCount.ToString(), x.NayaxProcessingFees.HasEstimatedFees.ToString(), x.CommissionPercent.ToString(CultureInfo.InvariantCulture), x.SiteCommission.ToString(CultureInfo.InvariantCulture), Number(x.DirectProfit), Number(x.DirectMarginPercent), x.TransactionCount.ToString(CultureInfo.InvariantCulture) })).ToList();
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
                new() { "From", "To", "TaxableSales", "GstOnSales", "TaxableFees", "GstOnFees", "PurchaseLineGst", "PurchaseChargeGst", "PurchaseInputGst", "PurchaseUnresolvedComponents", "PurchaseUnresolvedAmount", "PurchaseGstIncomplete", "NetGst", "SalesDataAvailability", "SalesDataAsOfUtc", "FeeDataAvailability", "FeeDataAsOfUtc", "NetGstAvailability", "DataQualityNotes" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), AvailabilityExportCells.Amount(value.TaxableSales, sales), AvailabilityExportCells.Amount(value.GstOnSales, sales), AvailabilityExportCells.Amount(value.TaxableFees, gstFees), AvailabilityExportCells.Amount(value.GstOnFees, gstFees), value.PurchaseLineGst.ToString(CultureInfo.InvariantCulture), value.PurchaseChargeGst.ToString(CultureInfo.InvariantCulture), value.InventoryPurchaseGst.ToString(CultureInfo.InvariantCulture), value.PurchaseUnresolvedComponentCount.ToString(CultureInfo.InvariantCulture), value.PurchaseUnresolvedAmount.ToString(CultureInfo.InvariantCulture), value.PurchaseGstIncomplete.ToString(), AvailabilityExportCells.Amount(value.NetGst, value.NetGstAvailability), AvailabilityExportCells.State(sales), AvailabilityExportCells.AsOfUtc(sales), AvailabilityExportCells.State(gstFees), AvailabilityExportCells.AsOfUtc(gstFees), AvailabilityExportCells.State(value.NetGstAvailability), string.Join(" ", value.DataQuality.Notes ?? []) }
            };
            return new ReportExportTable(rows, "Report");
        }
        if (report is "dashboard")
        {
            var value = await _getDashboardReport.Handle(filter, cancellationToken);
            var rows = new List<List<string>>
            {
                new() { "From", "To", "Sales", "GrossProfit", "Transactions", "Quantity", "MachineCount", "ProductCount", "UnmappedProductCount" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.Sales.ToString(CultureInfo.InvariantCulture), Number(value.GrossProfit), value.Transactions.ToString(CultureInfo.InvariantCulture), value.Quantity.ToString(CultureInfo.InvariantCulture), value.MachineCount.ToString(), value.ProductCount.ToString(), value.UnmappedProductCount.ToString() }
            };
            return new ReportExportTable(rows, "Report");
        }
        var products = await _getProductProfitabilityReport.Handle(filter, cancellationToken);
        var productRows = new[] { new List<string> { "Product", "Sales", "Quantity", "CostOfGoods", "GrossProfit", "MarginPercent", "Transactions", "Unmapped" } }
            .Concat(products.Rows.Select(x => new List<string> { x.ProductName, x.Sales.ToString(CultureInfo.InvariantCulture), x.Quantity.ToString(CultureInfo.InvariantCulture), Number(x.CostOfGoods), Number(x.GrossProfit), Number(x.MarginPercent), x.TransactionCount.ToString(CultureInfo.InvariantCulture), x.IsUnmapped.ToString() })).ToList();
        return new ReportExportTable(productRows, "Report");
    }

    private static long? MachineId(ReportingFilterDto filter) => filter.MachineId ?? filter.MachineID;
}
