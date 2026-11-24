using System.Diagnostics;
using System.Globalization;
using Inventory.Application.Maintenance;
using Inventory.Domain.Maintenance;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// Collects the SQLite planner statistics the sales date-range index of issue #1287 needs, under the
/// repository owner's decision of 24 November 2026 ("Go ahead with statistics collection for
/// #1381").
///
/// <para><strong>The whole of the collection is one statement over one named index:</strong>
/// <c>ANALYZE "IX_NayaxSales_BusinessId_MachineAuthorizationTime"</c>. That is the narrowest scope
/// SQLite offers - narrower than <c>ANALYZE NayaxSales</c>, which would also describe that table's
/// three other indexes, and far narrower than a bare <c>ANALYZE</c>, which would describe every index
/// of every table in the database and change plan selection across the whole schema. What it writes
/// is one <c>sqlite_stat1</c> row.</para>
///
/// <para><strong>What that one row changes, measured rather than assumed</strong>
/// (<c>NayaxSalesDateRangeIndexMigrationTests</c>). SQLite enables a <em>skip-scan</em> only for an
/// index that carries statistics saying its leading column is worth more than about eighteen rows per
/// distinct value, which is exactly what the reports and dashboards need: their SQL does not name the
/// business, because the central tenant filter is translated as
/// <c>(@unrestricted OR "BusinessId" = @business)</c>, so the index's leading column is unconstrained
/// and the skip-scan is the only way in. The one wider effect, stated because it is real: loading an
/// index's statistics also sets SQLite's row estimate for the <em>table</em> that index is on, so
/// every query over <c>NayaxSales</c> now costs its alternatives against a true row count instead of
/// SQLite's built-in guess of about a million. No other table is described at all, and the three
/// other <c>NayaxSales</c> indexes gain no statistics and therefore no skip-scan of their own.</para>
///
/// <para><strong>It reads and writes nothing about any business.</strong> All three statements are
/// raw SQL on the context's own connection rather than queries over a <c>DbSet</c>, and that is the
/// point rather than a shortcut: no entity, no query filter and no tenant column is involved anywhere
/// on this path. The first asks whether <c>sqlite_stat1</c> exists, the second reads the one recorded
/// figure for this one index, the third counts the rows of one table - two numbers, each a count of
/// rows, and nothing that identifies a business, a machine, a product, a sale or a figure. It is also
/// not reachable from a request: the two callers are API startup and the completion of a background
/// sales history import (AGENTS.md § Tenant ownership and data isolation, which makes raw SQL a
/// boundary violation <em>in a request path</em>).</para>
///
/// <para><strong>Collecting cannot change a row or a figure.</strong> <c>ANALYZE</c> reads index
/// keys and writes aggregate counts; it does not touch a business's rows, and SQLite's query results
/// do not depend on which plan was chosen to produce them. The relational tests assert that directly -
/// both businesses' sales, their totals and the money-report facts are identical either side of a
/// collection.</para>
///
/// <para><strong>The write lock.</strong> <c>ANALYZE</c> is a write, so it takes SQLite's single
/// write lock for as long as it takes to scan the index and replace one row: measured at 0.10 s
/// initially and 0.11 s for a refresh on the synthetic 1,000,000-sale database (500,000 for each of
/// two businesses) this repository benchmarks against, beside 0.80 s for building the index itself
/// (<c>NayaxSalesDateRangeIndexSizeBenchmark</c>). Another writer meeting it waits on the ordinary
/// 30-second busy timeout, which is why both callers run it outside any transaction of their own and
/// why a timeout is reported rather than raised. Deciding <em>not</em> to collect takes no write lock
/// at all: it is one <c>COUNT(*)</c> and one row of <c>sqlite_stat1</c>, under 10 ms at that
/// size.</para>
/// </summary>
public sealed class SqliteSalesDateRangeStatistics : ISalesDateRangeStatistics
{
    /// <summary>
    /// The one index this describes - the index migration
    /// <c>20261123120000_AddNayaxSalesDateRangeIndex</c> creates and
    /// <c>AppDbContext</c> maps. It is a compile-time constant and never anything a caller supplied,
    /// which is what makes it safe to name in a statement SQLite will not let be parameterised.
    /// </summary>
    public const string IndexName = "IX_NayaxSales_BusinessId_MachineAuthorizationTime";

    private readonly AppDbContext _db;

    public SqliteSalesDateRangeStatistics(AppDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<SalesDateRangeStatisticsResult> CollectIfStaleAsync(
        CancellationToken cancellationToken)
    {
        long? recorded;
        long current;

        try
        {
            recorded = await RecordedSalesAsync(cancellationToken);
            current = await CurrentSalesAsync(cancellationToken);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            return Unavailable(exception);
        }

        var decision = SalesDateRangeStatisticsPolicy.Decide(recorded, current);

        if (decision != SalesDateRangeStatisticsDecision.Collect)
        {
            return new SalesDateRangeStatisticsResult(
                decision == SalesDateRangeStatisticsDecision.AlreadyCurrent
                    ? SalesDateRangeStatisticsOutcome.AlreadyCurrent
                    : SalesDateRangeStatisticsOutcome.NotWorthCollecting,
                recorded,
                current);
        }

        var started = Stopwatch.StartNew();

        try
        {
            // The one statement. Not parameterised because SQLite's ANALYZE takes a schema object
            // name rather than a value, and the name is the constant above.
            await _db.Database.ExecuteSqlRawAsync(
                $"ANALYZE \"{IndexName}\";", cancellationToken);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            return Unavailable(exception) with { RecordedSales = recorded, CurrentSales = current };
        }

        return new SalesDateRangeStatisticsResult(
            SalesDateRangeStatisticsOutcome.Collected, recorded, current, started.Elapsed);
    }

    /// <summary>
    /// How many rows the recorded statistics say this index covers, or <see langword="null"/> when
    /// there are none.
    ///
    /// <para><c>sqlite_stat1</c> does not exist until something has analysed something, which is why
    /// its existence is asked first rather than caught: a statement naming a table that is not there
    /// fails when it is prepared. Each row's <c>stat</c> column is a space-separated list whose first
    /// field is the row count - "<c>1000000 500000 1</c>" for this index on the benchmark database -
    /// so the first field is the whole of what is read. Anything unparseable is read as "no
    /// statistics", which collects rather than trusts it.</para>
    /// </summary>
    private async Task<long?> RecordedSalesAsync(CancellationToken cancellationToken)
    {
        var statisticsExist = await _db.Database
            .SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM sqlite_master "
                + "WHERE type = 'table' AND name = 'sqlite_stat1'")
            .SingleAsync(cancellationToken);

        if (statisticsExist == 0) return null;

        var stat = await _db.Database
            .SqlQueryRaw<string?>(
                "SELECT stat AS Value FROM sqlite_stat1 WHERE idx = {0}", IndexName)
            .FirstOrDefaultAsync(cancellationToken);

        var rows = stat?.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return long.TryParse(rows, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? count
            : null;
    }

    /// <summary>
    /// How many sales the table holds now, across every business.
    ///
    /// <para>It is a count of rows and nothing else: no business column is named, selected or
    /// returned. SQLite answers it from the narrowest index it has, so it is one index scan - tens of
    /// milliseconds at the benchmark's million rows - and it is made at most twice in the life of a
    /// process (once at startup, once more when a background history import finishes), never on a
    /// request.</para>
    /// </summary>
    private async Task<long> CurrentSalesAsync(CancellationToken cancellationToken) =>
        await _db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM \"NayaxSales\"")
            .SingleAsync(cancellationToken);

    /// <summary>
    /// The type name only, never the message: a SQLite message can quote the parameter values of the
    /// statement that failed.
    /// </summary>
    private static SalesDateRangeStatisticsResult Unavailable(Exception exception) =>
        new(SalesDateRangeStatisticsOutcome.Unavailable, FailureType: exception.GetType().Name);

    private static bool NotCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
}
