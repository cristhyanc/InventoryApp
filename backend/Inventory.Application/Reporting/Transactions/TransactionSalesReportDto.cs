using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Transactions;

public record TransactionSalesReportDto(
    DateTime From, DateTime To, IReadOnlyList<TransactionSalesRowDto> Rows, TransactionSalesTotalsDto Totals,
    ReportingDataQualityDto DataQuality, int Page, int PageSize, int TotalCount,
    TransactionSalesFilterOptionsDto FilterOptions);
