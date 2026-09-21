using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;

namespace InventoryApi.Tests.Application.Reporting.ProductProfitability;

/// <summary>
/// In-memory stub of the Application-owned product profitability use-case interface, so the
/// dashboard use case can be tested against a controlled product profitability result without
/// exercising the real matching orchestration or its facts port.
/// </summary>
public sealed class FakeGetProductProfitabilityReport : IGetProductProfitabilityReport
{
    private readonly ProductProfitabilityReportDto _report;

    public FakeGetProductProfitabilityReport(ProductProfitabilityReportDto report) => _report = report;

    public ReportingFilterDto LastRequest { get; private set; }

    public Task<ProductProfitabilityReportDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        LastRequest = filter;
        return Task.FromResult(_report);
    }
}
