using Inventory.Application.Maintenance;
using Inventory.Domain.Maintenance;
using Inventory.Infrastructure.Persistence;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Collects the SQLite planner statistics the sales date-range index needs, once per startup, right
/// after the schema step (issue #1287, the repository owner's decision of 24 November 2026 to go
/// ahead with statistics collection for pull request #1381).
///
/// <para><strong>Why startup is the trigger for the initial collection.</strong> The index arrives in
/// a migration, and <see cref="DatabaseSchemaStartup"/> is what applies it - so the first moment the
/// index exists on a deployed database is a startup, and this runs immediately after it on the same
/// scope. Without statistics the money reports and dashboards keep scanning every sale the business
/// has ever stored, because their SQL does not name the business and SQLite enables the skip-scan that
/// reaches this index only for an index that has statistics. One startup is therefore the whole of
/// what the deployed improvement needs.</para>
///
/// <para><strong>What it costs at every other startup: one count of rows.</strong> Whether to collect
/// is <see cref="SalesDateRangeStatisticsPolicy"/>'s decision, not this step's, and it is a staleness
/// rule rather than a schedule: statistics are collected when there are none for a table that holds
/// at least <see cref="SalesDateRangeStatisticsPolicy.MinimumSales"/> sales, or when the table has
/// drifted by <see cref="SalesDateRangeStatisticsPolicy.GrowthFactor"/> from what the recorded
/// statistics describe. So a restart of a database whose statistics are current reads one
/// <c>COUNT(*)</c> and takes no write lock at all, and a brand-new database with no sales collects
/// nothing - at this startup and at every later one, until the business has actually traded.</para>
///
/// <para><strong>It cannot stop the application from starting.</strong> Statistics are an
/// optimisation: a slower plan is not a reason to refuse to serve requests, which is the opposite of
/// how this file treats a schema the code does not match. The port never throws for a database
/// failure - a write lock held past the busy timeout, most often - and reports it instead, so the
/// worst case here is a logged warning and a slower report until the next startup or the next finished
/// sales history import tries again. Nothing is retried in a loop and nothing is scheduled.</para>
///
/// <para><strong>The other trigger is not here.</strong> A finished background sales history import
/// (issue #1304) asks the same port the same question, because that is the one path that can add a
/// quarter of a million sales between two restarts; see
/// <c>Inventory.Application.Imports.ProcessNayaxSalesHistoryImportJob</c>. Sales arriving one at a
/// time from the Nayax queue reader deliberately do <em>not</em> ask: that would be a count of the
/// sales table per message, and growth alone cannot make the planner stop using the index (stale
/// statistics understate how many sales a business holds, and it is that figure being large which
/// enables the skip-scan), so the next startup is soon enough.</para>
/// </summary>
public static class SalesDateRangeStatisticsStartup
{
    /// <summary>
    /// Asks the port whether the statistics need collecting, and reports what it answered.
    /// </summary>
    public static async Task CollectAsync(
        ISalesDateRangeStatistics statistics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger(typeof(SalesDateRangeStatisticsStartup).FullName!);

        var result = await statistics.CollectIfStaleAsync(cancellationToken);

        switch (result.Outcome)
        {
            case SalesDateRangeStatisticsOutcome.Collected:
                logger.LogInformation(
                    "Collected SQLite planner statistics for {Index} over {CurrentSales} stored "
                        + "sale(s) in {ElapsedMs} ms (the statistics it replaced described "
                        + "{RecordedSales}). The sales date-range reads can use the index; the next "
                        + "collection happens when the table has grown or shrunk by a factor of "
                        + "{GrowthFactor}.",
                    SqliteSalesDateRangeStatistics.IndexName,
                    result.CurrentSales,
                    (long)result.Duration.TotalMilliseconds,
                    result.RecordedSales,
                    SalesDateRangeStatisticsPolicy.GrowthFactor);
                return;

            case SalesDateRangeStatisticsOutcome.AlreadyCurrent:
                logger.LogInformation(
                    "SQLite planner statistics for {Index} already describe the {CurrentSales} "
                        + "stored sale(s); startup collected none and took no write lock.",
                    SqliteSalesDateRangeStatistics.IndexName,
                    result.CurrentSales);
                return;

            case SalesDateRangeStatisticsOutcome.NotWorthCollecting:
                logger.LogInformation(
                    "There are {CurrentSales} stored sale(s), fewer than the {MinimumSales} at "
                        + "which SQLite planner statistics change any sales date-range plan, so "
                        + "startup collected none.",
                    result.CurrentSales,
                    SalesDateRangeStatisticsPolicy.MinimumSales);
                return;

            default:
                logger.LogWarning(
                    "Could not collect SQLite planner statistics for {Index} at startup "
                        + "({FailureType}); the application starts normally and the sales date-range "
                        + "reports may use a slower plan until the next startup, or the next finished "
                        + "sales history import, collects them.",
                    SqliteSalesDateRangeStatistics.IndexName,
                    result.FailureType);
                return;
        }
    }
}
