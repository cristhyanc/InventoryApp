using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.ProductProfitability;

public record ProductProfitabilityReportDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<ProductProfitabilityRowDto> Rows,
    ReportingDataQualityDto DataQuality);
