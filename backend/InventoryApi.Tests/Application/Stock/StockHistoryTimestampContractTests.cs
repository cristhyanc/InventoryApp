using System.Text.Json;
using Inventory.Application.Stock;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.Stock;

/// <summary>
/// Reproduces the production defect from issue #230: Stock History can render a manual-restock
/// timestamp as the raw UTC clock value instead of Canberra local time, even though the template
/// already uses <c>businessDateTime</c>.
///
/// Root cause: <see cref="StockAdjustment.CreatedAt"/> is always assigned
/// <see cref="DateTime.UtcNow"/> (<see cref="DateTimeKind.Utc"/>), but Microsoft's SQLite EF Core
/// provider - the only provider this API ever runs against, see Program.cs - does not round-trip
/// <see cref="DateTimeKind"/>: a value freshly queried back from the database (exactly what
/// <see cref="GetStockHistory"/> does for every request, issue #282) always materialises with
/// <see cref="DateTimeKind.Unspecified"/>. System.Text.Json then serialises it without a trailing
/// "Z"/offset, so the JSON instant is ambiguous. The Angular <c>BusinessDateTimePipe</c> parses an
/// unmarked string as browser-local time (see business-date-time.pipe.spec.ts, which already
/// proves the pipe itself converts a properly UTC-marked instant to Canberra time correctly) -
/// which, for an operator whose browser is already set to Australia/Canberra, reproduces exactly
/// the "still shows the UTC clock value" symptom, because the local parse plus the Canberra
/// re-render cancel out instead of composing.
///
/// These tests use a real (non-InMemory) Sqlite provider deliberately: EF Core's InMemory
/// provider keeps the original CLR object and does not reproduce the Kind loss, so it would not
/// catch this regression. Serializing the Application-layer <see cref="StockAdjustmentRecord"/>
/// directly, rather than the mapped API response entity, follows the same precedent
/// <c>SyncRestockTimestampContractTests</c> established (issue #237): the defect is a plain
/// <see cref="DateTime"/> property with no custom converter on either shape, so the Kind loss and
/// its serialized ambiguity reproduce identically at this boundary.
/// </summary>
public class StockHistoryTimestampContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    // 2024-06-15T00:00:00Z is Australian winter - outside daylight saving (AEST, UTC+10).
    private static readonly DateTime AestInstant = new(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    // 2024-01-15T00:00:00Z is Australian summer - inside daylight saving (AEDT, UTC+11).
    private static readonly DateTime AedtInstant = new(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [MemberData(nameof(KnownInstants))]
    public async Task History_preserves_the_UTC_instant_identity_of_CreatedAt_across_a_real_Sqlite_round_trip(
        DateTime utcInstant)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var setup = TestAppDbContext.Unrestricted(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 5 });
            setup.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = 1,
                QuantityChange = 5,
                QuantityAfter = 5,
                Reason = StockAdjustmentReason.Restock,
                Source = StockAdjustmentSource.Manual,
                CreatedAt = utcInstant,
                EffectiveAt = utcInstant
            });
            await setup.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.Unrestricted(options);
        var useCase = new GetStockHistory(new EfStockAdjustmentStore(db, new InventoryCostService(db), new InventoryCostRebuildService(db)));
        var history = await useCase.Handle(1, CancellationToken.None);

        var adjustment = Assert.Single(history);

        // This is the actual boundary the frontend depends on: does a value freshly read back
        // from SQLite still carry an unambiguous UTC instant once serialized?
        var json = JsonSerializer.Serialize(adjustment, WebDefaults);
        using var document = JsonDocument.Parse(json);
        var createdAtJson = document.RootElement.GetProperty("createdAt").GetString();

        Assert.NotNull(createdAtJson);
        Assert.True(
            createdAtJson!.EndsWith("Z", StringComparison.Ordinal) || createdAtJson.Contains('+', StringComparison.Ordinal),
            $"Expected an unambiguous UTC instant (trailing 'Z' or an explicit offset) but got '{createdAtJson}', " +
            "which the frontend would parse as browser-local time instead of the persisted UTC instant.");
        Assert.Equal(utcInstant, document.RootElement.GetProperty("createdAt").GetDateTime().ToUniversalTime());
    }

    public static IEnumerable<object[]> KnownInstants()
    {
        yield return new object[] { AestInstant };
        yield return new object[] { AedtInstant };
    }
}
