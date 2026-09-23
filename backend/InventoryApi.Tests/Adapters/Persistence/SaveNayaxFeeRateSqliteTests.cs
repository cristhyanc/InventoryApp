using Inventory.Application.NayaxFeeSettings;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Tests.Application.NayaxFeeSettings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational evidence that the save use case and the real EF adapter together keep one row per
/// effective calendar date, as the endpoint did before the slice was extracted.
/// </summary>
public class SaveNayaxFeeRateSqliteTests
{
    [Fact]
    public async Task Saving_twice_on_the_same_calendar_date_updates_the_single_row_in_place()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (connection)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            await using (var setup = new AppDbContext(options))
                await setup.Database.EnsureCreatedAsync();

            var originalCreatedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            var clock = new FakeClock(originalCreatedAt);

            // Each save uses its own DbContext, the way two HTTP requests would.
            SaveNayaxFeeRateResult first;
            await using (var db = new AppDbContext(options))
                first = await new SaveNayaxFeeRate(new EfNayaxFeeRateStore(db), clock)
                    .Handle(0.17m, new DateTime(2026, 3, 1, 9, 30, 0), CancellationToken.None);

            clock.UtcNow = new DateTime(2026, 6, 2, 8, 15, 0, DateTimeKind.Utc);

            SaveNayaxFeeRateResult second;
            await using (var db = new AppDbContext(options))
                second = await new SaveNayaxFeeRate(new EfNayaxFeeRateStore(db), clock)
                    .Handle(0.21m, new DateTime(2026, 3, 1, 18, 45, 0), CancellationToken.None);

            Assert.True(first.IsValid);
            Assert.True(second.IsValid);
            Assert.NotNull(first.Record);
            Assert.NotNull(second.Record);

            await using var verify = new AppDbContext(options);
            var row = Assert.Single(await verify.NayaxProcessingFeeRates.ToListAsync());
            Assert.Equal(new DateTime(2026, 3, 1), row.EffectiveFrom);
            Assert.Equal(row.EffectiveFrom.Date, row.EffectiveFrom);
            Assert.Equal(first.Record.Id, row.Id);
            Assert.Equal(originalCreatedAt, row.CreatedAt);
            Assert.Equal(0.21m, row.FeeExGst);

            Assert.Equal(first.Record.Id, second.Record.Id);
            Assert.Equal(new DateTime(2026, 3, 1), second.Record.EffectiveFrom);
            Assert.Equal(originalCreatedAt, second.Record.CreatedAt);
            Assert.Equal(0.21m, second.Record.FeeExGst);
        }
    }
}
