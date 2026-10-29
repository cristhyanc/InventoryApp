using Inventory.Application.Access;
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
using Inventory.Domain.Subscriptions;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The financial reports and their CSV/XLSX exports.
///
/// Every action requires <c>Reports.View</c> (issue #526): these are the business's sales, profit,
/// GST and reconciliation figures, which #328's table gives to a Manager and an Owner. The export
/// shares the capability of the report it exports rather than carrying one of its own - a file is
/// the same figures in another container, and a separate capability would be a way to read a report
/// that is otherwise refused.
///
/// <para><strong>Every action also declares the plan feature it belongs to</strong> (issue #967).
/// Bookkeeping and the GST accounting aid are
/// <see cref="PlanFeature.BookkeepingAndGstReports"/> - the two reports that need no sales provider,
/// which a Starter business includes - and the other six are
/// <see cref="PlanFeature.ProviderSalesReports"/>. The export declares
/// <c>[RequirePlanPerReport]</c> for the same reason it shares the capability: it is the same
/// figures in another container, so it must need the same plan as the report it exports, and its
/// one route serves every family. A plan is evaluated after the capability, so a role refusal is
/// unchanged and a plan never grants a role more access.</para>
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.ReportsView)]
public sealed class ReportsController : ControllerBase
{
    private readonly GetReportExportRows _getReportExportRows;
    private readonly IReportExportFileWriter _reportExportFileWriter;
    private readonly GetBookkeepingReport _getBookkeepingReport;
    private readonly GetDailyReport _getDailyReport;
    private readonly GetReconciliationReport _getReconciliationReport;
    private readonly GetMachineProfitabilityReport _getMachineProfitabilityReport;
    private readonly GetProductProfitabilityReport _getProductProfitabilityReport;
    private readonly GetGstAccountingAid _getGstAccountingAid;
    private readonly GetDashboardReport _getDashboardReport;
    private readonly GetTransactionSalesReport _getTransactionSalesReport;

    public ReportsController(GetReportExportRows getReportExportRows,
        IReportExportFileWriter reportExportFileWriter, GetBookkeepingReport getBookkeepingReport,
        GetDailyReport getDailyReport, GetReconciliationReport getReconciliationReport,
        GetMachineProfitabilityReport getMachineProfitabilityReport,
        GetProductProfitabilityReport getProductProfitabilityReport,
        GetGstAccountingAid getGstAccountingAid,
        GetDashboardReport getDashboardReport,
        GetTransactionSalesReport getTransactionSalesReport)
    {
        _getReportExportRows = getReportExportRows;
        _reportExportFileWriter = reportExportFileWriter;
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
    [RequirePlan(PlanFeature.BookkeepingAndGstReports)]
    public Task<BookkeepingReportDto> Bookkeeping([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getBookkeepingReport.Handle(filter, ct);
    [HttpGet("daily")]
    [RequirePlan(PlanFeature.ProviderSalesReports)]
    public Task<DailyReportDto> Daily([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getDailyReport.Handle(filter, ct);
    [HttpGet("reconciliation")]
    [RequirePlan(PlanFeature.ProviderSalesReports)]
    public Task<ReconciliationReportDto> Reconciliation([FromQuery] ReportingFilterDto filter, [FromQuery] decimal tolerance = 0.01m, CancellationToken ct = default) => _getReconciliationReport.Handle(filter, tolerance, ct);
    [HttpGet("machine-profitability")]
    [RequirePlan(PlanFeature.ProviderSalesReports)]
    public Task<MachineProfitabilityReportDto> MachineProfitability([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getMachineProfitabilityReport.Handle(filter, ct);
    [HttpGet("product-profitability")]
    [RequirePlan(PlanFeature.ProviderSalesReports)]
    public Task<ProductProfitabilityReportDto> ProductProfitability([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getProductProfitabilityReport.Handle(filter, ct);
    [HttpGet("gst")]
    [RequirePlan(PlanFeature.BookkeepingAndGstReports)]
    public Task<GstAccountingAidDto> Gst([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getGstAccountingAid.Handle(filter, ct);
    [HttpGet("dashboard")]
    [RequirePlan(PlanFeature.ProviderSalesReports)]
    public Task<DashboardReportDto> Dashboard([FromQuery] ReportingFilterDto filter, CancellationToken ct) => _getDashboardReport.Handle(filter, ct);
    [HttpGet("transactions")]
    [RequirePlan(PlanFeature.ProviderSalesReports)]
    public Task<TransactionSalesReportDto> Transactions([FromQuery] TransactionSalesFilterDto filter, CancellationToken ct) =>
        _getTransactionSalesReport.Handle(filter, ct);

    [HttpGet("{report}/export")]
    [RequirePlanPerReport]
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
            ? _reportExportFileWriter.WriteXlsx(table)
            : _reportExportFileWriter.WriteCsv(table);
        return File(bytes, format.Equals("xlsx", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv",
            $"{report}.{format.ToLowerInvariant()}");
    }
}
