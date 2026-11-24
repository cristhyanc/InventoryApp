using System.Diagnostics;
using System.Globalization;
using Inventory.Application.Maintenance;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Xunit.Abstractions;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The <strong>opt-in</strong> measurements issue #1287 asks the pull request to report, on a
/// synthetic database of the size audit #1226 measured - 500,000 sales for each of two businesses -
/// and reported <strong>separately</strong> for the three things that take SQLite's write lock:
/// creating <c>AddNayaxSalesDateRangeIndex</c>, the <em>initial</em> collection of that index's
/// planner statistics, and a policy-driven <em>refresh</em> of them once history has grown.
///
/// <para><strong>Why it is opt-in.</strong> Repository validation stops any test that runs past 90
/// seconds and must not depend on a 150 MB temporary file (AGENTS.md § Commands), so these cases are
/// skipped unless they are asked for - the same arrangement
/// <see cref="InventoryApi.Tests.Application.Imports.NayaxSalesHistoryFullSizeBenchmark"/> uses for
/// issue #1304's full-size figures. <see cref="NayaxSalesDateRangeIndexMigrationTests"/> is what runs
/// on every validation.</para>
///
/// <para><strong>How to run them.</strong> From the repository root:</para>
/// <code>
/// INVENTORY_SALES_DATE_INDEX_BENCHMARK=1 dotnet test \
///   backend/Inventory.IntegrationTests/Inventory.IntegrationTests.csproj --configuration Release \
///   --filter "FullyQualifiedName~NayaxSalesDateRangeIndexSizeBenchmark" --logger "console;verbosity=detailed"
/// </code>
/// <para><c>INVENTORY_SALES_DATE_INDEX_BENCHMARK_SALES</c> overrides the per-business row count.</para>
///
/// <para><strong>What they measured (GitHub-hosted runner, 24 November 2026)</strong>, at 500,000
/// sales for each of two businesses:</para>
/// <code>
/// step                              rows        file                   growth           duration
/// CREATE INDEX (the migration)      1,000,000   131.9 MB -> 160.2 MB   +29,659,136 B    0.80 s
/// initial statistics collection     1,000,000   160.2 MB -> 160.2 MB        +4,096 B    0.10 s
/// deciding they are already current 1,000,000   no write at all                   -     0.00 s
/// refresh after a fourfold growth   1,000,000   167.3 MB -> 167.3 MB            +0 B    0.11 s
/// </code>
/// <para>The index's proportion matches audit #1226's own figure (132.7 MB to 162.4 MB, about 22%).
/// All three write-lock holds are sub-second at that size, so what Production startup and a finished
/// history import add to the write lock is short - but each is a write lock, and the usual
/// pre-release backup still applies. Statistics cost essentially nothing in storage: what
/// <c>ANALYZE</c> writes for one index is a single <c>sqlite_stat1</c> row, which is the one 4 KB page
/// the initial collection adds and the zero bytes a refresh adds by overwriting it. The refresh case's
/// absolute file size differs from the other case's because its sales are written with the index
/// already in place rather than indexed afterwards; read the growth, not the total.</para>
/// <para>The rows these tests write are narrower than production rows - they fill the columns a sale
/// cannot be stored without, not the optional Nayax text - so the absolute file sizes are smaller
/// than the audit's 132.7 MB and the growth is best read as a proportion. The index holds one integer
/// and one timestamp per sale either way.</para>
/// </summary>
public class NayaxSalesDateRangeIndexSizeBenchmark : IDisposable
{
    /// <summary>The variable that asks for the run. Anything but <c>1</c> or <c>true</c> skips it.</summary>
    private const string EnabledVariable = "INVENTORY_SALES_DATE_INDEX_BENCHMARK";

    private const string SalesVariable = "INVENTORY_SALES_DATE_INDEX_BENCHMARK_SALES";

    /// <summary>The audit's size: half a million sales for each of the two businesses.</summary>
    private const int DefaultSalesPerBusiness = 500_000;

    private const string PreviousMigration = "AddNayaxSalesProductIdIndex";
    private const string IndexMigration = "AddNayaxSalesDateRangeIndex";
    private const string IndexName = "IX_NayaxSales_BusinessId_MachineAuthorizationTime";

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-sales-date-index-benchmark-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;
    private readonly ITestOutputHelper _output;

    public NayaxSalesDateRangeIndexSizeBenchmark(ITestOutputHelper output)
    {
        _output = output;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
    }

    /// <summary>
    /// The index migration, the initial statistics collection and the cost of deciding that they are
    /// already current - three separate write-lock figures over the same database, in the order a
    /// deployment meets them.
    /// </summary>
    [Fact]
    public async Task The_index_migration_and_the_initial_statistics_collection_report_their_own_cost()
    {
        if (!Requested())
        {
            ReportSkipped();
            return;
        }

        var salesPerBusiness = SalesPerBusiness();

        await using (var before = TestAppDbContext.Unrestricted(Options()))
            await before.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        await SeedAsync(businessId: 1, firstId: 1, salesPerBusiness);
        await SeedAsync(businessId: 2, firstId: salesPerBusiness + 1, salesPerBusiness);

        var rows = await SalesCountAsync();
        Assert.Equal(2L * salesPerBusiness, rows);

        var sizeBeforeIndex = FileSizeBytes();

        var migration = Stopwatch.StartNew();
        await using (var upgraded = TestAppDbContext.Unrestricted(Options()))
            await upgraded.Database.GetService<IMigrator>().MigrateAsync(IndexMigration);
        migration.Stop();

        var sizeAfterIndex = FileSizeBytes();

        Assert.NotNull(await ScalarAsync(
            $"SELECT sql FROM sqlite_master WHERE type = 'index' AND name = '{IndexName}'"));
        Assert.Equal(rows, await SalesCountAsync());

        Report("CREATE INDEX (the migration)", rows, sizeBeforeIndex, sizeAfterIndex, migration.Elapsed);

        // The initial collection, through the very adapter startup uses - its own duration is the
        // write lock it holds, and its own file growth is one sqlite_stat1 row.
        var initial = await CollectAsync();
        var sizeAfterStatistics = FileSizeBytes();

        Assert.Equal(SalesDateRangeStatisticsOutcome.Collected, initial.Outcome);
        Assert.Null(initial.RecordedSales);
        Assert.Equal(rows, initial.CurrentSales);
        Assert.Equal(rows, await SalesCountAsync());

        Report(
            "initial statistics collection",
            rows,
            sizeAfterIndex,
            sizeAfterStatistics,
            initial.Duration);

        // And what every later startup pays when nothing has changed: one COUNT(*) and one read of
        // the recorded figure, with no write and no lock at all.
        var decision = Stopwatch.StartNew();
        var current = await CollectAsync();
        decision.Stop();

        Assert.Equal(SalesDateRangeStatisticsOutcome.AlreadyCurrent, current.Outcome);
        Assert.Equal(rows, current.RecordedSales);

        _output.WriteLine(
            $"deciding that the statistics are already current over {rows:N0} sales: "
                + $"{decision.Elapsed.TotalSeconds:F2}s, no write and no lock.");
    }

    /// <summary>
    /// A policy-driven <em>refresh</em> at the audit's size, which is the figure a release needs for
    /// the second trigger: the statistics are collected while the table holds a quarter of its final
    /// rows, history then grows past
    /// <see cref="Inventory.Domain.Maintenance.SalesDateRangeStatisticsPolicy.GrowthFactor"/> - the
    /// shape a 250,000-row history import has - and the refresh that fires is timed over the full
    /// 500,000 sales per business.
    /// </summary>
    [Fact]
    public async Task A_refresh_after_a_fourfold_growth_reports_its_own_cost()
    {
        if (!Requested())
        {
            ReportSkipped();
            return;
        }

        var salesPerBusiness = SalesPerBusiness();
        var quarter = salesPerBusiness / 4;

        await using (var migrated = TestAppDbContext.Unrestricted(Options()))
            await migrated.Database.MigrateAsync();

        await SeedAsync(businessId: 1, firstId: 1, quarter);
        await SeedAsync(businessId: 2, firstId: salesPerBusiness + 1, quarter);

        var initial = await CollectAsync();
        Assert.Equal(SalesDateRangeStatisticsOutcome.Collected, initial.Outcome);
        Assert.Equal(2L * quarter, initial.CurrentSales);

        // The growth: each business to its full size, which takes the table past the factor.
        await SeedAsync(
            businessId: 1,
            firstId: 1,
            salesPerBusiness - quarter,
            firstIndex: quarter,
            withBusinessRow: false);
        await SeedAsync(
            businessId: 2,
            firstId: salesPerBusiness + 1,
            salesPerBusiness - quarter,
            firstIndex: quarter,
            withBusinessRow: false);

        var rows = await SalesCountAsync();
        Assert.Equal(2L * salesPerBusiness, rows);

        var sizeBefore = FileSizeBytes();

        var refreshed = await CollectAsync();

        var sizeAfter = FileSizeBytes();

        Assert.Equal(SalesDateRangeStatisticsOutcome.Collected, refreshed.Outcome);
        Assert.Equal(2L * quarter, refreshed.RecordedSales);
        Assert.Equal(rows, refreshed.CurrentSales);
        Assert.Equal(rows, await SalesCountAsync());

        Report("refresh after a fourfold growth", rows, sizeBefore, sizeAfter, refreshed.Duration);
    }

    /// <summary>The real collection path, which is the one startup and a finished import use.</summary>
    private async Task<SalesDateRangeStatisticsResult> CollectAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(Options());

        return await new SqliteSalesDateRangeStatistics(db).CollectIfStaleAsync(CancellationToken.None);
    }

    private void ReportSkipped() =>
        _output.WriteLine(
            $"Skipped: set {EnabledVariable}=1 to run the measurement. It writes "
                + $"{SalesPerBusiness():N0} sales for each of two businesses and a database file of "
                + "well over a hundred megabytes, which is why it is not part of repository "
                + "validation.");

    private void Report(string step, long rows, long sizeBefore, long sizeAfter, TimeSpan elapsed) =>
        _output.WriteLine(
            $"{step} over {rows:N0} sales: file {Megabytes(sizeBefore)} -> {Megabytes(sizeAfter)} "
                + $"(+{sizeAfter - sizeBefore:N0} bytes, "
                + $"{(double)(sizeAfter - sizeBefore) / sizeBefore * 100:F1}%); "
                + $"{elapsed.TotalSeconds:F2}s holding the write lock.");

    private async Task<long> SalesCountAsync() =>
        Convert.ToInt64(
            await ScalarAsync("SELECT COUNT(*) FROM NayaxSales"), CultureInfo.InvariantCulture);

    /// <summary>
    /// One business's sales, written by SQLite itself from a recursive sequence rather than row by
    /// row through EF: what is being measured is the index, so the seed must not dominate the run.
    /// Each row fills exactly the columns a stored sale cannot be null in, plus a spread of
    /// authorization instants over two years so the index is built over realistic key order.
    ///
    /// <para><paramref name="firstIndex"/> is where the sequence starts, so a second call continues
    /// the same business's history with keys and transaction ids it does not already hold - which is
    /// what the refresh case's growth needs.</para>
    /// </summary>
    private async Task SeedAsync(
        int businessId,
        long firstId,
        int sales,
        int firstIndex = 0,
        bool withBusinessRow = true)
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
                SELECT {firstIndex} UNION ALL SELECT i + 1 FROM sequence
                WHERE i < {firstIndex + sales - 1}
            )
            INSERT INTO NayaxSales (
                Id, BusinessId, TransactionID, MachineID, MachineAuthorizationTime,
                SettlementValue, PaymentMethod, TransactionStatusId, CostingStatus, CostSource,
                Provider, ProviderProductIdIsRaw, CostOfGoodsSold, UnitCostAtSale)
            SELECT
                {firstId} + i,
                {businessId},
                100000000 + i,
                942488500 + (i % 40),
                strftime('%Y-%m-%d %H:%M:%S', '2025-01-01 00:00:00', '+' || (i % 730) || ' days',
                    '+' || (i % 86400) || ' seconds'),
                2.5 + (i % 7) * 0.5,
                CASE i % 2 WHEN 0 THEN 'Credit Card' ELSE 'Cash' END,
                12, 1, 1, 1, 1,
                1.2 + (i % 5) * 0.1,
                1.2 + (i % 5) * 0.1
            FROM sequence;
            """);
    }

    private static string Megabytes(long bytes) =>
        $"{bytes / 1024d / 1024d:F1} MB";

    /// <summary>The database and anything SQLite keeps beside it, which is what a release has to copy.</summary>
    private long FileSizeBytes()
    {
        SqliteConnection.ClearAllPools();
        return new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" }
            .Where(File.Exists)
            .Sum(path => new FileInfo(path).Length);
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

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

    private static bool Requested() =>
        Environment.GetEnvironmentVariable(EnabledVariable) is "1" or "true" or "True";

    private static int SalesPerBusiness() =>
        int.TryParse(
            Environment.GetEnvironmentVariable(SalesVariable),
            CultureInfo.InvariantCulture,
            out var configured) && configured > 0
            ? configured
            : DefaultSalesPerBusiness;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
