using Inventory.Application.Reporting.Gst;
using InventoryApi.Data;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IGstReportFactsProvider"/>. It lives in
/// InventoryApi, not Inventory.Infrastructure, for the same reason <see cref="EfBookkeepingReportFactsProvider"/>
/// does: it depends on <see cref="AppDbContext"/>, which still lives in InventoryApi. It reuses the
/// imported-summary query already shared by the bookkeeping/daily/reconciliation/profitability
/// adapters through <see cref="EfReportingSharedQueries"/> rather than duplicating it.
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
