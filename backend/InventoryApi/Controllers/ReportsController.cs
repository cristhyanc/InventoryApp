using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using InventoryApi.Adapters.Export;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly GetReportExportRows _getReportExportRows;
    private readonly GetBookkeepingReport _getBookkeepingReport;
    private readonly GetDailyReport _getDailyReport;
    private readonly GetReconciliationReport _getReconciliationReport;
    private readonly GetMachineProfitabilityReport _getMachineProfitabilityReport;
    private readonly GetProductProfitabilityReport _getProductProfitabilityReport;
    private readonly GetGstAccountingAid _getGstAccountingAid;
    private readonly GetDashboardReport _getDashboardReport;
    private readonly GetTransactionSalesReport _getTransactionSalesReport;

    public ReportsController(GetReportExportRows getReportExportRows, GetBookkeepingReport getBookkeepingReport,
        GetDailyReport getDailyReport, GetReconciliationReport getReconciliationReport,
        GetMachineProfitabilityReport getMachineProfitabilityReport,
        GetProductProfitabilityReport getProductProfitabilityReport,
        GetGstAccountingAid getGstAccountingAid,
        GetDashboardReport getDashboardReport,
        GetTransactionSalesReport getTransactionSalesReport)
    {
        _getReportExportRows = getReportExportRows;
        _getBookkeepingReport = getBookkeepingReport;
        _getDailyReport = getDailyReport;
        _getReconciliationReport = getReconciliationReport;
        _getMachineProfitabilityReport = getMachineProfitabilityReport;
        _getProductProfitabilityReport = getProductProfitabilityReport;
        _getGstAccountingAid = getGstAccountingAid;
        _getDashboardReport = getDashboardReport;
        _getTransactionSalesReport = getTransactionSalesReport;
    }

    [HttpGet("bookkeeping")]
    public Task<BookkeepingReportDto> Bookkeeping([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getBookkeepingReport.Handle(filter, ct);
    [HttpGet("daily")]
    public Task<DailyReportDto> Daily([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getDailyReport.Handle(filter, ct);
    [HttpGet("reconciliation")]
    public Task<ReconciliationReportDto> Reconciliation([FromQuery] ReportingFilterDto filter, [FromQuery] decimal tolerance = 0.01m, CancellationToken ct = default) => _getReconciliationReport.Handle(filter, tolerance, ct);
    [HttpGet("machine-profitability")]
    public Task<MachineProfitabilityReportDto> MachineProfitability([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getMachineProfitabilityReport.Handle(filter, ct);
    [HttpGet("product-profitability")]
    public Task<ProductProfitabilityReportDto> ProductProfitability([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getProductProfitabilityReport.Handle(filter, ct);
    [HttpGet("gst")]
    public Task<GstAccountingAidDto> Gst([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getGstAccountingAid.Handle(filter, ct);
    [HttpGet("dashboard")]
    public Task<DashboardReportDto> Dashboard([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getDashboardReport.Handle(filter, ct);
    [HttpGet("transactions")]
    public Task<TransactionSalesReportDto> Transactions([FromQuery] TransactionSalesFilterDto filter, CancellationToken ct) =>
        _getTransactionSalesReport.Handle(filter, ct);

    [HttpGet("{report}/export")]
    public async Task<IActionResult> Export(string report, [FromQuery] string format = "csv",
        [FromQuery] ReportingFilterDto? filter = null, [FromQuery] TransactionSalesFilterDto? transactionFilter = null,
        CancellationToken ct = default)
    {
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase) && !string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest("format must be csv or xlsx.");
        var isTransactionReport = report.Equals("transactions", StringComparison.OrdinalIgnoreCase) ||
            report.Equals("transaction-sales", StringComparison.OrdinalIgnoreCase);
        var table = isTransactionReport
            ? await _getReportExportRows.Handle(report, transactionFilter ?? new TransactionSalesFilterDto(), ct)
            : await _getReportExportRows.Handle(report, filter ?? new ReportingFilterDto(), ct);
        var bytes = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase)
            ? ReportExportFileWriter.WriteXlsx(table)
            : ReportExportFileWriter.WriteCsv(table);
        return File(bytes, format.Equals("xlsx", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv",
            $"{report}.{format.ToLowerInvariant()}");
    }
}
