using Inventory.Application.Reporting.Gst;
using Inventory.Infrastructure.Data;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IGstReportFactsProvider"/>. It lives in
/// Inventory.Infrastructure for the same reason <see cref="EfBookkeepingReportFactsProvider"/> does:
/// it depends on <see cref="AppDbContext"/>, which moved here in issue #307 ahead of this adapter
/// family (issue #308, Persistence 7/8 of #153). It reuses the imported-summary query already shared
/// by the bookkeeping/daily/reconciliation/profitability adapters through
/// <see cref="EfReportingSharedQueries"/> rather than duplicating it.
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
        return new GstReportFacts(imported.ContainsRows, imported.ContainsGstClassification);
    }
}
