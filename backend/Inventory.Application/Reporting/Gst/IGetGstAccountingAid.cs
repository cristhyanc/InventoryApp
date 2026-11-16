using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// Application-owned interface for the GST accounting-aid use case, so another report feature - the
/// Bookkeeping BAS summary of issue #1318 - can reuse its authoritative result without depending on
/// the concrete <see cref="GetGstAccountingAid"/> class. It is the same precedent
/// <c>Inventory.Application.Reporting.Bookkeeping.IGetBookkeepingReport</c> established for this
/// use case's own dependency on bookkeeping.
/// </summary>
public interface IGetGstAccountingAid
{
    /// <summary>The aid report as the API and its export publish it.</summary>
    Task<GstAccountingAidDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken);

    /// <summary>
    /// The same run, with the figures and conditional notes a second projection needs beside the
    /// report. <see cref="Handle"/> is this method's report half, so the two can never calculate a
    /// period differently.
    /// </summary>
    Task<GstAccountingAidComposition> Compose(ReportingFilterDto filter, CancellationToken cancellationToken);
}
