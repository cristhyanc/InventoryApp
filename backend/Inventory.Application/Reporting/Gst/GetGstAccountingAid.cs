using System.Globalization;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;
using Inventory.Domain.Reporting.Gst;
using static Inventory.Application.Reporting.Shared.ReportingQuality;

namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// The GST accounting-aid report use case: reuses the already-migrated bookkeeping report's
/// GST-on-sales/GST-on-fees figures through <see cref="IGetBookkeepingReport"/>, retrieves the
/// imported-summary data-quality facts and the period's purchase GST components through the narrow
/// <see cref="IGstReportFactsProvider"/> port, applies the Domain GST-derivation policies, and
/// builds the authoritative <see cref="GstAccountingAidDto"/> consumed by both the API response and
/// the CSV/XLSX export.
///
/// Purchase input GST (issue #432, parent issue #62 decision D1) is calculated by
/// <see cref="PurchaseInputGstPolicy"/>, which delegates every amount and rounding decision to
/// <c>PurchaseGstPolicy</c>. Only resolved components reduce net GST; unclassified ones are shown
/// as an unresolved count and amount beside a clear incomplete status, so an incomplete period is
/// never presented as a finished BAS figure.
/// </summary>
public sealed class GetGstAccountingAid
{
    private readonly IGetBookkeepingReport _getBookkeepingReport;
    private readonly IGstReportFactsProvider _facts;

    public GetGstAccountingAid(IGetBookkeepingReport getBookkeepingReport, IGstReportFactsProvider facts)
    {
        _getBookkeepingReport = getBookkeepingReport;
        _facts = facts;
    }

    public async Task<GstAccountingAidDto> Handle(ReportingFilterDto filter, CancellationToken cancellationToken)
    {
        var bookkeeping = await _getBookkeepingReport.Handle(filter, cancellationToken);
        var machineId = filter.MachineId ?? filter.MachineID;
        var isMachineFiltered = machineId.HasValue;
        var facts = await _facts.GetFactsAsync(bookkeeping.From, bookkeeping.To, machineId, cancellationToken);

        var purchases = PurchaseInputGstPolicy.Calculate(facts.Purchases);

        var derived = GstAccountingAidPolicy.Calculate(new GstAccountingAidInputs(
            Sales: bookkeeping.Sales,
            GstOnSales: bookkeeping.GstOnSales,
            NayaxFeesExGst: bookkeeping.NayaxFeesExGst,
            GstOnFees: bookkeeping.GstOnFees,
            OperatingExpenseGst: bookkeeping.OperatingExpenseGst,
            PurchaseInputGst: purchases.TotalGst));

        // The bookkeeping report already decided how well recorded provider coverage accounts for
        // this period (issue #730); this report shows the same two figure families, so it reports
        // the same states rather than deciding them a second time.
        var availability = bookkeeping.ProviderAvailability;

        var notes = new List<string>();
        if (!purchases.IsComplete)
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{purchases.UnresolvedComponentCount} purchase component(s) totalling {purchases.UnresolvedAmount:0.00} have no GST classification; purchase input GST and net GST are incomplete."));
        if (isMachineFiltered)
            notes.Add("Purchase input GST is a whole-business figure and is not allocated to individual machines, so it is excluded from a machine-filtered report; net GST is incomplete.");
        AddAvailabilityNote(notes, availability.Sales, "Taxable sales and GST on sales");
        AddAvailabilityNote(notes, availability.Fees, "Taxable Nayax fees and the GST on them");

        var quality = Quality(
            missingStatus: true,
            historicalCostUnavailable: true,
            // Only imported reimbursement rows can be missing a GST classification (issue #730). A
            // period with no settlement import at all has no such rows, so warning about them said
            // nothing about this business's data and hid the real answer - that the fee figures are
            // unavailable, which the fee availability state above now reports.
            gstClassificationMissing: facts.ImportedContainsRows && !facts.ImportedContainsGstClassification,
            commissionNotPersisted: true,
            containsUnmappedProducts: false,
            notes: notes);

        return new GstAccountingAidDto(bookkeeping.From, bookkeeping.To, derived.TaxableSales,
            bookkeeping.GstOnSales, derived.TaxableFees, bookkeeping.GstOnFees, derived.NetGst, quality,
            InventoryPurchaseGst: purchases.TotalGst)
        {
            OperatingExpenseGst = bookkeeping.OperatingExpenseGst,
            PurchaseLineGst = purchases.LineGst,
            PurchaseChargeGst = purchases.ChargeGst,
            PurchaseUnresolvedComponentCount = purchases.UnresolvedComponentCount,
            PurchaseUnresolvedAmount = purchases.UnresolvedAmount,
            PurchaseGstIncomplete = !purchases.IsComplete || isMachineFiltered,
            ProviderAvailability = availability,
            // Net GST depends on both families, so it carries the combined state the bookkeeping
            // report already derived from them through the Domain policy - never a second
            // combination written here.
            NetGstAvailability = availability.Combined
        };
    }

    /// <summary>
    /// The GST report's own wording of a non-complete figure family, named for the figures this
    /// report actually shows. A complete family adds nothing.
    /// </summary>
    private static void AddAvailabilityNote(List<string> notes, ReportAvailabilityDto reported, string figures)
    {
        var availability = reported.ToDomain();
        switch (availability.State)
        {
            case ReportAvailabilityState.Missing:
                notes.Add($"{figures} are unavailable for this period: no provider data is recorded for it and no synchronization or import covered it. The amounts shown are not a measured zero.");
                break;
            case ReportAvailabilityState.Stale:
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{figures} are accounted for only as of {availability.AsOfUtc:yyyy-MM-dd HH:mm} UTC; net GST for this period is incomplete."));
                break;
            case ReportAvailabilityState.Unverified:
                notes.Add($"{figures} are not accounted for across the whole period: the data predates coverage recording, or a failed or partial run left a gap. Net GST for this period is incomplete.");
                break;
            case ReportAvailabilityState.Complete:
            default:
                break;
        }
    }
}
