namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// One run of the shared GST calculation for a period (issue #1318).
///
/// <para>The GST accounting aid report and the Bookkeeping BAS summary are two projections of this
/// one result, so "reuse the GST Accounting Aid calculation and data-quality rules through a shared
/// backend path" is a fact about the code rather than a convention: both surfaces take their figures
/// and their conditional notes from here, and neither derives a GST amount of its own.</para>
/// </summary>
/// <param name="Report">
/// The aid report exactly as <c>GET api/reports/gst</c> and its CSV/XLSX export publish it.
/// </param>
/// <param name="ConditionalNotes">
/// The notes this period actually earned - unresolved purchase components, the machine-filter
/// exclusion, and each non-complete provider family - without the standing disclaimers the aid's own
/// <c>DataQuality</c> prepends. A surface that reports conditionally (issue #476) takes these.
/// </param>
/// <param name="TotalSalesIncludingGst">
/// The period's whole GST-inclusive sales, GST-free sales included: BAS label G1. The aid's own
/// contract does not publish it, because the aid shows the taxable part instead.
/// </param>
/// <param name="GstCredits">
/// The eligible GST credits <see cref="Report"/>'s net GST subtracted, from the Domain policy that
/// subtracted them: BAS label 1B.
/// </param>
/// <param name="EstimatedFeeGst">
/// How much of the period's fee GST was estimated from the configured rate rather than taken from an
/// imported settlement.
/// </param>
/// <param name="FeeGstIncludesEstimates">
/// Whether any card transaction in the period had its fee - and therefore its fee GST - estimated.
/// It is the fee result's own answer, which is about transactions rather than about an amount: an
/// estimate of <c>$0.00</c> is still an estimate.
/// </param>
public sealed record GstAccountingAidComposition(
    GstAccountingAidDto Report,
    IReadOnlyList<string> ConditionalNotes,
    decimal TotalSalesIncludingGst,
    decimal GstCredits,
    decimal EstimatedFeeGst,
    bool FeeGstIncludesEstimates);
