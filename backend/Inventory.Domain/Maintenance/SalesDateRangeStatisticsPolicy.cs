namespace Inventory.Domain.Maintenance;

/// <summary>What <see cref="SalesDateRangeStatisticsPolicy"/> says to do about the sales date-range index's statistics.</summary>
public enum SalesDateRangeStatisticsDecision
{
    /// <summary>
    /// Collect them now. Either none are recorded for an index that is already worth describing, or
    /// the recorded ones are a different size from the table they describe.
    /// </summary>
    Collect,

    /// <summary>
    /// The recorded statistics still describe this table closely enough. Nothing is collected, and
    /// nothing takes the write lock.
    /// </summary>
    AlreadyCurrent,

    /// <summary>
    /// There is not enough sales history for statistics to change any plan, so none are collected.
    /// This is what a brand-new database answers.
    /// </summary>
    NotWorthCollecting,
}

/// <summary>
/// When this application collects SQLite planner statistics for the sales date-range index of issue
/// #1287 - <c>IX_NayaxSales_BusinessId_MachineAuthorizationTime</c> - and when it leaves them alone
/// (the repository owner's decision of 24 November 2026, "Go ahead with statistics collection for
/// #1381").
///
/// <para><strong>Why statistics are needed at all.</strong> The index is the right index, but the
/// money reports and dashboards do not name their business in SQL: the central tenant filter is
/// translated as <c>(@unrestricted OR "BusinessId" = @business)</c>, which constrains the index's
/// leading column not at all. SQLite can still search the index for them by <em>skip-scan</em>, and
/// it enables a skip-scan only for an index that has a <c>sqlite_stat1</c> row saying its leading
/// column holds many rows per distinct value. Without statistics the planner keeps choosing a full
/// scan of every sale the business has ever stored, which is the 145-226 ms audit #1226 measured.</para>
///
/// <para><strong>Every number is here and nowhere else</strong>, for the reason
/// <see cref="Inventory.Domain.Imports.NayaxSalesHistoryPolicy"/> states for the sales history
/// import's: a bound written where the statement happens to be issued is a bound the next reader
/// forgets. This type decides; the SQLite adapter behind
/// <c>Inventory.Application.Maintenance.ISalesDateRangeStatistics</c> reads the two numbers it
/// decides from and issues the one statement.</para>
///
/// <para><strong>It is deliberately a staleness rule and not a schedule.</strong> There is no timer,
/// no cron entry and no new scheduler: the two triggers are startup and the completion of a
/// background sales history import (issue #1304), and each asks this type whether to collect. What
/// bounds the work is therefore the data rather than the clock - see <see cref="GrowthFactor"/>.</para>
/// </summary>
public static class SalesDateRangeStatisticsPolicy
{
    /// <summary>
    /// How many stored sales there have to be before statistics are collected at all: 1,000.
    ///
    /// <para>Below this the question does not arise. SQLite only considers a skip-scan when a
    /// distinct leading-column value is worth more than about eighteen rows, and a table of a few
    /// hundred sales is read in a millisecond whichever plan wins - so collecting would take the
    /// write lock to buy nothing. It is also what makes a brand-new database's behaviour simple to
    /// state: an empty <c>NayaxSales</c> collects nothing and writes nothing, at every startup,
    /// until the business has actually traded.</para>
    /// </summary>
    public const long MinimumSales = 1_000;

    /// <summary>
    /// How far the table may drift from the recorded statistics before they are collected again: a
    /// factor of four, in either direction.
    ///
    /// <para><strong>This is what bounds the refresh, and it bounds it by the data rather than by
    /// time.</strong> A collection only happens when the table has quadrupled (or shrunk to a
    /// quarter) since the last one, so the number of collections over the whole life of a database is
    /// about <c>log₄(sales / 1,000)</c> - nine of them between the 1,000-row floor and 250 million
    /// sales. A business trickling in a few hundred sales a day never triggers one, and the 250,000
    /// rows one uploaded history may add trigger at most one.</para>
    ///
    /// <para>Four rather than two because what the plan depends on is an order of magnitude, not a
    /// row count: the recorded average rows per business is what enables the skip-scan, and a figure
    /// that is within a factor of four picks the same plan. Collecting more often would hold the
    /// write lock more often for the same plan.</para>
    ///
    /// <para>A plan already chosen cannot regress because statistics went stale as history grew:
    /// stale statistics understate how many sales a business holds, and the skip-scan is enabled by
    /// that figure being <em>large</em>. Growth therefore only ever strengthens the decision the
    /// recorded statistics already support, which is why a long-running process that never restarts
    /// is not a correctness problem.</para>
    /// </summary>
    public const int GrowthFactor = 4;

    /// <summary>
    /// Decides from the two numbers the store can read cheaply.
    /// </summary>
    /// <param name="recordedSales">
    /// How many rows the statistics recorded for the index say the table holds, or
    /// <see langword="null"/> when there are none. SQLite records no row for an empty index, so a
    /// recorded count of zero or less is read as "none" rather than as "none recorded yet, honestly
    /// zero".
    /// </param>
    /// <param name="currentSales">How many sales the table holds now, across every business.</param>
    public static SalesDateRangeStatisticsDecision Decide(long? recordedSales, long currentSales)
    {
        if (recordedSales is null or <= 0)
        {
            return currentSales >= MinimumSales
                ? SalesDateRangeStatisticsDecision.Collect
                : SalesDateRangeStatisticsDecision.NotWorthCollecting;
        }

        var recorded = recordedSales.Value;

        // Grown by the factor, or shrunk by it. Both are collected, because what is being kept is a
        // description of the table rather than a high-water mark.
        return currentSales >= recorded * GrowthFactor || recorded >= currentSales * GrowthFactor
            ? SalesDateRangeStatisticsDecision.Collect
            : SalesDateRangeStatisticsDecision.AlreadyCurrent;
    }
}
