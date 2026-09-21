using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.MachineProfitability;

public record MachineProfitabilityReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<MachineProfitabilityRowDto> Rows,
    ReportingDataQualityDto DataQuality);
