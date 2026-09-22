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
                    x.NayaxFeesExGst.ToString(CultureInfo.InvariantCulture), (x.NayaxFeesIncludingGst - x.NayaxFeesExGst).ToString(CultureInfo.InvariantCulture), x.NayaxFeesIncludingGst.ToString(CultureInfo.InvariantCulture), x.NayaxFeeSource,
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
                    value.Totals.NayaxFeesExGst.ToString(CultureInfo.InvariantCulture), value.Totals.NayaxFeesIncludingGst.ToString(CultureInfo.InvariantCulture),
                    value.Totals.ImportedReimbursement.ToString(CultureInfo.InvariantCulture), value.Totals.NetReimbursement.ToString(CultureInfo.InvariantCulture),
                    string.Empty
                });
            return new ReportExportTable(rows, "Report");
        }
        if (report is "bookkeeping")
        {
            var value = await _getBookkeepingReport.Handle(filter, cancellationToken);
            var isMachineFiltered = MachineId(filter).HasValue;
            var rows = new List<List<string>>
            {
                new() { "From", "To", "FinancialYear", "GrossSales", "CardSales", "CashSales", "CardTransactions", "CashTransactions", "COGS", "GrossProfit",                 "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "ActualNayaxFee", "EstimatedNayaxFee", "EstimatedCardTransactionCount", "HasEstimates", "DeliveryCosts", "PackageCosts", "OtherOperatingExpenses", "NetSettlement", "SiteCommission", isMachineFiltered ? "DirectProfit" : "NetProfit", isMachineFiltered ? "DirectMarginPercent" : "NetMargin", "GstOnSales", "GstOnFees" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.FinancialYear, value.Sales.ToString(CultureInfo.InvariantCulture), value.CardSales.ToString(CultureInfo.InvariantCulture), value.CashSales.ToString(CultureInfo.InvariantCulture), value.CardTransactionCount.ToString(), value.CashTransactionCount.ToString(), Number(value.CostOfGoods), Number(value.GrossProfit), value.NayaxProcessingFees.TotalFeeExGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingFees.TotalFeeGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingFees.TotalFeeIncGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingFees.ActualFeeIncGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingFees.EstimatedFeeIncGst.ToString(CultureInfo.InvariantCulture), value.NayaxProcessingFees.EstimatedCardTransactionCount.ToString(), value.NayaxProcessingFees.HasEstimatedFees.ToString(), value.DeliveryCosts.ToString(CultureInfo.InvariantCulture), value.PackageCosts.ToString(CultureInfo.InvariantCulture), value.OtherOperatingExpenses.ToString(CultureInfo.InvariantCulture), value.NetSettlement.ToString(CultureInfo.InvariantCulture), value.SiteCommission.ToString(CultureInfo.InvariantCulture), Number(isMachineFiltered ? value.DirectProfit : value.NetProfit), Number(isMachineFiltered ? value.DirectMarginPercent : value.NetMarginPercent), value.GstOnSales.ToString(CultureInfo.InvariantCulture), value.GstOnFees.ToString(CultureInfo.InvariantCulture) }
            };
            return new ReportExportTable(rows, "Report");
        }
        if (report is "reconciliation")
        {
            var value = await _getReconciliationReport.Handle(filter, 0.01m, cancellationToken);
            var rows = new List<List<string>>
            {
                new() { "From", "To", "TotalVendingSales", "CardSales", "CashSales", "CardTransactionSales", "NayaxReportedGrossCardSales", "CardTransactionCount", "NayaxReportedCardTransactionCount", "CountDifference", "GrossDifference", "GrossStatus", "ProcessingFeesExGst", "FeeGst", "OtherFees", "Adjustments", "ExpectedNetReimbursement", "ActualNetReimbursement", "SettlementDifference", "SettlementStatus", "Status", "PayoutDate" }
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
                x.ExpectedNetReimbursement.ToString(CultureInfo.InvariantCulture), x.ActualNetReimbursement.ToString(CultureInfo.InvariantCulture),
                x.SettlementDifference.ToString(CultureInfo.InvariantCulture), x.SettlementStatus, x.Status,
                x.PayoutDate?.ToString("yyyy-MM-dd") ?? string.Empty
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
                    x.ExpectedNetReimbursement.ToString(CultureInfo.InvariantCulture), x.ActualNetReimbursement.ToString(CultureInfo.InvariantCulture),
                    x.SettlementDifference.ToString(CultureInfo.InvariantCulture), x.SettlementStatus, x.Status, string.Empty
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
            var rows = new List<List<string>>
            {
                new() { "From", "To", "TaxableSales", "GstOnSales", "TaxableFees", "GstOnFees", "NetGst" },
                new() { value.From.ToString("yyyy-MM-dd"), value.To.ToString("yyyy-MM-dd"), value.TaxableSales.ToString(CultureInfo.InvariantCulture), value.GstOnSales.ToString(CultureInfo.InvariantCulture), value.TaxableFees.ToString(CultureInfo.InvariantCulture), value.GstOnFees.ToString(CultureInfo.InvariantCulture), value.NetGst.ToString(CultureInfo.InvariantCulture) }
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
