using System.Globalization;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Tenancy;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.Gst;

namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// The GST and BAS summary Bookkeeping shows for a GST-registered business (issue #1318).
///
/// <para>It calculates no GST of its own. It decides three things: whether there is a summary to
/// show at all, which is the business's own GST registration; whether the requested scope can carry
/// one, which a machine filter cannot; and - through
/// <see cref="BasGstSummaryPolicy"/> - how the shared GST accounting-aid figures read under the BAS
/// labels G1, 1A and 1B. Every amount and every conditional note comes from
/// <see cref="IGetGstAccountingAid.Compose"/>, the one calculation the GST accounting aid report
/// itself is published from.</para>
/// </summary>
public sealed class GetGstBasSummary
{
    private readonly IGetGstAccountingAid _getGstAccountingAid;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IGstRegistrationStore _registration;

    public GetGstBasSummary(IGetGstAccountingAid getGstAccountingAid,
        ICurrentBusinessProvider currentBusiness, IGstRegistrationStore registration)
    {
        _getGstAccountingAid = getGstAccountingAid;
        _currentBusiness = currentBusiness;
        _registration = registration;
    }

    public async Task<GstBasSummaryDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var range = ReportingRangeResolver.Resolve(
            filter.From, filter.To, filter.StartDate, filter.EndDate, filter.FinancialYear);

        // Registration first, because a business that is not registered for GST has no BAS GST
        // section at all: it is not asked to widen its scope, it is simply not shown a summary. The
        // business comes from the caller's own membership, never from the filter.
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken).ConfigureAwait(false);
        var registered = await _registration.FindGstRegisteredAsync(businessId, cancellationToken).ConfigureAwait(false);
        if (registered != true)
            return Unavailable(range, GstBasSummaryStatus.NotGstRegistered);

        // A machine filter cannot carry this summary. Whole-business purchases and operating
        // expenses are not allocated to a machine - the exclusion the GST accounting aid already
        // applies - so a machine-scoped 1B would be a wrong credit total rather than a partial one.
        // Nothing is calculated for that scope; the client asks the reader to select all machines.
        if ((filter.MachineId ?? filter.MachineID).HasValue)
            return Unavailable(range, GstBasSummaryStatus.MachineFiltered);

        var composed = await _getGstAccountingAid.Compose(filter, cancellationToken).ConfigureAwait(false);
        var report = composed.Report;

        var summary = BasGstSummaryPolicy.Calculate(new BasGstSummaryInputs(
            TotalSalesIncludingGst: composed.TotalSalesIncludingGst,
            GstOnSales: report.GstOnSales,
            PurchaseInputGst: report.InventoryPurchaseGst,
            OperatingExpenseGst: report.OperatingExpenseGst,
            GstOnFees: report.GstOnFees,
            PurchaseGstIncomplete: report.PurchaseGstIncomplete,
            SalesIncomplete: !report.ProviderAvailability.Sales.IsComplete,
            FeesIncomplete: !report.ProviderAvailability.Fees.IsComplete,
            FeeGstIncludesEstimates: composed.FeeGstIncludesEstimates,
            HasSalesAssumedTaxable: report.NoGstRuleSales > 0m));

        // The shared calculation's own conditional notes, with the two this summary adds because it
        // is the surface that presents those figures as a BAS estimate.
        var notes = new List<string>(composed.ConditionalNotes);
        if (report.NoGstRuleSales > 0m)
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"GST on sales includes {report.NoGstRuleSales:0.00} of sales of products with no GST rule, which are assumed taxable. Setting those products' GST rules is what makes 1A final."));
        if (composed.FeeGstIncludesEstimates)
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{composed.EstimatedFeeGst:0.00} of the Nayax fee GST credit is estimated from the configured processing-fee rate for dates no settlement import covers."));

        return new GstBasSummaryDto(report.From, report.To, GstBasSummaryStatus.Available,
            new GstBasSummaryFiguresDto(
                TotalSales: summary.TotalSales,
                GstOnSales: summary.GstOnSales,
                GstCredits: summary.GstCredits,
                PurchaseGstCredits: summary.PurchaseGstCredits,
                OperatingExpenseGstCredits: summary.OperatingExpenseGstCredits,
                NayaxFeeGstCredits: summary.NayaxFeeGstCredits,
                NetGst: summary.NetGst,
                NetGstOutcome: summary.Outcome.ToString(),
                IsIncomplete: summary.IsIncomplete,
                GstFreeSales: report.GstFreeSales,
                NoGstRuleSales: report.NoGstRuleSales,
                PurchaseUnresolvedComponentCount: report.PurchaseUnresolvedComponentCount,
                PurchaseUnresolvedAmount: report.PurchaseUnresolvedAmount,
                EstimatedFeeGst: composed.EstimatedFeeGst,
                FeeGstIncludesEstimates: composed.FeeGstIncludesEstimates,
                ProviderAvailability: report.ProviderAvailability,
                NetGstAvailability: report.NetGstAvailability),
            Quality(report.DataQuality.GstClassificationMissing, notes));
    }

    /// <summary>
    /// A period with no summary: the resolved range, the reason, and deliberately no figures at all.
    /// A set of zeros here would read as a nil BAS, which is the one thing this report must never
    /// say about a period nobody calculated.
    /// </summary>
    private static GstBasSummaryDto Unavailable(DateRange range, string status) =>
        new(range.From, range.ToDate, status, null, Quality(gstClassificationMissing: false, notes: []));

    /// <summary>
    /// The summary's data-quality block, in the conditional form (issue #476): the notes are this
    /// period's own, and the standing disclaimers stay out of a BAS estimate.
    ///
    /// <para>Only <c>gstClassificationMissing</c> is derived from a fact this report holds - it is
    /// the GST accounting aid's own answer about imported reimbursement rows, passed through
    /// unchanged rather than re-decided. The other flags describe facts this summary does not
    /// report: transaction statuses and COGS belong to the Bookkeeping report beside it, commission
    /// is not a GST figure, and no product mapping feeds a BAS label. Claiming them here would say
    /// something about data this report never read.</para>
    /// </summary>
    private static ReportingDataQualityDto Quality(bool gstClassificationMissing, IReadOnlyList<string> notes) =>
        ReportingQuality.Conditional(
            missingStatus: false,
            historicalCostUnavailable: false,
            gstClassificationMissing: gstClassificationMissing,
            commissionNotPersisted: false,
            containsUnmappedProducts: false,
            notes: notes);
}
