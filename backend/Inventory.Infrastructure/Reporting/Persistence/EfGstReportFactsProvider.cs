using Inventory.Application.Reporting.Gst;
using Inventory.Domain.Purchases;
using Inventory.Domain.Reporting.Gst;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IGstReportFactsProvider"/>. It lives in
/// Inventory.Infrastructure for the same reason <see cref="EfBookkeepingReportFactsProvider"/> does:
/// it depends on <see cref="AppDbContext"/>, which moved here in issue #307 ahead of this adapter
/// family (issue #308, Persistence 7/8 of #153). It reuses the imported-summary query already shared
/// by the bookkeeping/daily/reconciliation/profitability adapters through
/// <see cref="EfReportingSharedQueries"/> rather than duplicating it.
///
/// Its purchase query (issue #432) projects the raw per-line and per-charge amounts and stored
/// classifications and calculates nothing: the rounding and the taxable/GST-free/unknown rules stay
/// in <see cref="PurchaseInputGstPolicy"/>/<c>PurchaseGstPolicy</c>. The date predicate is the same
/// inclusive-calendar-day range <see cref="EfBookkeepingReportFactsProvider"/> already applies to
/// <c>PurchaseDate</c> for its delivery/package totals, so both reports read the same purchases for
/// the same period. Reads go through the tenant query filters on <see cref="AppDbContext"/>; there
/// is no business predicate here, by design (AGENTS.md § Tenant ownership and data isolation).
/// </summary>
public sealed class EfGstReportFactsProvider : IGstReportFactsProvider
{
    private readonly AppDbContext _db;

    public EfGstReportFactsProvider(AppDbContext db)
    {
        _db = db;
    }

    public async Task<GstReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        var endExclusive = to.Date.AddDays(1);
        var imported = await EfReportingSharedQueries.ImportedSummaryAsync(_db, from, endExclusive, machineId, cancellationToken);
        var purchases = await PurchaseComponentsAsync(from, endExclusive, machineId, cancellationToken);
        return new GstReportFacts(imported.ContainsRows, imported.ContainsGstClassification, purchases);
    }

    /// <summary>
    /// The period's purchase GST components. A purchase is a whole-business record with no machine
    /// of its own, so a machine-filtered report reads none at all - the same exclusion
    /// <see cref="EfBookkeepingReportFactsProvider"/> applies to its delivery/package totals. The
    /// GST use case states that exclusion in the report's data quality rather than letting the
    /// filter change the totals silently.
    /// </summary>
    private async Task<IReadOnlyList<PurchaseGstComponents>> PurchaseComponentsAsync(
        DateTime from, DateTime endExclusive, long? machineId, CancellationToken cancellationToken)
    {
        if (machineId.HasValue)
            return [];

        var rows = await _db.Receipts.AsNoTracking()
            .Where(purchase => purchase.PurchaseDate >= from && purchase.PurchaseDate < endExclusive)
            .Select(purchase => new
            {
                purchase.DeliveryCost,
                purchase.DeliveryGstClassification,
                purchase.PackageCost,
                purchase.PackageGstClassification,
                Lines = purchase.Items
                    .Select(item => new { item.Quantity, item.UnitCost, item.GstClassification })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return rows.ConvertAll(purchase => new PurchaseGstComponents(
            purchase.Lines.ConvertAll(line => new PurchaseGstLine(line.Quantity, line.UnitCost, line.GstClassification)),
            new PurchaseGstCharge(purchase.DeliveryCost, purchase.DeliveryGstClassification),
            new PurchaseGstCharge(purchase.PackageCost, purchase.PackageGstClassification)));
    }
}
