using System.Diagnostics;
using System.Globalization;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Xunit.Abstractions;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The <strong>opt-in</strong> measurement issue #1287 asks the pull request to report: how long
/// <c>AddNayaxSalesDateRangeIndex</c> takes, and how much the database file grows, on a synthetic
/// database of the size audit #1226 measured - 500,000 sales for each of two businesses.
///
/// <para><strong>Why it is opt-in.</strong> Repository validation stops any test that runs past 90
/// seconds and must not depend on a 150 MB temporary file (AGENTS.md § Commands), so this case is
/// skipped unless it is asked for - the same arrangement
/// <see cref="InventoryApi.Tests.Application.Imports.NayaxSalesHistoryFullSizeBenchmark"/> uses for
/// issue #1304's full-size figures. <see cref="NayaxSalesDateRangeIndexMigrationTests"/> is what runs
/// on every validation.</para>
///
/// <para><strong>How to run it.</strong> From the repository root:</para>
/// <code>
/// INVENTORY_SALES_DATE_INDEX_BENCHMARK=1 dotnet test \
///   backend/Inventory.IntegrationTests/Inventory.IntegrationTests.csproj --configuration Release \
///   --filter "FullyQualifiedName~NayaxSalesDateRangeIndexSizeBenchmark" --logger "console;verbosity=detailed"
/// </code>
/// <para><c>INVENTORY_SALES_DATE_INDEX_BENCHMARK_SALES</c> overrides the per-business row count.</para>
///
/// <para><strong>What it measured (GitHub-hosted runner, 23 November 2026)</strong>, at 500,000 sales
/// for each of two businesses:</para>
/// <code>
/// rows        file before   file after   growth   migration
/// 1,000,000      131.9 MB     160.2 MB    21.4%       0.67 s
/// </code>
/// <para>The proportion matches audit #1226's own figure (132.7 MB to 162.4 MB, about 22%). The
/// migration itself is sub-second at that size, so the write lock it holds at Production startup is
/// short - but it is a write lock, and the usual pre-release backup still applies.</para>
/// <para>The rows this test writes are narrower than production rows - it fills the columns a sale
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

    [Fact]
    public async Task The_index_migration_reports_its_duration_and_the_file_it_adds()
    {
        if (!Requested())
        {
            _output.WriteLine(
                $"Skipped: set {EnabledVariable}=1 to run the measurement. It writes "
                    + $"{SalesPerBusiness():N0} sales for each of two businesses and a database file of "
                    + "well over a hundred megabytes, which is why it is not part of repository "
                    + "validation.");
            return;
        }

        var salesPerBusiness = SalesPerBusiness();

        await using (var before = TestAppDbContext.Unrestricted(Options()))
            await before.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        await SeedAsync(businessId: 1, firstId: 1, salesPerBusiness);
        await SeedAsync(businessId: 2, firstId: salesPerBusiness + 1, salesPerBusiness);

        var rows = Convert.ToInt64(await ScalarAsync("SELECT COUNT(*) FROM NayaxSales"), CultureInfo.InvariantCulture);
        Assert.Equal(2L * salesPerBusiness, rows);

        var sizeBefore = FileSizeBytes();

        var migration = Stopwatch.StartNew();
        await using (var upgraded = TestAppDbContext.Unrestricted(Options()))
            await upgraded.Database.GetService<IMigrator>().MigrateAsync(IndexMigration);
        migration.Stop();

        var sizeAfter = FileSizeBytes();

        Assert.NotNull(await ScalarAsync(
            $"SELECT sql FROM sqlite_master WHERE type = 'index' AND name = '{IndexName}'"));
        Assert.Equal(rows, Convert.ToInt64(
            await ScalarAsync("SELECT COUNT(*) FROM NayaxSales"), CultureInfo.InvariantCulture));

        _output.WriteLine(
            $"{rows:N0} sales ({salesPerBusiness:N0} for each of two businesses): file "
                + $"{Megabytes(sizeBefore)} -> {Megabytes(sizeAfter)} "
                + $"(+{Megabytes(sizeAfter - sizeBefore)}, "
                + $"{(double)(sizeAfter - sizeBefore) / sizeBefore * 100:F1}%); "
                + $"migration {migration.Elapsed.TotalSeconds:F2}s.");
    }

    /// <summary>
    /// One business's sales, written by SQLite itself from a recursive sequence rather than row by
    /// row through EF: what is being measured is the index, so the seed must not dominate the run.
    /// Each row fills exactly the columns a stored sale cannot be null in, plus a spread of
    /// authorization instants over two years so the index is built over realistic key order.
    /// </summary>
    private async Task SeedAsync(int businessId, long firstId, int sales)
    {
        await ExecuteAsync(
            $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, SupplierPresetsCopiedAtUtc)
            VALUES ({businessId}, 'Fictional Vending {businessId}', 1, '2026-01-01 00:00:00', NULL);
            """);

        await ExecuteAsync(
            $"""
            WITH RECURSIVE sequence(i) AS (
                SELECT 0 UNION ALL SELECT i + 1 FROM sequence WHERE i < {sales - 1}
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
