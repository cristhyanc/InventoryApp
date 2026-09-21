using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;

namespace InventoryApi.Services.Interfaces;

public interface IReportingService
{
    Task<BookkeepingReportDto> GetBookkeeping(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<DailyReportDto> GetDaily(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<ReconciliationReportDto> GetReconciliation(ReportingFilterDto filter, decimal tolerance = 0.01m, CancellationToken cancellationToken = default);
    Task<MachineProfitabilityReportDto> GetMachineProfitability(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<ProductProfitabilityReportDto> GetProductProfitability(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<GstAccountingAidDto> GetGst(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<DashboardReportDto> GetDashboard(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<BookkeepingReportDto> GetBookkeepingAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<DailyReportDto> GetDailyAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<ReconciliationReportDto> GetReconciliationAsync(ReportingFilterDto filter, decimal tolerance = 0.01m, CancellationToken cancellationToken = default);
    Task<MachineProfitabilityReportDto> GetMachineProfitabilityAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<ProductProfitabilityReportDto> GetProductProfitabilityAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<GstAccountingAidDto> GetGstAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<DashboardReportDto> GetDashboardAsync(ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<TransactionSalesReportDto> GetTransactionsAsync(TransactionSalesFilterDto filter, CancellationToken cancellationToken = default);
    Task<byte[]> ExportCsvAsync(string report, ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<byte[]> ExportXlsxAsync(string report, ReportingFilterDto filter, CancellationToken cancellationToken = default);
    Task<byte[]> ExportCsvAsync(string report, TransactionSalesFilterDto filter, CancellationToken cancellationToken = default);
    Task<byte[]> ExportXlsxAsync(string report, TransactionSalesFilterDto filter, CancellationToken cancellationToken = default);
}
