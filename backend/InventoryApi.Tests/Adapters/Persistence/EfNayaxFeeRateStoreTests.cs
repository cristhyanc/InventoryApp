using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

public class EfNayaxFeeRateStoreTests
{
    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = new AppDbContext(options);
        await setup.Database.EnsureCreatedAsync();
        return (connection, options);
    }

    [Fact]
    public async Task Add_persists_row_with_normalized_date_and_given_timestamp()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = new AppDbContext(options);
            var store = new EfNayaxFeeRateStore(db);

            var createdAt = new DateTime(2026, 3, 1, 4, 5, 6, DateTimeKind.Utc);
            var record = await store.AddAsync(new DateTime(2026, 3, 1), 0.19m, createdAt, CancellationToken.None);

            Assert.Equal(new DateTime(2026, 3, 1), record.EffectiveFrom);
            Assert.Equal(createdAt, record.CreatedAt);
            Assert.Equal(0.19m, record.FeeExGst);

            await using var verify = new AppDbContext(options);
            var persisted = Assert.Single(await verify.NayaxProcessingFeeRates.ToListAsync());
            Assert.Equal(record.Id, persisted.Id);
            Assert.Equal(new DateTime(2026, 3, 1), persisted.EffectiveFrom);
            Assert.Equal(createdAt, persisted.CreatedAt);
        }
    }

    [Fact]
    public async Task FindByEffectiveDateAsync_ignores_time_of_day_on_stored_and_queried_dates()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = new AppDbContext(options);
            var store = new EfNayaxFeeRateStore(db);
            await store.AddAsync(new DateTime(2026, 3, 1), 0.19m, DateTime.UtcNow, CancellationToken.None);

            var found = await store.FindByEffectiveDateAsync(new DateTime(2026, 3, 1, 23, 59, 0), CancellationToken.None);

            Assert.NotNull(found);
        }
    }

    [Fact]
    public async Task UpdateFeeAsync_changes_fee_but_preserves_id_effective_date_and_created_at()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = new AppDbContext(options);
            var store = new EfNayaxFeeRateStore(db);
            var originalCreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var created = await store.AddAsync(new DateTime(2026, 3, 1), 0.17m, originalCreatedAt, CancellationToken.None);

            var updated = await store.UpdateFeeAsync(created.Id, 0.25m, CancellationToken.None);

            Assert.Equal(created.Id, updated.Id);
            Assert.Equal(created.EffectiveFrom, updated.EffectiveFrom);
            Assert.Equal(originalCreatedAt, updated.CreatedAt);
            Assert.Equal(0.25m, updated.FeeExGst);

            await using var verify = new AppDbContext(options);
            Assert.Single(await verify.NayaxProcessingFeeRates.ToListAsync());
        }
    }

    [Fact]
    public async Task ListOrderedByEffectiveDateDescendingAsync_returns_rates_newest_first()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = new AppDbContext(options);
            var store = new EfNayaxFeeRateStore(db);
            await store.AddAsync(new DateTime(2026, 1, 1), 0.10m, DateTime.UtcNow, CancellationToken.None);
            await store.AddAsync(new DateTime(2026, 3, 1), 0.20m, DateTime.UtcNow, CancellationToken.None);
            await store.AddAsync(new DateTime(2026, 2, 1), 0.15m, DateTime.UtcNow, CancellationToken.None);

            var list = await store.ListOrderedByEffectiveDateDescendingAsync(CancellationToken.None);

            Assert.Equal(
                new[] { new DateTime(2026, 3, 1), new DateTime(2026, 2, 1), new DateTime(2026, 1, 1) },
                list.Select(x => x.EffectiveFrom));
        }
    }
}
