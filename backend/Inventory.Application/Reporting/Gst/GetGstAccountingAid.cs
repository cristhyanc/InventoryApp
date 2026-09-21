using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Gst;
using static Inventory.Application.Reporting.Shared.ReportingQuality;

namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// The GST accounting-aid report use case: reuses the already-migrated bookkeeping report's
/// GST-on-sales/GST-on-fees figures through <see cref="IGetBookkeepingReport"/>, retrieves the
/// imported-summary data-quality facts this report still needs through the narrow
/// <see cref="IGstReportFactsProvider"/> port, applies the Domain GST-derivation policy, and builds
/// the authoritative <see cref="GstAccountingAidDto"/> consumed by both the API response and the
/// CSV/XLSX export.
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
        var facts = await _facts.GetFactsAsync(bookkeeping.From, bookkeeping.To, machineId, cancellationToken);

        var derived = GstAccountingAidPolicy.Calculate(new GstAccountingAidInputs(
            Sales: bookkeeping.Sales,
            GstOnSales: bookkeeping.GstOnSales,
            NayaxFeesExGst: bookkeeping.NayaxFeesExGst,
            GstOnFees: bookkeeping.GstOnFees,
            OperatingExpenseGst: bookkeeping.OperatingExpenseGst));

        var quality = Quality(facts.ImportedContainsRows, facts.ImportedContainsGstClassification, false);

        return new GstAccountingAidDto(bookkeeping.From, bookkeeping.To, derived.TaxableSales,
            bookkeeping.GstOnSales, derived.TaxableFees, bookkeeping.GstOnFees, derived.NetGst, quality)
        {
            OperatingExpenseGst = bookkeeping.OperatingExpenseGst
        };
    }
}
