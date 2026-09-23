using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;

namespace InventoryApi.Tests.Application.Reporting.Gst;

/// <summary>
/// In-memory stub of the Application-owned bookkeeping use-case interface, so the GST accounting-aid
/// use case can be tested against a controlled bookkeeping result without exercising the real
/// bookkeeping orchestration or its facts port.
/// </summary>
public sealed class FakeGetBookkeepingReport : IGetBookkeepingReport
{
    private readonly BookkeepingReportDto _report;

    public FakeGetBookkeepingReport(BookkeepingReportDto report) => _report = report;

    public ReportingFilterDto? LastRequest { get; private set; }

    public Task<BookkeepingReportDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        LastRequest = filter;
        return Task.FromResult(_report);
    }
}
