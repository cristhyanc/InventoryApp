using System.Data.Common;
using Inventory.Application.Machines;
using Inventory.Application.Maintenance;
using Inventory.Domain.Maintenance;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Xunit.Abstractions;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The sales date-range index of issue #1287 (audit #1226, finding DB-02) and the planner statistics
/// the repository owner authorised for it on 24 November 2026, on a real SQLite <em>file</em> rather
/// than an in-memory database, because what is being proved is relational: which index SQLite's own
/// planner chooses for the SQL EF Core generates, and that applying the migration and collecting
/// statistics over a populated database keeps every business's rows and every figure computed from
/// them.
///
/// <para>About fourteen queries in <c>Inventory.Infrastructure</c> - the dashboards, the money
/// reports and reconciliation - select sales by a <c>MachineAuthorizationTime</c> range. Until this
/// index they had no index holding that instant, so each of them read every sale the business had
/// ever stored and then filtered: 145-226 ms for a one-day or one-month total on the audit's
/// synthetic 500,000-sales-per-business database, growing with every year of trading.</para>
///
/// <para>The plan tests run <c>EXPLAIN QUERY PLAN</c> over the statements the real adapters issue -
/// captured from the <see cref="DbCommand"/> EF executes, parameters included, not over a query shape
/// retyped here - so a change to those queries that changes which index they reach fails this test
/// instead of passing unnoticed.</para>
///
/// <para><strong>Why statistics are part of this at all.</strong> The money reports and dashboards do
/// not name their business in SQL: the central tenant filter is translated as
/// <c>(@unrestricted OR "BusinessId" = @business)</c>, which constrains the index's leading column not
/// at all - the same <c>OR</c> that made issue #1304 index <c>ProductId</c> on its own. SQLite reaches
/// the index for them by <em>skip-scan</em>, and it enables a skip-scan only for an index that carries
/// planner statistics. So the index alone bought those reads nothing on a deployed database, which is
/// what the owner's decision resolves: the application now collects them itself, through
/// <see cref="SqliteSalesDateRangeStatistics"/>, bounded by
/// <see cref="SalesDateRangeStatisticsPolicy"/>. <strong>No test here runs <c>ANALYZE</c>:</strong>
/// every statistic in these cases is collected by the same code path startup and a finished sales
/// history import use.</para>
/// </summary>
public sealed class NayaxSalesDateRangeIndexMigrationTests : IDisposable
{
    private const string PreviousMigration = "AddNayaxSalesProductIdIndex";
    private const string IndexMigration = "AddNayaxSalesDateRangeIndex";
    private const string IndexName = SqliteSalesDateRangeStatistics.IndexName;

    private const int BusinessAId = 1;
    private const int BusinessBId = 2;

    /// <summary>
    /// Enough sales per business for collected statistics to record a business as holding far more
    /// than the eighteen rows SQLite wants before it will skip an index's leading column, comfortably
    /// over <see cref="SalesDateRangeStatisticsPolicy.MinimumSales"/>, and few enough to seed and
    /// plan in well under a second.
    /// </summary>
    private const int SeededSalesPerBusiness = 4_000;

    /// <summary>The reported business dates, and the instants the trivial calendar maps them to.</summary>
    private static readonly DateTime FirstBusinessDate = new(2026, 6, 1);
    private static readonly DateTime LastBusinessDate = new(2026, 6, 30);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-sales-date-index-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;
    private readonly List<CapturedStatement> _statements = [];
    private readonly ITestOutputHelper _output;

    /// <summary>How many synthetic sales each business has been seeded, so growth continues from there.</summary>
    private readonly Dictionary<int, int> _seeded = [];

    public NayaxSalesDateRangeIndexMigrationTests(ITestOutputHelper output)
    {
        _output = output;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
    }

    /// <summary>
    /// The upgrade this release applies: a database at the previous migration already holding sales
    /// for two businesses gains the index, keeps every row, and - because <c>Down</c> works - can be
    /// taken back to the previous migration with its rows intact.
    /// </summary>
    [Fact]
    public async Task The_migration_adds_the_index_keeps_both_businesses_sales_and_rolls_back_cleanly()
    {
        await using (var before = TestAppDbContext.Unrestricted(Options()))
            await MigrateToAsync(before, PreviousMigration);

        Assert.Null(await IndexSqlAsync());
        await SeedTwoBusinessesWithSalesAsync();

        await using (var upgraded = TestAppDbContext.Unrestricted(Options()))
            await MigrateToAsync(upgraded, IndexMigration);

        var indexSql = await IndexSqlAsync();
        Assert.NotNull(indexSql);
        // The business column leads, so one business's window is a contiguous range of the index and
        // no part of it describes another business's sales (AGENTS.md § Tenant ownership and data
        // isolation).
        Assert.Contains("(\"BusinessId\", \"MachineAuthorizationTime\")", indexSql, StringComparison.Ordinal);
        Assert.Equal(3L, await ScalarAsync($"SELECT COUNT(*) FROM NayaxSales WHERE BusinessId = {BusinessAId}"));
        Assert.Equal(2L, await ScalarAsync($"SELECT COUNT(*) FROM NayaxSales WHERE BusinessId = {BusinessBId}"));

        await using (var rolledBack = TestAppDbContext.Unrestricted(Options()))
            await MigrateToAsync(rolledBack, PreviousMigration);

        Assert.Null(await IndexSqlAsync());
        Assert.Equal(3L, await ScalarAsync($"SELECT COUNT(*) FROM NayaxSales WHERE BusinessId = {BusinessAId}"));
        Assert.Equal(2L, await ScalarAsync($"SELECT COUNT(*) FROM NayaxSales WHERE BusinessId = {BusinessBId}"));
    }

    /// <summary>
    /// The index is the right shape for the read it was added for: a business's sales between two
    /// instants, with the business named, is a search of the index over exactly that business's slice
    /// of it - which is the query audit #1226 timed at 0-7 ms against 145-226 ms without the index.
    /// This one needs no statistics at all, which is why it is asserted separately.
    /// </summary>
    [Fact]
    public async Task A_business_scoped_sales_date_range_searches_the_index()
    {
        await MigrateAndSeedAsync();

        var plan = await PlanAsync(new CapturedStatement(
            """
            SELECT SUM("SettlementValue") FROM "NayaxSales"
            WHERE "BusinessId" = @business
              AND "MachineAuthorizationTime" >= @from
              AND "MachineAuthorizationTime" < @endExclusive
            """,
            [
                new("@business", BusinessAId),
                new("@from", FirstBusinessDate),
                new("@endExclusive", LastBusinessDate.AddDays(1)),
            ]));

        Report("a business-scoped sales date range", plan);

        Assert.True(
            plan.Exists(line => line.Contains(IndexName, StringComparison.Ordinal)),
            $"Expected {IndexName} in the plan: {string.Join(" | ", plan)}");
        Assert.True(
            plan.Exists(line => line.StartsWith("SEARCH", StringComparison.Ordinal)),
            $"Expected a search rather than a scan: {string.Join(" | ", plan)}");
    }

    /// <summary>
    /// <strong>Criterion 2 of issue #1287, through the real collection path.</strong> A populated
    /// database with no statistics at all - the state every database this repository has ever created
    /// is in - has them collected by <see cref="SqliteSalesDateRangeStatistics"/>, the one adapter
    /// startup and a finished sales history import use, and the two date-range reads the criterion
    /// names then search the index: the shared business-date selection of issue #1250, here through
    /// the reconciliation report, and one other dashboard query, the Dashboard summary card's period
    /// totals.
    ///
    /// <para>Nothing in this test runs <c>ANALYZE</c>, and nothing changes a query, a tenant
    /// predicate or an index: the only thing between the two sets of plans is the adapter's own call.
    /// The plans before it are reported rather than asserted on, so that the previous plan being a
    /// scan is evidence in the output rather than a requirement this test would defend.</para>
    /// </summary>
    [Fact]
    public async Task The_report_and_dashboard_reads_search_the_index_once_the_application_collects_statistics()
    {
        await MigrateAsync();
        await SeedBusinessWithManySalesAsync(BusinessAId);
        await SeedBusinessWithManySalesAsync(BusinessBId);

        // The starting point the criterion asks for: a populated file with no statistics whatsoever.
        Assert.False(await StatisticsTableExistsAsync());
        Assert.Null(await RecordedStatisticsAsync());

        await ReportSalesRangePlansAsync("before the application collected statistics", assert: false);

        var collected = await CollectAsync();

        Assert.Equal(SalesDateRangeStatisticsOutcome.Collected, collected.Outcome);
        Assert.Null(collected.RecordedSales);
        Assert.Equal(SeededSalesPerBusiness * 2L, collected.CurrentSales);
        Assert.NotNull(await RecordedStatisticsAsync());
        _output.WriteLine(
            $"collected in {collected.Duration.TotalMilliseconds:F0} ms; "
            + $"sqlite_stat1 for the index now says: {await RecordedStatisticsAsync()}");

        await ReportSalesRangePlansAsync("after the application collected statistics", assert: true);
    }

    /// <summary>
    /// The bounded refresh policy, exercised as history grows (issue #1287's refresh criterion).
    ///
    /// <para>Three things are asserted, which together are the bound: a second call straight after a
    /// collection collects nothing and takes no write lock; growing the table by less than
    /// <see cref="SalesDateRangeStatisticsPolicy.GrowthFactor"/> still collects nothing; and growing
    /// it past that factor collects again, recording the table as it now is. So the number of
    /// collections over the life of a database is about <c>log₄(sales)</c> rather than one per
    /// import, per request or per restart.</para>
    /// </summary>
    [Fact]
    public async Task The_refresh_collects_again_only_once_sales_history_has_grown_by_the_bounded_factor()
    {
        await MigrateAsync();
        await SeedBusinessWithManySalesAsync(BusinessAId);
        await SeedBusinessWithManySalesAsync(BusinessBId);

        var first = await CollectAsync();
        Assert.Equal(SalesDateRangeStatisticsOutcome.Collected, first.Outcome);

        // Asked again with nothing changed: no statement that takes the write lock.
        var again = await CollectAsync();
        Assert.Equal(SalesDateRangeStatisticsOutcome.AlreadyCurrent, again.Outcome);
        Assert.Equal(SeededSalesPerBusiness * 2L, again.RecordedSales);
        Assert.Equal(TimeSpan.Zero, again.Duration);

        // Ordinary growth - well short of the factor - still collects nothing.
        await GrowBusinessSalesAsync(BusinessAId, SeededSalesPerBusiness);
        var afterOrdinaryGrowth = await CollectAsync();
        Assert.Equal(SalesDateRangeStatisticsOutcome.AlreadyCurrent, afterOrdinaryGrowth.Outcome);

        // Past the factor - the shape a quarter-million-row history import has - collects again.
        await GrowBusinessSalesAsync(BusinessAId, SeededSalesPerBusiness * 7);
        var refreshed = await CollectAsync();
        Assert.Equal(SalesDateRangeStatisticsOutcome.Collected, refreshed.Outcome);
        Assert.Equal(SeededSalesPerBusiness * 2L, refreshed.RecordedSales);
        Assert.Equal(SeededSalesPerBusiness * 10L, refreshed.CurrentSales);

        // And what it recorded is the table as it now is, so the next decision starts from here.
        var afterRefresh = await CollectAsync();
        Assert.Equal(SalesDateRangeStatisticsOutcome.AlreadyCurrent, afterRefresh.Outcome);
        Assert.Equal(SeededSalesPerBusiness * 10L, afterRefresh.RecordedSales);
    }

    /// <summary>
    /// A brand-new database collects nothing and writes nothing: there is no sales history for
    /// statistics to describe, so the policy's floor keeps the write lock untaken at every startup
    /// until the business has actually traded.
    /// </summary>
    [Fact]
    public async Task An_initial_database_with_no_sales_collects_no_statistics()
    {
        await MigrateAsync();

        var result = await CollectAsync();

        Assert.Equal(SalesDateRangeStatisticsOutcome.NotWorthCollecting, result.Outcome);
        Assert.Equal(0L, result.CurrentSales);
        Assert.Null(result.RecordedSales);
        Assert.Equal(TimeSpan.Zero, result.Duration);

        // Nothing was analysed, so SQLite never created its statistics table.
        Assert.False(await StatisticsTableExistsAsync());
    }

    /// <summary>
    /// Collecting statistics changes which plan SQLite picks and nothing else: both businesses keep
    /// every row, and the money report's own figures for each of them are identical either side of a
    /// collection (issue #1287's criterion that financial results are preserved).
    ///
    /// <para>Two businesses deliberately, and the second one's figures read as the second business -
    /// what would show a changed plan reaching the wrong rows is one business's total picking up the
    /// other's.</para>
    /// </summary>
    [Fact]
    public async Task Collecting_statistics_preserves_every_businesss_rows_and_financial_figures()
    {
        await MigrateAsync();
        await SeedBusinessWithManySalesAsync(BusinessAId);
        await SeedBusinessWithManySalesAsync(BusinessBId);
        await SeedTwoBusinessesExtraSalesAsync();

        var before = await FinancialFiguresAsync();

        Assert.Equal(SalesDateRangeStatisticsOutcome.Collected, (await CollectAsync()).Outcome);

        var after = await FinancialFiguresAsync();

        Assert.Equal(before, after);

        // And the plain row counts, read outside every adapter.
        Assert.Equal(
            SeededSalesPerBusiness + 3L,
            await ScalarAsync($"SELECT COUNT(*) FROM NayaxSales WHERE BusinessId = {BusinessAId}"));
        Assert.Equal(
            SeededSalesPerBusiness + 2L,
            await ScalarAsync($"SELECT COUNT(*) FROM NayaxSales WHERE BusinessId = {BusinessBId}"));
        Assert.Equal(
            2L, await ScalarAsync("SELECT COUNT(*) FROM Businesses"));
    }

    /// <summary>
    /// Each business's money-report figures and sales total, read through the real report provider in
    /// that business's own scope - so a figure that moved between businesses shows up as a changed
    /// value here.
    /// </summary>
    private async Task<List<string>> FinancialFiguresAsync()
    {
        var figures = new List<string>();

        foreach (var businessId in new[] { BusinessAId, BusinessBId })
        {
            await using var db = TestAppDbContext.For(Options(), businessId);

            var facts = await new EfReconciliationReportFactsProvider(
                    db, TestFinancialUseCases.TrivialCalendar)
                .GetFactsAsync(FirstBusinessDate, LastBusinessDate, null, CancellationToken.None);

            figures.Add(
                $"business {businessId}: vending={facts.TotalVendingSales} card={facts.CardSales} "
                + $"cash={facts.CashSales} transactions={facts.TotalTransactionCount} "
                + $"periods={facts.Periods.Count} "
                + $"sales={await ScalarAsync($"SELECT COUNT(*) FROM NayaxSales WHERE BusinessId = {businessId}")} "
                + $"total={await ScalarAsync($"SELECT TOTAL(SettlementValue) FROM NayaxSales WHERE BusinessId = {businessId}")}");
        }

        return figures;
    }

    /// <summary>
    /// Runs both adapters, reports every statement they issue that reads the sales table by a
    /// <c>MachineAuthorizationTime</c> range together with the plan SQLite chose for it, and - when
    /// asked - asserts that plan searches the new index.
    /// </summary>
    private async Task ReportSalesRangePlansAsync(string state, bool assert)
    {
        await using (var reports = TestAppDbContext.For(Options(), BusinessAId))
        {
            _statements.Clear();
            await new EfReconciliationReportFactsProvider(reports, TestFinancialUseCases.TrivialCalendar)
                .GetFactsAsync(FirstBusinessDate, LastBusinessDate, null, CancellationToken.None);

            await ReportSalesRangeStatementsAsync(
                reports, "the issue #1250 business-date sales selection", state, assert);
        }

        await using (var dashboard = TestAppDbContext.For(Options(), BusinessAId))
        {
            _statements.Clear();
            await new EfDashboardSummarySalesFactsProvider(dashboard).GetSalesFactsAsync(
                Period(FirstBusinessDate.AddDays(15), LastBusinessDate),
                Period(FirstBusinessDate, FirstBusinessDate.AddDays(14)),
                CancellationToken.None);

            await ReportSalesRangeStatementsAsync(
                dashboard, "the dashboard summary card's period totals", state, assert);
        }
    }

    /// <summary>
    /// Each plan is reported before it is asserted on, because "the plan changed" is only useful with
    /// the plan in it.
    /// </summary>
    private async Task ReportSalesRangeStatementsAsync(
        AppDbContext db, string what, string state, bool assert)
    {
        var statements = _statements
            .Where(statement => statement.Sql.Contains("FROM \"NayaxSales\"", StringComparison.Ordinal)
                && statement.Sql.Contains("\"MachineAuthorizationTime\" >", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(statements);

        foreach (var statement in statements)
        {
            var plan = await PlanAsync(statement, db);

            Report($"{what}, {state}", plan, statement);

            if (!assert) continue;

            var reported = $"{what}, {state}:\n{statement.Sql}\nparameters: {statement.ParameterSummary}"
                + $"\nplan:\n{string.Join('\n', plan)}";

            Assert.True(
                plan.Exists(line => line.Contains(IndexName, StringComparison.Ordinal)),
                $"Expected {IndexName} in the plan once the application has collected statistics: "
                    + reported);
            Assert.True(
                plan.Exists(line => line.StartsWith("SEARCH", StringComparison.Ordinal)),
                $"Expected a search of the index rather than a scan of the sales table: {reported}");
        }
    }

    /// <summary>
    /// The real collection path - the one adapter startup and a finished sales history import use -
    /// over its own context, as both of those callers have.
    /// </summary>
    private async Task<SalesDateRangeStatisticsResult> CollectAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(Options());

        return await new SqliteSalesDateRangeStatistics(db).CollectIfStaleAsync(CancellationToken.None);
    }

    /// <summary>The plan, in the test output, so a run that fails here reports what SQLite chose.</summary>
    private void Report(string what, List<string> plan, CapturedStatement? statement = null) =>
        _output.WriteLine(statement is null
            ? $"{what}:\nplan:\n{string.Join('\n', plan)}"
            : $"{what}:\n{statement.Sql}\nparameters: {statement.ParameterSummary}\nplan:\n"
                + string.Join('\n', plan));

    /// <summary>A fully migrated database holding both businesses' sales.</summary>
    private async Task MigrateAndSeedAsync()
    {
        await MigrateAsync();
        await SeedTwoBusinessesWithSalesAsync();
    }

    private async Task MigrateAsync()
    {
        await using var setup = TestAppDbContext.Unrestricted(Options());
        await setup.Database.MigrateAsync();
    }

    /// <summary>
    /// One business's year of sales, written by SQLite itself from a recursive sequence. The plan test
    /// needs a realistic row count rather than a handful of rows: the collected statistics record how
    /// many rows a distinct <c>BusinessId</c> holds, and that number is what decides whether skipping
    /// the leading column is worth it to the planner. Three machines and a spread of instants over a
    /// year keep the index's key order realistic too.
    /// </summary>
    private async Task SeedBusinessWithManySalesAsync(int businessId) =>
        await SeedSalesAsync(businessId, first: 0, count: SeededSalesPerBusiness, withBusinessRow: true);

    /// <summary>More of the same business's history, which is what a refresh is decided from.</summary>
    private async Task GrowBusinessSalesAsync(int businessId, int count) =>
        await SeedSalesAsync(
            businessId,
            first: _seeded.TryGetValue(businessId, out var seeded) ? seeded : 0,
            count,
            withBusinessRow: false);

    private async Task SeedSalesAsync(int businessId, int first, int count, bool withBusinessRow)
    {
        if (withBusinessRow)
        {
            await ExecuteAsync(
                $"""
                INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, SupplierPresetsCopiedAtUtc)
                VALUES ({businessId}, 'Fictional Vending {businessId}', 1, '2026-01-01 00:00:00', NULL);
                """);
        }

        await ExecuteAsync(
            $"""
            WITH RECURSIVE sequence(i) AS (
                SELECT {first} UNION ALL SELECT i + 1 FROM sequence WHERE i < {first + count - 1}
            )
            INSERT INTO NayaxSales (
                Id, BusinessId, TransactionID, MachineID, MachineAuthorizationTime,
                SettlementValue, PaymentMethod, TransactionStatusId, CostingStatus, CostSource,
                Provider, ProviderProductIdIsRaw)
            SELECT
                {businessId} * 100000000 + i,
                {businessId},
                500000 + i,
                942488500 + (i % 3),
                strftime('%Y-%m-%d %H:%M:%S', '2026-01-01 00:00:00', '+' || (i % 365) || ' days',
                    '+' || (i % 86400) || ' seconds'),
                3.0, 'Cash', 12, 0, 0, 1, 1
            FROM sequence;
            """);

        _seeded[businessId] = first + count;
    }

    /// <summary>
    /// Two businesses with sales inside and outside the reported window, written as SQL so the same
    /// seed works at the previous migration - where the EF model's newer columns do not exist yet -
    /// and at the current one.
    /// </summary>
    private async Task SeedTwoBusinessesWithSalesAsync()
    {
        await ExecuteAsync(
            $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, SupplierPresetsCopiedAtUtc)
            VALUES ({BusinessAId}, 'Fictional Vending A', 1, '2026-01-01 00:00:00', NULL),
                   ({BusinessBId}, 'Fictional Vending B', 1, '2026-01-01 00:00:00', NULL);
            """);

        await SeedTwoBusinessesExtraSalesAsync();
    }

    /// <summary>
    /// The five hand-written sales the row-count and figure assertions name, with two businesses
    /// sharing a <c>TransactionID</c> and one sale outside the reported window. They carry distinct
    /// keys and transaction ids from the synthetic history above, so either seed can be used alone or
    /// after it.
    /// </summary>
    private async Task SeedTwoBusinessesExtraSalesAsync() =>
        await ExecuteAsync(
            $"""
            INSERT INTO NayaxSales (
                Id, BusinessId, TransactionID, MachineID, MachineAuthorizationTime,
                SettlementValue, TransactionStatusId, CostingStatus, CostSource, Provider,
                ProviderProductIdIsRaw, SqsVerification)
            VALUES
                (1, {BusinessAId}, 7001, 942488501, '2026-06-02 00:30:00', 3.5, 12, 0, 0, 1, 1, NULL),
                (2, {BusinessAId}, 7002, 942488501, '2026-06-20 09:15:00', 4.0, 12, 0, 0, 1, 1, NULL),
                (3, {BusinessAId}, 7003, 942488501, '2026-07-02 09:15:00', 5.0, 12, 0, 0, 1, 1, NULL),
                (4, {BusinessBId}, 7001, 942488502, '2026-06-03 10:00:00', 2.5, 12, 0, 0, 1, 1, NULL),
                (5, {BusinessBId}, 7004, 942488502, '2026-06-25 10:00:00', 2.5, 12, 0, 0, 1, 1, NULL);
            """);

    private static MachineDashboardPeriodUtc Period(DateTime startUtc, DateTime endUtc) =>
        new(startUtc, endUtc, startUtc.Date, endUtc.Date);

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(new StatementCapture(_statements))
            .Options;

    /// <summary>
    /// The plan SQLite itself produces for a captured statement, with the very parameter values EF
    /// bound to it.
    ///
    /// <para>An EF statement is planned on the context's <em>own</em> connection, because EF registers
    /// the functions its aggregates call (<c>ef_sum</c> and the rest) on the connection it opens, and
    /// a statement naming one cannot even be prepared anywhere else.</para>
    /// </summary>
    private async Task<List<string>> PlanAsync(CapturedStatement statement, AppDbContext? db = null)
    {
        await using var owned = db is null ? new SqliteConnection(_connectionString) : null;
        if (owned is not null)
            await owned.OpenAsync();
        else
            await db!.Database.OpenConnectionAsync();

        var connection = owned ?? db!.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + statement.Sql;
        foreach (var (name, value) in statement.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(3));
        }

        if (owned is null)
            await db!.Database.CloseConnectionAsync();

        return plan;
    }

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private async Task<string?> IndexSqlAsync() =>
        await ScalarAsync(
            $"SELECT sql FROM sqlite_master WHERE type = 'index' AND name = '{IndexName}'") as string;

    /// <summary>
    /// Whether SQLite has a statistics table at all. It does not exist until something has been
    /// analysed, which is what makes it the honest check for "this database has no statistics".
    /// </summary>
    private async Task<bool> StatisticsTableExistsAsync() =>
        await ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1'")
            is long count && count > 0;

    /// <summary>The recorded statistics row for this index, read from outside the adapter.</summary>
    private async Task<string?> RecordedStatisticsAsync() =>
        (await StatisticsTableExistsAsync())
            ? await ScalarAsync($"SELECT stat FROM sqlite_stat1 WHERE idx = '{IndexName}'") as string
            : null;

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private sealed record CapturedStatement(string Sql, List<KeyValuePair<string, object?>> Parameters)
    {
        public string ParameterSummary =>
            string.Join(", ", Parameters.Select(parameter => $"{parameter.Key}={parameter.Value ?? "NULL"}"));
    }

    /// <summary>
    /// Records the statement EF is about to execute together with its bound parameter values, which
    /// is what makes the plan below the plan of the real query rather than of a retyped one.
    /// </summary>
    private sealed class StatementCapture : DbCommandInterceptor
    {
        private readonly List<CapturedStatement> _statements;

        public StatementCapture(List<CapturedStatement> statements) => _statements = statements;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command) =>
            _statements.Add(new CapturedStatement(
                command.CommandText,
                command.Parameters
                    .Cast<DbParameter>()
                    .Select(parameter => new KeyValuePair<string, object?>(parameter.ParameterName, parameter.Value))
                    .ToList()));
    }
}
