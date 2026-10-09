using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Commissions;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IDashboardReportFactsProvider"/>. It lives in
/// Inventory.Infrastructure for the same reason <see cref="EfBookkeepingReportFactsProvider"/> does:
/// it depends on <see cref="AppDbContext"/>, which moved here in issue #307 ahead of this adapter
/// family (issue #308, Persistence 7/8 of #153), and it composes the Application-owned
/// <see cref="IGetSiteCommissionReport"/> use case. Its completed-sale query,
/// imported-reimbursement summary, and site-commission resolution are shared with the other
/// migrated report facts providers through <see cref="EfReportingSharedQueries"/> rather than
/// duplicated a further time; only the distinct machine/product counts unique to the dashboard
/// summary are computed locally, since no already-migrated adapter needs that projection.
/// </summary>
public sealed class EfDashboardReportFactsProvider : IDashboardReportFactsProvider
{
    private readonly AppDbContext _db;
    private readonly IGetSiteCommissionReport _siteCommissions;

    public EfDashboardReportFactsProvider(AppDbContext db, IGetSiteCommissionReport siteCommissions)
    {
        _db = db;
        _siteCommissions = siteCommissions;
    }

    public async Task<DashboardReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        var endExclusive = to.Date.AddDays(1);

        var summary = await EfReportingSharedQueries.SalesQuery(_db, from, endExclusive, machineId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Transactions = g.Count(),
                Machines = g.Select(x => x.MachineID).Distinct().Count(),
                Products = g.Select(x => x.NayaxProductId).Where(x => x.HasValue).Distinct().Count()
            })
            .SingleOrDefaultAsync(cancellationToken);

        var imported = await EfReportingSharedQueries.ImportedSummaryAsync(_db, from, endExclusive, machineId, cancellationToken);
        var commissions = await EfReportingSharedQueries.GetMachineCommissionsAsync(
            _db, _siteCommissions, from, to, endExclusive, machineId, cancellationToken);

        return new DashboardReportFacts(
            TransactionCount: summary?.Transactions ?? 0,
            MachineCount: summary?.Machines ?? 0,
            ProductCount: summary?.Products ?? 0,
            ImportedContainsRows: imported.ContainsRows,
            ImportedNetSettlement: imported.NetSettlement,
            CommissionIsComplete: commissions.IsComplete,
            CommissionWarnings: commissions.Warnings);
    }
}
