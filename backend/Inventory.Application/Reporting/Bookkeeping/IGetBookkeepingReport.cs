using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Bookkeeping;

/// <summary>
/// Application-owned interface for the bookkeeping report use case, so other report features (such
/// as the GST accounting aid) can reuse its authoritative result without depending on the concrete
/// <see cref="GetBookkeepingReport"/> class.
/// </summary>
public interface IGetBookkeepingReport
{
    Task<BookkeepingReportDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken);
}
