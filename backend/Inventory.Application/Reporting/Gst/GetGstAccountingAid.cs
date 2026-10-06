using System.Globalization;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
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

        var notes = new List<string>();
        if (!purchases.IsComplete)
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{purchases.UnresolvedComponentCount} purchase component(s) totalling {purchases.UnresolvedAmount:0.00} have no GST classification; purchase input GST and net GST are incomplete."));
        if (isMachineFiltered)
            notes.Add("Purchase input GST is a whole-business figure and is not allocated to individual machines, so it is excluded from a machine-filtered report; net GST is incomplete.");

        var quality = Quality(
            missingStatus: true,
            historicalCostUnavailable: true,
            gstClassificationMissing: !facts.ImportedContainsGstClassification,
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
            PurchaseGstIncomplete = !purchases.IsComplete || isMachineFiltered
        };
    }
}
