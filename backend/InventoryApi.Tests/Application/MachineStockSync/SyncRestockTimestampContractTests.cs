using System.Text.Json;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// Reproduces the defect issue #237 describes: a persisted <see cref="NayaxMachineStockEvent"/> is
/// normalised to a UTC instant at import (<c>SyncMachineStockFromNayax.AsUtc</c>, issue #217), but
/// Microsoft's SQLite EF Core provider - the only provider this API ever runs against, see
/// Program.cs - does not round-trip <see cref="DateTimeKind"/>: a value freshly queried back from
/// the database (exactly what every Sync Restock preview request does) materialises with
/// <see cref="DateTimeKind.Unspecified"/>. System.Text.Json then serialises
/// <c>eventDateTimeGmt</c> without a trailing "Z"/offset, so the JSON instant is ambiguous and the
/// Angular <c>BusinessDateTimePipe</c> parses it as browser-local time instead of UTC - the same
/// defect class issue #230/#232 fixed for Stock History/Transaction Sales.
///
/// These tests use a real (non-InMemory) Sqlite provider deliberately: EF Core's InMemory provider
/// keeps the original CLR object and does not reproduce the Kind loss, so it would not catch this
/// regression. Each test seeds the event directly into <see cref="AppDbContext.NayaxMachineStockEvents"/>
/// and round-trips it through a fresh context and the real <see cref="SyncMachineStockFromNayax"/>
/// use case with a Nayax client that reports no new alerts, so the previewed event is exactly what a
/// second Sync Restock request would read back after import.
/// </summary>
public class SyncRestockTimestampContractTests
{
    private const long MachineId = 900;
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    // 2026-06-15T00:00:00Z is Australian winter - outside daylight saving (AEST, UTC+10).
    private static readonly DateTime AestInstant = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    // 2026-01-15T00:00:00Z is Australian summer - inside daylight saving (AEDT, UTC+11).
    private static readonly DateTime AedtInstant = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    // 2026-06-14T14:30:00Z is still 14 June in UTC, but AEST (UTC+10) has already crossed into 15 June.
    private static readonly DateTime DateBoundaryInstant = new(2026, 6, 14, 14, 30, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> KnownInstants()
    {
        yield return new object[] { AestInstant };
        yield return new object[] { AedtInstant };
        yield return new object[] { DateBoundaryInstant };
    }

    [Theory]
    [MemberData(nameof(KnownInstants))]
    public async Task Sync_restock_preview_preserves_the_UTC_instant_identity_of_the_event_time_across_a_real_Sqlite_round_trip(
        DateTime utcInstant)
    {
        await using var connection = await CreateSqliteAsync();
        await using (var setup = Context(connection))
        {
            setup.NayaxMachineStockEvents.Add(new NayaxMachineStockEvent
            {
                NayaxEventLogId = 1,
                MachineId = MachineId,
                EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
                EventDateTimeGmt = utcInstant,
                RawEventData = "Adjusted Stock, Product MDB: 13 | Beef Jerky | 2",
                MatchStatus = NayaxStockEventMatchStatus.NeedsReview,
                NeedsReviewReason = "Unknown MDB",
                ProcessingStatus = NayaxStockEventProcessingStatus.Unprocessed
            });
            await setup.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var store = new EfMachineStockEventStore(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db));
        var useCase = new SyncMachineStockFromNayax(NoNewAlertsClient().Object, store);

        var preview = await useCase.Handle(MachineId, CancellationToken.None);
        var evt = Assert.Single(preview.Events);

        // This is the actual boundary the frontend depends on: does a value freshly read back from
        // SQLite still carry an unambiguous UTC instant once serialized?
        var json = JsonSerializer.Serialize(evt, WebDefaults);
        using var document = JsonDocument.Parse(json);
        var eventDateTimeGmtJson = document.RootElement.GetProperty("eventDateTimeGmt").GetString();

        Assert.NotNull(eventDateTimeGmtJson);
        Assert.True(
            eventDateTimeGmtJson!.EndsWith("Z", StringComparison.Ordinal) || eventDateTimeGmtJson.Contains('+', StringComparison.Ordinal),
            $"Expected an unambiguous UTC instant (trailing 'Z' or an explicit offset) but got '{eventDateTimeGmtJson}', " +
            "which the frontend would parse as browser-local time instead of the persisted UTC instant.");
        Assert.Equal(utcInstant, document.RootElement.GetProperty("eventDateTimeGmt").GetDateTime().ToUniversalTime());
    }

    /// <summary>
    /// Directly proves the acceptance criterion "verify whether a persisted
    /// <c>NayaxMachineStockEvent.EventDateTimeGMT</c> can return from EF/SQLite with
    /// <c>DateTimeKind.Unspecified</c>" against the store adapter Sync Restock actually reads
    /// through, independent of JSON serialization.
    /// </summary>
    [Fact]
    public async Task Event_time_materialises_as_Utc_kind_after_a_real_Sqlite_round_trip_through_the_store_adapter()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var setup = Context(connection))
        {
            setup.NayaxMachineStockEvents.Add(new NayaxMachineStockEvent
            {
                NayaxEventLogId = 1,
                MachineId = MachineId,
                EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
                EventDateTimeGmt = AestInstant,
                RawEventData = "Adjusted Stock, Product MDB: 13 | Beef Jerky | 2",
                MatchStatus = NayaxStockEventMatchStatus.NeedsReview,
                NeedsReviewReason = "Unknown MDB",
                ProcessingStatus = NayaxStockEventProcessingStatus.Unprocessed
            });
            await setup.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var store = new EfMachineStockEventStore(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db));

        var page = await store.GetUnprocessedEventsAsync(MachineId, CancellationToken.None);
        var pending = Assert.Single(page.Events);

        Assert.Equal(DateTimeKind.Utc, pending.EventDateTimeGmt.Kind);
        Assert.Equal(AestInstant, pending.EventDateTimeGmt);
    }

    private static Mock<INayaxLynxClient> NoNewAlertsClient()
    {
        var mock = new Mock<INayaxLynxClient>();
        mock.Setup(x => x.GetMachineLastAlertsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineAlert>());
        return mock;
    }

    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = Context(connection);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    private static AppDbContext Context(SqliteConnection connection) =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
}
