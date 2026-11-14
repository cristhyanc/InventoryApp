using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Commissions;
using Inventory.Application.Time;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IDashboardReportFactsProvider"/>. It lives in
/// Inventory.Infrastructure for the same reason <see cref="EfBookkeepingReportFactsProvider"/> does:
/// it depends on <see cref="AppDbContext"/>, which moved here in issue #307 ahead of this adapter
/// family (issue #308, Persistence 7/8 of #153), and it composes the Application-owned
/// <see cref="IGetSiteCommissionReport"/> use case. Its completed-sale query and site-commission
/// resolution are shared with the other migrated report facts providers through
/// <see cref="EfReportingSharedQueries"/> rather than duplicated a further time; only the distinct
/// machine/product counts unique to the dashboard summary are computed locally, since no
/// already-migrated adapter needs that projection.
///
/// <para>It counted the period's completed transactions too until issue #804, which moved that
/// count onto <see cref="EfBookkeepingReportFactsProvider"/>'s own completed-sale read - the same
/// rows, already in memory there - so one definition feeds both reports. Issue #805 did the same
/// for its imported-reimbursement summary: that adapter already reads the identical
/// <see cref="EfReportingSharedQueries.ImportedSummaryAsync"/> result for the same period and
/// machine scope, so this one no longer queries it at all.</para>
/// </summary>
public sealed class EfDashboardReportFactsProvider : IDashboardReportFactsProvider
{
    private readonly AppDbContext _db;
    private readonly IGetSiteCommissionReport _siteCommissions;
    private readonly IBusinessCalendar _businessCalendar;

    public EfDashboardReportFactsProvider(
        AppDbContext db, IGetSiteCommissionReport siteCommissions, IBusinessCalendar businessCalendar)
    {
        _db = db;
        _siteCommissions = siteCommissions;
        _businessCalendar = businessCalendar;
    }

    public async Task<DashboardReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        // The requested dates are business dates, so the sales they select are the ones between the
        // business-day midnights those dates cover (issue #1250) - the same window the bookkeeping
        // report this summary composes selects its own figures over, so the machine and product
        // counts describe exactly the sales the amounts beside them are summed from.
        var window = EfReportingSharedQueries.SalesWindow(_businessCalendar, from, to);

        var summary = await EfReportingSharedQueries.SalesQuery(_db, window, machineId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Machines = g.Select(x => x.MachineID).Distinct().Count(),
                // Distinct *local* products the period's sales are attributed to (issue #758).
                // A sale with no association counts toward no product, as an unmatched one already
                // did; it stays visible in the reports that list sales.
                Products = g.Select(x => x.ProductId).Where(x => x.HasValue).Distinct().Count()
            })
            .SingleOrDefaultAsync(cancellationToken);

        var commissions = await EfReportingSharedQueries.GetMachineCommissionsAsync(
            _db, _siteCommissions, window, machineId, cancellationToken);

        return new DashboardReportFacts(
            MachineCount: summary?.Machines ?? 0,
            ProductCount: summary?.Products ?? 0,
            CommissionIsComplete: commissions.IsComplete,
            CommissionWarnings: commissions.Warnings);
    }
}
