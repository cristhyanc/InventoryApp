using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>SetWeeklySupplierPriceFreshness</c> (owner decision, 1 November 2026): BIG W
/// and Kmart are checked once a week, so their records' freshness limit becomes 174 hours, and
/// nothing else about any supplier record changes - in either direction.
/// </summary>
public class SetWeeklySupplierPriceFreshnessMigrationTests
{
    private const string PreviousMigration = "20261030000000_AddAttachmentStorageQuota";
    private const string TargetMigration = "20261101090000_SetWeeklySupplierPriceFreshness";

    private static readonly string[] WeeklySuppliers = ["BIG W", "KMART"];

    [Fact]
    public async Task The_upgrade_sets_BIG_W_and_Kmart_to_the_weekly_limit_and_leaves_every_other_record_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration);
        var before = await SnapshotAsync(db);
        Assert.Equal(SupplierPriceFreshnessPolicy.TwiceWeeklyCheckLimitHours, before["BIG W"].PriceCheckFreshnessHours);
        Assert.Equal(SupplierPriceFreshnessPolicy.DailyCheckLimitHours, before["KMART"].PriceCheckFreshnessHours);

        await migrator.MigrateAsync(TargetMigration);
        var after = await SnapshotAsync(db);

        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (name, expected) in before)
        {
            var stored = after[name];
            var expectedLimit = WeeklySuppliers.Contains(name)
                ? SupplierPriceFreshnessPolicy.WeeklyCheckLimitHours
                : expected.PriceCheckFreshnessHours;
            Assert.Equal(expected with { PriceCheckFreshnessHours = expectedLimit }, stored);
        }
    }

    [Fact]
    public async Task Reversing_the_upgrade_restores_the_previous_limits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration);
        var before = await SnapshotAsync(db);

        await migrator.MigrateAsync(TargetMigration);
        await migrator.MigrateAsync(PreviousMigration);

        Assert.Equal(before, await SnapshotAsync(db));
    }

    private sealed record PresetRow(
        string Name,
        string? Url,
        bool IsEnabled,
        bool PricesIncludeGst,
        int PriceCheckFreshnessHours,
        DateTime UpdatedAtUtc);

    /// <summary>
    /// The columns this test is about, projected <strong>in SQL</strong> rather than read as whole
    /// entities: this database stands at an older migration than the current model, so a read of
    /// every mapped property would ask for a column a later migration adds (issue #1077's
    /// <c>SearchPrices</c>, for one) and fail on the schema rather than on the claim under test.
    /// </summary>
    private static async Task<Dictionary<string, PresetRow>> SnapshotAsync(AppDbContext db)
    {
        db.ChangeTracker.Clear();
        var rows = await db.SupplierPresets
            .AsNoTracking()
            .Select(preset => new
            {
                preset.NormalisedName,
                preset.Name,
                preset.Url,
                preset.IsEnabled,
                preset.PricesIncludeGst,
                preset.PriceCheckFreshnessHours,
                preset.UpdatedAtUtc,
            })
            .ToListAsync();

        return rows.ToDictionary(
            row => row.NormalisedName,
            row => new PresetRow(
                row.Name,
                row.Url,
                row.IsEnabled,
                row.PricesIncludeGst,
                row.PriceCheckFreshnessHours,
                row.UpdatedAtUtc),
            StringComparer.Ordinal);
    }
}
