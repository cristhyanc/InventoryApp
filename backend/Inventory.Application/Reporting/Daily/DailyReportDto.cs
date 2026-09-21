using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Daily;

public record DailyReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<DailyReportRowDto> Rows,
    ReportingDataQualityDto DataQuality,
    DailyReportTotalsDto? Totals = null);
