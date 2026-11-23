using System.Data.Common;
using Inventory.Application.Machines;
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
/// The sales date-range index of issue #1287 (audit #1226, finding DB-02), on a real SQLite
/// <em>file</em> rather than an in-memory database, because what is being proved is relational: which
/// index SQLite's own planner chooses for the SQL EF Core generates, and that applying the migration
/// over a populated database keeps every business's rows.
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
/// <para><strong>What they measured, and the limit it exposes.</strong> The index is searched for a
/// sales date range that names its business. The money reports and dashboards do not name it: the
/// central tenant filter is translated as <c>(@unrestricted OR "BusinessId" = @business)</c>, which
/// constrains the index's leading column not at all - the same <c>OR</c> that made issue #1304 index
/// <c>ProductId</c> on its own. SQLite can still reach the index through a <em>skip-scan</em>
/// (<c>ANY(BusinessId)</c>), but only on a database that has statistics, and it chooses a full table
/// scan without them. Nothing in this repository runs <c>ANALYZE</c>, so a deployed database has
/// none. <see cref="The_report_and_dashboard_reads_reach_the_index_only_once_the_database_has_statistics"/>
/// pins both halves of that; the pull request for issue #1287 carries it as the decision a human
/// owns.</para>
/// </summary>
public sealed class NayaxSalesDateRangeIndexMigrationTests : IDisposable
{
    private const string PreviousMigration = "AddNayaxSalesProductIdIndex";
    private const string IndexMigration = "AddNayaxSalesDateRangeIndex";
    private const string IndexName = "IX_NayaxSales_BusinessId_MachineAuthorizationTime";

    private const int BusinessAId = 1;
    private const int BusinessBId = 2;

    /// <summary>
    /// Enough sales per business for <c>ANALYZE</c> to record a business as holding far more than the
    /// eighteen rows SQLite wants before it will skip an index's leading column, and few enough to
    /// seed and plan in well under a second.
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
    /// The two date-range reads criterion 2 of issue #1287 names - the shared business-date selection
    /// of issue #1250, here through the reconciliation report, and one other dashboard query, the
    /// Dashboard summary card's period totals - against a database holding both businesses' sales.
    ///
    /// <para>Measured, not assumed, in both states a deployed database can be in:</para>
    /// <list type="bullet">
    /// <item><strong>Without statistics</strong>, which is every database this repository creates
    /// because nothing here runs <c>ANALYZE</c>: both statements scan the sales table. The tenant
    /// filter is an <c>OR</c> over a bare parameter, so nothing constrains the index's leading column,
    /// and with no row statistics the planner prefers the scan.</item>
    /// <item><strong>After <c>ANALYZE</c></strong>: the same statements search the index by
    /// skip-scan - <c>ANY(BusinessId) AND MachineAuthorizationTime&gt;? AND
    /// MachineAuthorizationTime&lt;?</c> - because the statistics say one business holds far more than
    /// the eighteen rows a skipped leading column has to be worth.</item>
    /// </list>
    ///
    /// <para>So the index is the right one and the reports can reach it; what they are missing is the
    /// statistics, which is a decision about running and refreshing <c>ANALYZE</c> that issue #1287
    /// does not make and this change deliberately does not take. Both halves are asserted so that
    /// neither can change silently: if the first half starts searching the index, the gain has been
    /// obtained and this test should become the plain assertion criterion 2 asks for.</para>
    /// </summary>
    [Fact]
    public async Task The_report_and_dashboard_reads_reach_the_index_only_once_the_database_has_statistics()
    {
        await MigrateAsync();
        await SeedBusinessWithManySalesAsync(BusinessAId);
        await SeedBusinessWithManySalesAsync(BusinessBId);

        await AssertSalesRangePlansAsync(statisticsCollected: false);

        // The one statement that changes which plan SQLite picks. It is run here, on the test's own
        // database, and by nothing in the application.
        await ExecuteAsync("ANALYZE;");

        await AssertSalesRangePlansAsync(statisticsCollected: true);
    }

    /// <summary>
    /// Runs both adapters and asserts every statement they issue that reads the sales table by a
    /// <c>MachineAuthorizationTime</c> range scans the table, or searches the index, according to
    /// whether the database has statistics.
    /// </summary>
    private async Task AssertSalesRangePlansAsync(bool statisticsCollected)
    {
        await using (var reports = TestAppDbContext.For(Options(), BusinessAId))
        {
            _statements.Clear();
            await new EfReconciliationReportFactsProvider(reports, TestFinancialUseCases.TrivialCalendar)
                .GetFactsAsync(FirstBusinessDate, LastBusinessDate, null, CancellationToken.None);

            await AssertSalesRangeStatementsAsync(
                reports, "the issue #1250 business-date sales selection", statisticsCollected);
        }

        await using (var dashboard = TestAppDbContext.For(Options(), BusinessAId))
        {
            _statements.Clear();
            await new EfDashboardSummarySalesFactsProvider(dashboard).GetSalesFactsAsync(
                Period(FirstBusinessDate.AddDays(15), LastBusinessDate),
                Period(FirstBusinessDate, FirstBusinessDate.AddDays(14)),
                CancellationToken.None);

            await AssertSalesRangeStatementsAsync(
                dashboard, "the dashboard summary card's period totals", statisticsCollected);
        }
    }

    /// <summary>
    /// Each plan is reported before it is asserted on, because "the plan changed" is only useful with
    /// the plan in it.
    /// </summary>
    private async Task AssertSalesRangeStatementsAsync(
        AppDbContext db, string what, bool statisticsCollected)
    {
        var statements = _statements
            .Where(statement => statement.Sql.Contains("FROM \"NayaxSales\"", StringComparison.Ordinal)
                && statement.Sql.Contains("\"MachineAuthorizationTime\" >", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(statements);

        foreach (var statement in statements)
        {
            var plan = await PlanAsync(statement, db);
            var state = statisticsCollected ? "after ANALYZE" : "without statistics";
            var reported = $"{what}, {state}:\n{statement.Sql}\nparameters: {statement.ParameterSummary}"
                + $"\nplan:\n{string.Join('\n', plan)}";

            Report($"{what}, {state}", plan, statement);

            var usesIndex = plan.Exists(line => line.Contains(IndexName, StringComparison.Ordinal));
            if (statisticsCollected)
            {
                Assert.True(
                    usesIndex,
                    $"Expected a skip-scan of {IndexName} once the database has statistics: {reported}");
            }
            else
            {
                Assert.True(
                    !usesIndex,
                    $"{IndexName} is now reachable without statistics by {reported}\n\n"
                        + "That is the wanted outcome, not a defect: make this the plain assertion "
                        + "criterion 2 of issue #1287 asks for, and update docs/architecture.md "
                        + "§ SQLite operating assumptions and scale strategy.");
                Assert.True(
                    plan.Exists(line => line.StartsWith("SCAN", StringComparison.Ordinal)),
                    $"Expected the recorded table scan in {reported}");
            }
        }
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
    /// needs a realistic row count rather than a handful of rows: <c>ANALYZE</c> records how many rows
    /// a distinct <c>BusinessId</c> holds, and that number is what decides whether skipping the
    /// leading column is worth it to the planner. Three machines and a spread of instants over a year
    /// keep the index's key order realistic too.
    /// </summary>
    private async Task SeedBusinessWithManySalesAsync(int businessId)
    {
        await ExecuteAsync(
            $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, SupplierPresetsCopiedAtUtc)
            VALUES ({businessId}, 'Fictional Vending {businessId}', 1, '2026-01-01 00:00:00', NULL);
            """);

        await ExecuteAsync(
            $"""
            WITH RECURSIVE sequence(i) AS (
                SELECT 0 UNION ALL SELECT i + 1 FROM sequence WHERE i < {SeededSalesPerBusiness - 1}
            )
            INSERT INTO NayaxSales (
                Id, BusinessId, TransactionID, MachineID, MachineAuthorizationTime,
                SettlementValue, PaymentMethod, TransactionStatusId, CostingStatus, CostSource,
                Provider, ProviderProductIdIsRaw)
            SELECT
                {businessId} * 1000000 + i,
                {businessId},
                500000 + i,
                942488500 + (i % 3),
                strftime('%Y-%m-%d %H:%M:%S', '2026-01-01 00:00:00', '+' || (i % 365) || ' days',
                    '+' || (i % 86400) || ' seconds'),
                3.0, 'Cash', 12, 0, 0, 1, 1
            FROM sequence;
            """);
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
    }

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
