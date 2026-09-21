using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportingService _service;
    private readonly GetBookkeepingReport _getBookkeepingReport;
    private readonly GetDailyReport _getDailyReport;
    private readonly GetReconciliationReport _getReconciliationReport;
    private readonly GetMachineProfitabilityReport _getMachineProfitabilityReport;
    private readonly GetProductProfitabilityReport _getProductProfitabilityReport;

    public ReportsController(IReportingService service, GetBookkeepingReport getBookkeepingReport,
        GetDailyReport getDailyReport, GetReconciliationReport getReconciliationReport,
        GetMachineProfitabilityReport getMachineProfitabilityReport,
        GetProductProfitabilityReport getProductProfitabilityReport)
    {
        _service = service;
        _getBookkeepingReport = getBookkeepingReport;
        _getDailyReport = getDailyReport;
        _getReconciliationReport = getReconciliationReport;
        _getMachineProfitabilityReport = getMachineProfitabilityReport;
        _getProductProfitabilityReport = getProductProfitabilityReport;
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
    public Task<GstAccountingAidDto> Gst([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _service.GetGstAsync(filter, ct);
    [HttpGet("dashboard")]
    public Task<DashboardReportDto> Dashboard([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _service.GetDashboardAsync(filter, ct);
    [HttpGet("transactions")]
    public Task<TransactionSalesReportDto> Transactions([FromQuery] TransactionSalesFilterDto filter, CancellationToken ct) =>
        _service.GetTransactionsAsync(filter, ct);

    [HttpGet("{report}/export")]
    public async Task<IActionResult> Export(string report, [FromQuery] string format = "csv",
        [FromQuery] ReportingFilterDto? filter = null, [FromQuery] TransactionSalesFilterDto? transactionFilter = null,
        CancellationToken ct = default)
    {
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase) && !string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest("format must be csv or xlsx.");
        var isTransactionReport = report.Equals("transactions", StringComparison.OrdinalIgnoreCase) ||
            report.Equals("transaction-sales", StringComparison.OrdinalIgnoreCase);
        var bytes = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase)
            ? isTransactionReport
                ? await _service.ExportXlsxAsync(report, transactionFilter ?? new TransactionSalesFilterDto(), ct)
                : await _service.ExportXlsxAsync(report, filter ?? new ReportingFilterDto(), ct)
            : isTransactionReport
                ? await _service.ExportCsvAsync(report, transactionFilter ?? new TransactionSalesFilterDto(), ct)
                : await _service.ExportCsvAsync(report, filter ?? new ReportingFilterDto(), ct);
        return File(bytes, format.Equals("xlsx", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv",
            $"{report}.{format.ToLowerInvariant()}");
    }
}
