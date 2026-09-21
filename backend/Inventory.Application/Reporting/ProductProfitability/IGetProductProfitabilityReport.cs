using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.ProductProfitability;

/// <summary>
/// Application-owned interface for the product profitability report use case, so other report
/// features (such as the dashboard) can reuse its authoritative result without depending on the
/// concrete <see cref="GetProductProfitabilityReport"/> class.
/// </summary>
public interface IGetProductProfitabilityReport
{
    Task<ProductProfitabilityReportDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken);
}
