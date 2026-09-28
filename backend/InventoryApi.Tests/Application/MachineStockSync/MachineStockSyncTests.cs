using System.Text.Json;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// The Sync Restock reconciliation workflow (issue #183) as the Application use cases
/// <see cref="SyncMachineStockFromNayax"/>/<see cref="ApplyMachineStockSync"/> now implement it:
/// import Nayax Event 501 stock-adjustment alerts, resolve them to a local product via Machine +
/// MDB, preview the storage impact, and apply only explicitly accepted events through the existing
/// inventory/costing movement logic. The use cases are exercised over the real
/// <see cref="EfMachineStockEventStore"/> adapter rather than a stub, so the costing invariants are
/// covered end to end.
///
/// The provider differs deliberately by what a test proves. Application/domain behaviour - parsing,
/// matching, the apply/duplicate rulings, preview shape - uses the EF Core InMemory provider via
/// <see cref="CreateInMemoryDb"/>. Behaviour that depends on a real transaction, on constraints, or
/// on what is actually committed uses relational SQLite via <see cref="CreateSqliteDbAsync"/>,
/// because InMemory is not relational: <see cref="EfMachineStockEventStore.ApplyRefillAsync"/> only
/// opens a database transaction when <c>Database.IsRelational()</c>, so an InMemory test cannot
/// prove that a movement and its event state commit or roll back together. See the "Transactional
/// safety" and "Relational duplicate resolution" regions.
/// </summary>
public class MachineStockSyncTests
{
    private const long MachineId = 900;
    private const long ProductId = 200;
    private static readonly DateTime EventTime = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateInMemoryDb(string dbName) =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options);

    /// <summary>
    /// Opens <paramref name="connection"/>, creates the schema on it, and returns options for a
    /// relational SQLite database. The caller keeps the connection alive for the whole test, because
    /// a <c>:memory:</c> database exists only as long as its connection does. Returning options
    /// rather than a context lets a test read back what was committed through a fresh context, which
    /// is the only way to tell a real commit from tracked in-memory state.
    /// </summary>
    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteDbAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var schema = TestAppDbContext.Unrestricted(options);
        await schema.Database.EnsureCreatedAsync();
        return options;
    }

    private static (SyncMachineStockFromNayax Sync, ApplyMachineStockSync Apply) UseCases(
        AppDbContext db,
        INayaxLynxClient nayax,
        IInventoryCostRebuildService? rebuild = null)
    {
        var store = new EfMachineStockEventStore(
            db, new InventoryCostService(db), rebuild ?? new InventoryCostRebuildService(db));
        return (new SyncMachineStockFromNayax(nayax, store), new ApplyMachineStockSync(store));
    }

    private static ResolveMachineStockDuplicate ResolveUseCase(
        AppDbContext db, IInventoryCostRebuildService? rebuild = null) =>
        new(new EfMachineStockEventStore(
            db, new InventoryCostService(db), rebuild ?? new InventoryCostRebuildService(db)));

    private static Product SeedCostedProduct(
        AppDbContext db, long id, string name, int quantityInStock, decimal unitCost = 1m)
    {
        var product = new Product
        {
            Id = id,
            Name = name,
            QuantityInStock = quantityInStock,
            CostingQuantity = quantityInStock,
            InventoryValue = quantityInStock * unitCost,
            AverageUnitCost = unitCost
        };
        db.Products.Add(product);
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = id,
            QuantityChange = quantityInStock,
            QuantityAfter = quantityInStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = unitCost,
            EffectiveAt = EventTime.AddDays(-2)
        });
        return product;
    }

    private static NayaxMachineAlert StockAlert(long eventLogId, string eventData, DateTime? eventDateTimeGmt = null) => new()
    {
        EventLogId = eventLogId,
        MachineId = MachineId,
        EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
        EventDescription = "Stock Adjust for Machine",
        EventData = eventData,
        EventDateTimeGmt = eventDateTimeGmt ?? EventTime,
        // The machine clock in Sydney (AEST, UTC+10).
        EventDateTimeVmc = DateTime.SpecifyKind((eventDateTimeGmt ?? EventTime).AddHours(10), DateTimeKind.Unspecified)
    };

    private static Mock<INayaxLynxClient> NayaxClientReturning(
        List<NayaxMachineAlert> alerts, List<NayaxMachineProduct>? machineProducts = null)
    {
        var mock = new Mock<INayaxLynxClient>();
        mock.Setup(x => x.GetMachineLastAlertsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alerts);
        mock.Setup(x => x.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machineProducts ?? new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = ProductId, MDBCode = 13, ProductName = "25g Nobby's Beef Jerky Hot" }
            });
        return mock;
    }

    #region Parsing and matching

    [Fact]
    public async Task Matches_product_via_machine_and_mdb_and_previews_it_as_matched()
    {
        using var db = CreateInMemoryDb(nameof(Matches_product_via_machine_and_mdb_and_previews_it_as_matched));
        SeedCostedProduct(db, ProductId, "25g Nobby's Beef Jerky Hot", 20);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Eunhye Chung 'Adjusted Stock, Product MDB: 13 | 25g Nobby's Beef Jerky Hot | 2")
        ]);
        var (sync, _) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        Assert.Equal(1, preview.NewEventCount);
        var evt = Assert.Single(preview.Events);
        Assert.Equal(NayaxStockEventMatchStatus.Matched, evt.MatchStatus);
        Assert.Equal(ProductId, evt.MatchedProductId);
        Assert.Equal(2, evt.ParsedQuantity);
        Assert.Equal(20, evt.AvailableStorageQuantity);
        Assert.False(evt.IsInsufficientStock);
        Assert.False(evt.IsDiscrepancy);
    }

    [Fact]
    public async Task Same_product_on_two_mdbs_combines_into_one_impact_row_while_events_stay_individually_auditable()
    {
        using var db = CreateInMemoryDb(nameof(Same_product_on_two_mdbs_combines_into_one_impact_row_while_events_stay_individually_auditable));
        SeedCostedProduct(db, ProductId, "Nu Pure Spring Water 600mL", 20);
        await db.SaveChangesAsync();

        var machineProducts = new List<NayaxMachineProduct>
        {
            new() { NayaxProductID = ProductId, MDBCode = 31, ProductName = "Nu Pure Spring Water 600mL" },
            new() { NayaxProductID = ProductId, MDBCode = 32, ProductName = "Nu Pure Spring Water 600mL" }
        };
        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 31 | Nu Pure Spring Water 600mL | 5"),
            StockAlert(2, "Product MDB: 32 | Nu Pure Spring Water 600mL | 1")
        ], machineProducts);
        var (sync, _) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        Assert.Equal(2, preview.Events.Count);
        var impact = Assert.Single(preview.ProductImpacts);
        Assert.Equal(ProductId, impact.ProductId);
        Assert.Equal(6, impact.PendingRefillQuantity);
        Assert.Equal(20, impact.AvailableStorageQuantity);
    }

    [Fact]
    public async Task Tolerates_harmless_case_and_whitespace_formatting_differences()
    {
        using var db = CreateInMemoryDb(nameof(Tolerates_harmless_case_and_whitespace_formatting_differences));
        SeedCostedProduct(db, ProductId, "Coke 375mL", 10);
        await db.SaveChangesAsync();

        var machineProducts = new List<NayaxMachineProduct>
        {
            new() { NayaxProductID = ProductId, MDBCode = 7, ProductName = "Coke 375mL" }
        };
        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 7 |   coke 375ML   | 1")
        ], machineProducts);
        var (sync, _) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        var evt = Assert.Single(preview.Events);
        Assert.Equal(NayaxStockEventMatchStatus.Matched, evt.MatchStatus);
    }

    [Fact]
    public async Task Material_product_name_mismatch_needs_review_and_causes_no_movement()
    {
        using var db = CreateInMemoryDb(nameof(Material_product_name_mismatch_needs_review_and_causes_no_movement));
        SeedCostedProduct(db, ProductId, "Coke 375mL", 10);
        await db.SaveChangesAsync();

        var machineProducts = new List<NayaxMachineProduct>
        {
            new() { NayaxProductID = ProductId, MDBCode = 7, ProductName = "Coke 375mL" }
        };
        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 7 | Sprite 375mL | 1")
        ], machineProducts);
        var (sync, apply) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        var evt = Assert.Single(preview.Events);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, evt.MatchStatus);
        Assert.Contains("mismatch", evt.NeedsReviewReason, StringComparison.OrdinalIgnoreCase);

        var applyResult = await apply.Handle(MachineId, [evt.Id], CancellationToken.None);
        Assert.Equal(NayaxStockEventApplyOutcome.NotMatched, applyResult.Results[0].Outcome);
        Assert.Equal(10, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
    }

    [Fact]
    public async Task Unknown_mdb_needs_review_and_causes_no_movement()
    {
        using var db = CreateInMemoryDb(nameof(Unknown_mdb_needs_review_and_causes_no_movement));
        SeedCostedProduct(db, ProductId, "Coke 375mL", 10);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 99 | Coke 375mL | 1")
        ], []);
        var (sync, _) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        var evt = Assert.Single(preview.Events);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, evt.MatchStatus);
        Assert.Contains("MDB 99", evt.NeedsReviewReason);
        Assert.Null(evt.MatchedProductId);
    }

    [Fact]
    public async Task Malformed_event_data_needs_review_and_causes_no_movement()
    {
        using var db = CreateInMemoryDb(nameof(Malformed_event_data_needs_review_and_causes_no_movement));
        SeedCostedProduct(db, ProductId, "Coke 375mL", 10);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([StockAlert(1, "Nothing useful here")]);
        var (sync, _) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        var evt = Assert.Single(preview.Events);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, evt.MatchStatus);
        Assert.Null(evt.ParsedMdb);
        Assert.Null(evt.MatchedProductId);
    }

    #endregion

    #region Idempotency

    [Fact]
    public async Task Re_syncing_the_same_event_does_not_create_a_duplicate_or_a_second_deduction()
    {
        using var db = CreateInMemoryDb(nameof(Re_syncing_the_same_event_does_not_create_a_duplicate_or_a_second_deduction));
        SeedCostedProduct(db, ProductId, "25g Nobby's Beef Jerky Hot", 20);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 13 | 25g Nobby's Beef Jerky Hot | 2")
        ]);
        var (sync, apply) = UseCases(db, nayax.Object);

        var first = await sync.Handle(MachineId, CancellationToken.None);
        Assert.Equal(1, first.NewEventCount);
        var eventId = first.Events[0].Id;

        var applied = await apply.Handle(MachineId, [eventId], CancellationToken.None);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, applied.Results[0].Outcome);

        // Re-fetching the same alert must not create a second event or a second deduction.
        var second = await sync.Handle(MachineId, CancellationToken.None);
        Assert.Equal(0, second.NewEventCount);
        Assert.Empty(second.Events);
        Assert.Equal("No new Nayax stock-adjustment alerts to review.", second.Message);

        Assert.Equal(18, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
        Assert.Single(await db.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
        Assert.Single(await db.NayaxMachineStockEvents.ToListAsync());
    }

    [Fact]
    public async Task Idempotency_is_keyed_on_event_log_id_not_on_event_content()
    {
        using var db = CreateInMemoryDb(nameof(Idempotency_is_keyed_on_event_log_id_not_on_event_content));
        SeedCostedProduct(db, ProductId, "25g Nobby's Beef Jerky Hot", 20);
        await db.SaveChangesAsync();

        // Two distinct Nayax event log entries with identical content are two real adjustments; the
        // same EventLogID repeated within one response is one.
        const string eventData = "Product MDB: 13 | 25g Nobby's Beef Jerky Hot | 2";
        var nayax = NayaxClientReturning([
            StockAlert(1001, eventData),
            StockAlert(1002, eventData),
            StockAlert(1002, eventData)
        ]);
        var (sync, _) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        Assert.Equal(2, preview.NewEventCount);
        Assert.Equal([1001L, 1002L], preview.Events.Select(e => e.NayaxEventLogId).Order());
    }

    [Fact]
    public async Task Imported_event_persists_the_documented_nayax_identity_timestamps_and_source_alert()
    {
        using var db = CreateInMemoryDb(nameof(Imported_event_persists_the_documented_nayax_identity_timestamps_and_source_alert));
        SeedCostedProduct(db, ProductId, "25g Nobby's Beef Jerky Hot", 20);
        await db.SaveChangesAsync();

        const string eventData = "Eunhye Chung 'Adjusted Stock, Product MDB: 13 | 25g Nobby's Beef Jerky Hot | 2";
        var alert = StockAlert(
            5550001, eventData, DateTime.SpecifyKind(new DateTime(2026, 9, 1, 10, 0, 0), DateTimeKind.Unspecified));
        alert.EventSourceId = 3;
        alert.EventSourceName = "Nayax Core";
        alert.EventGroupId = 12;
        alert.EventGroupName = "Inventory";
        alert.EventCategoryId = 4;
        alert.EventCategoryName = "Information";
        alert.SiteId = 2;
        alert.EntityTypeId = 1;
        alert.EntityTypeName = "Machine";
        var (sync, _) = UseCases(db, NayaxClientReturning([alert]).Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        var evt = Assert.Single(preview.Events);
        Assert.Equal(5550001, evt.NayaxEventLogId);
        // EventDateTimeGMT without an offset is GMT, never server-local time.
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), evt.EventDateTimeGmt);
        Assert.Equal(DateTimeKind.Utc, evt.EventDateTimeGmt.Kind);
        Assert.Equal(new DateTime(2026, 9, 1, 20, 0, 0), evt.EventDateTimeVmc);
        Assert.Equal(eventData, evt.RawEventData);

        var stored = await db.NayaxMachineStockEvents.SingleAsync();
        Assert.Equal(5550001, stored.NayaxEventLogId);
        Assert.Equal(eventData, stored.RawEventData);

        using var source = JsonDocument.Parse(stored.RawSourceMetadata!);
        Assert.Equal(5550001, source.RootElement.GetProperty("EventLogID").GetInt64());
        Assert.Equal("Stock Adjust for Machine", source.RootElement.GetProperty("EventDescription").GetString());
        Assert.Equal("Nayax Core", source.RootElement.GetProperty("EventSourceName").GetString());
        Assert.Equal("Inventory", source.RootElement.GetProperty("EventGroupName").GetString());
        Assert.Equal("Information", source.RootElement.GetProperty("EventCategoryName").GetString());
        Assert.Equal(eventData, source.RootElement.GetProperty("EventData").GetString());
        Assert.True(source.RootElement.TryGetProperty("EventDateTimeVMC", out _));
        Assert.False(source.RootElement.TryGetProperty("EventName", out _));
    }

    [Fact]
    public async Task Re_applying_an_already_applied_event_never_deducts_a_second_time()
    {
        using var db = CreateInMemoryDb(nameof(Re_applying_an_already_applied_event_never_deducts_a_second_time));
        SeedCostedProduct(db, ProductId, "25g Nobby's Beef Jerky Hot", 20);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 13 | 25g Nobby's Beef Jerky Hot | 2")
        ]);
        var (sync, apply) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);
        var eventId = preview.Events[0].Id;
        var first = await apply.Handle(MachineId, [eventId], CancellationToken.None);

        var second = await apply.Handle(MachineId, [eventId], CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.Applied, second.Results[0].Outcome);
        Assert.Equal("Already applied.", second.Results[0].Message);
        Assert.Equal(first.Results[0].StockAdjustmentId, second.Results[0].StockAdjustmentId);
        Assert.Equal(18, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
        Assert.Single(await db.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
    }

    [Fact]
    public async Task No_new_alerts_produces_a_clear_empty_state_message()
    {
        using var db = CreateInMemoryDb(nameof(No_new_alerts_produces_a_clear_empty_state_message));
        var nayax = NayaxClientReturning([]);
        var (sync, _) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        Assert.Empty(preview.Events);
        Assert.Equal("No new Nayax stock-adjustment alerts to review.", preview.Message);
    }

    #endregion

    #region Applying a validated positive refill

    [Fact]
    public async Task Valid_positive_refill_reduces_storage_and_preserves_costing_invariants()
    {
        using var db = CreateInMemoryDb(nameof(Valid_positive_refill_reduces_storage_and_preserves_costing_invariants));
        SeedCostedProduct(db, ProductId, "25g Nobby's Beef Jerky Hot", 20, unitCost: 1.5m);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 13 | 25g Nobby's Beef Jerky Hot | 8")
        ]);
        var (sync, apply) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);
        var result = await apply.Handle(MachineId, [preview.Events[0].Id], CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Results[0].Outcome);
        var product = await db.Products.FindAsync(ProductId);
        Assert.Equal(12, product!.QuantityInStock);
        Assert.Equal(20, product.CostingQuantity); // internal transfer never touches costing quantity/value
        Assert.Equal(1.5m, product.AverageUnitCost);

        var movement = await db.StockAdjustments.SingleAsync(x => x.Reason == StockAdjustmentReason.MachineRefill);
        Assert.Equal(StockAdjustmentSource.Nayax, movement.Source);
        Assert.Equal(-8, movement.QuantityChange);
        Assert.Equal(MachineId, movement.MachineId);

        var evt = await db.NayaxMachineStockEvents.SingleAsync();
        Assert.Equal(NayaxStockEventProcessingStatus.Applied, evt.ProcessingStatus);
        Assert.Equal(movement.Id, evt.StockAdjustmentId);
        Assert.NotNull(evt.ProcessedAt);
    }

    #endregion

    #region Insufficient storage

    [Fact]
    public async Task Positive_adjustment_exceeding_storage_is_not_partially_applied_and_storage_never_goes_negative()
    {
        using var db = CreateInMemoryDb(nameof(Positive_adjustment_exceeding_storage_is_not_partially_applied_and_storage_never_goes_negative));
        SeedCostedProduct(db, ProductId, "Nu Pure Spring Water 600mL", 4);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 13 | Nu Pure Spring Water 600mL | 5")
        ], [new() { NayaxProductID = ProductId, MDBCode = 13, ProductName = "Nu Pure Spring Water 600mL" }]);
        var (sync, apply) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);
        var previewEvent = preview.Events[0];
        Assert.True(previewEvent.IsInsufficientStock);
        Assert.Equal(4, previewEvent.AvailableStorageQuantity);
        Assert.Equal(1, previewEvent.UnaccountedDifference);

        var result = await apply.Handle(MachineId, [previewEvent.Id], CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.InsufficientStock, result.Results[0].Outcome);
        Assert.Equal(4, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
        Assert.Empty(await db.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
        Assert.Equal(NayaxStockEventProcessingStatus.Unprocessed, (await db.NayaxMachineStockEvents.SingleAsync()).ProcessingStatus);
    }

    #endregion

    #region Negative discrepancy

    [Fact]
    public async Task Negative_adjustment_is_retained_as_a_discrepancy_and_never_increases_storage_or_applies()
    {
        using var db = CreateInMemoryDb(nameof(Negative_adjustment_is_retained_as_a_discrepancy_and_never_increases_storage_or_applies));
        SeedCostedProduct(db, ProductId, "Coke 375mL", 10);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 7 | Coke 375mL | -3")
        ], [new() { NayaxProductID = ProductId, MDBCode = 7, ProductName = "Coke 375mL" }]);
        var (sync, apply) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);
        var previewEvent = preview.Events[0];
        Assert.True(previewEvent.IsDiscrepancy);
        Assert.Equal(-3, previewEvent.ParsedQuantity);

        var result = await apply.Handle(MachineId, [previewEvent.Id], CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.NotApplicable, result.Results[0].Outcome);
        Assert.Equal(10, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
        Assert.Empty(await db.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
    }

    #endregion

    #region Mixed batches

    [Fact]
    public async Task Valid_events_apply_while_needs_review_events_in_the_same_batch_remain_unapplied()
    {
        using var db = CreateInMemoryDb(nameof(Valid_events_apply_while_needs_review_events_in_the_same_batch_remain_unapplied));
        SeedCostedProduct(db, ProductId, "Coke 375mL", 10);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 7 | Coke 375mL | 2"),
            StockAlert(2, "not parseable at all")
        ], [new() { NayaxProductID = ProductId, MDBCode = 7, ProductName = "Coke 375mL" }]);
        var (sync, apply) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);
        Assert.Equal(2, preview.Events.Count);
        var ids = preview.Events.Select(e => e.Id).ToArray();

        var result = await apply.Handle(MachineId, ids, CancellationToken.None);

        var validResult = result.Results.Single(r => r.EventId == preview.Events.Single(e => e.MatchStatus == NayaxStockEventMatchStatus.Matched).Id);
        var invalidResult = result.Results.Single(r => r.EventId == preview.Events.Single(e => e.MatchStatus == NayaxStockEventMatchStatus.NeedsReview).Id);

        Assert.Equal(NayaxStockEventApplyOutcome.Applied, validResult.Outcome);
        Assert.Equal(NayaxStockEventApplyOutcome.NotMatched, invalidResult.Outcome);
        Assert.Equal(8, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
    }

    [Fact]
    public async Task An_event_belonging_to_another_machine_is_never_applied_through_this_machine()
    {
        using var db = CreateInMemoryDb(nameof(An_event_belonging_to_another_machine_is_never_applied_through_this_machine));
        SeedCostedProduct(db, ProductId, "Coke 375mL", 10);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 7 | Coke 375mL | 2")
        ], [new() { NayaxProductID = ProductId, MDBCode = 7, ProductName = "Coke 375mL" }]);
        var (sync, apply) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        var result = await apply.Handle(MachineId + 1, [preview.Events[0].Id], CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.Error, result.Results[0].Outcome);
        Assert.Equal("Event not found for this machine.", result.Results[0].Message);
        Assert.Equal(10, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
    }

    #endregion

    #region Transactional safety

    [Fact]
    public async Task A_failure_while_applying_rolls_back_so_no_event_is_marked_processed_without_its_movement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var schema = TestAppDbContext.Unrestricted(options))
            await schema.Database.EnsureCreatedAsync();

        await using var db = TestAppDbContext.Unrestricted(options);
        SeedCostedProduct(db, ProductId, "Coke 375mL", 10);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 7 | Coke 375mL | 2")
        ], [new() { NayaxProductID = ProductId, MDBCode = 7, ProductName = "Coke 375mL" }]);

        var failingRebuild = new Mock<IInventoryCostRebuildService>();
        failingRebuild
            .Setup(x => x.RebuildAsync(ProductId, null, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated rebuild failure"));

        var (sync, apply) = UseCases(db, nayax.Object, failingRebuild.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);
        var result = await apply.Handle(MachineId, [preview.Events[0].Id], CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.Error, result.Results[0].Outcome);

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Equal(10, (await verify.Products.FindAsync(ProductId))!.QuantityInStock);
        Assert.Empty(await verify.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
        Assert.Equal(
            NayaxStockEventProcessingStatus.Unprocessed,
            (await verify.NayaxMachineStockEvents.SingleAsync()).ProcessingStatus);
    }

    #endregion

    #region Manual fallback and source distinction

    [Fact]
    public async Task Manual_refill_remains_functional_and_is_source_distinguishable_from_a_nayax_refill()
    {
        using var db = CreateInMemoryDb(nameof(Manual_refill_remains_functional_and_is_source_distinguishable_from_a_nayax_refill));
        SeedCostedProduct(db, ProductId, "Coke 375mL", 20, unitCost: 1m);
        await db.SaveChangesAsync();

        IStockService stockService = new StockService(db);
        await stockService.Adjust(ProductId, new StockAdjustmentDto(-3, StockAdjustmentReason.MachineRefill, "manual restock", MachineId, null));

        var nayax = NayaxClientReturning([
            StockAlert(2, "Product MDB: 7 | Coke 375mL | 4", EventTime.AddHours(3))
        ], [new() { NayaxProductID = ProductId, MDBCode = 7, ProductName = "Coke 375mL" }]);
        var (sync, apply) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);
        await apply.Handle(MachineId, [preview.Events[0].Id], CancellationToken.None);

        var movements = await db.StockAdjustments
            .Where(x => x.Reason == StockAdjustmentReason.MachineRefill)
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(StockAdjustmentSource.Manual, movements[0].Source);
        Assert.Equal(StockAdjustmentSource.Nayax, movements[1].Source);
        Assert.Equal(13, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
    }

    #endregion

    #region Possible duplicate

    [Fact]
    public async Task A_nayax_event_matching_a_recent_manual_refill_is_surfaced_as_a_possible_duplicate()
    {
        using var db = CreateInMemoryDb(nameof(A_nayax_event_matching_a_recent_manual_refill_is_surfaced_as_a_possible_duplicate));
        SeedCostedProduct(db, ProductId, "Coke 375mL", 20, unitCost: 1m);
        await db.SaveChangesAsync();

        IStockService stockService = new StockService(db);
        await stockService.Adjust(ProductId, new StockAdjustmentDto(-4, StockAdjustmentReason.MachineRefill, "operator restocked before Nayax reported it", MachineId, null));
        var manualMovement = await db.StockAdjustments.SingleAsync(x => x.Reason == StockAdjustmentReason.MachineRefill);
        manualMovement.EffectiveAt = EventTime.AddHours(-1);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 7 | Coke 375mL | 4")
        ], [new() { NayaxProductID = ProductId, MDBCode = 7, ProductName = "Coke 375mL" }]);
        var (sync, _) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        var evt = Assert.Single(preview.Events);
        Assert.True(evt.IsPossibleDuplicate);
        Assert.NotNull(evt.PossibleDuplicateNotes);
    }

    [Fact]
    public async Task A_manual_refill_far_outside_the_window_is_not_flagged_as_a_possible_duplicate()
    {
        using var db = CreateInMemoryDb(nameof(A_manual_refill_far_outside_the_window_is_not_flagged_as_a_possible_duplicate));
        SeedCostedProduct(db, ProductId, "Coke 375mL", 20, unitCost: 1m);
        await db.SaveChangesAsync();

        IStockService stockService = new StockService(db);
        await stockService.Adjust(ProductId, new StockAdjustmentDto(-4, StockAdjustmentReason.MachineRefill, "an unrelated much earlier manual restock", MachineId, null));
        var manualMovement = await db.StockAdjustments.SingleAsync(x => x.Reason == StockAdjustmentReason.MachineRefill);
        manualMovement.EffectiveAt = EventTime.AddDays(-30);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 7 | Coke 375mL | 4")
        ], [new() { NayaxProductID = ProductId, MDBCode = 7, ProductName = "Coke 375mL" }]);
        var (sync, _) = UseCases(db, nayax.Object);

        var preview = await sync.Handle(MachineId, CancellationToken.None);

        var evt = Assert.Single(preview.Events);
        Assert.False(evt.IsPossibleDuplicate);
    }

    #endregion

    #region Explicit duplicate resolution: application behaviour, EF InMemory (issue #196)

    /// <summary>
    /// Seeds a product already refilled manually, then previews a matching Nayax event so it is
    /// flagged a possible duplicate, and returns everything a resolution test needs.
    /// </summary>
    private static async Task<(
        SyncMachineStockFromNayax Sync,
        ApplyMachineStockSync Apply,
        ResolveMachineStockDuplicate Resolve,
        int EventId,
        int ManualStockAdjustmentId)> SeedDuplicateScenarioAsync(
        AppDbContext db, IInventoryCostRebuildService? rebuild = null)
    {
        SeedCostedProduct(db, ProductId, "Coke 375mL", 20, unitCost: 1m);
        await db.SaveChangesAsync();

        IStockService stockService = new StockService(db);
        await stockService.Adjust(
            ProductId,
            new StockAdjustmentDto(-4, StockAdjustmentReason.MachineRefill, "operator restocked before Nayax reported it", MachineId, null));
        var manualMovement = await db.StockAdjustments.SingleAsync(x => x.Reason == StockAdjustmentReason.MachineRefill);
        manualMovement.EffectiveAt = EventTime.AddHours(-1);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 7 | Coke 375mL | 4")
        ], [new() { NayaxProductID = ProductId, MDBCode = 7, ProductName = "Coke 375mL" }]);
        var (sync, apply) = UseCases(db, nayax.Object, rebuild);
        var resolve = ResolveUseCase(db, rebuild);

        var preview = await sync.Handle(MachineId, CancellationToken.None);
        var evt = Assert.Single(preview.Events);
        Assert.True(evt.IsPossibleDuplicate);

        return (sync, apply, resolve, evt.Id, manualMovement.Id);
    }

    [Fact]
    public async Task Ordinary_apply_refuses_a_flagged_possible_duplicate()
    {
        using var db = CreateInMemoryDb(nameof(Ordinary_apply_refuses_a_flagged_possible_duplicate));
        var (_, apply, _, eventId, _) = await SeedDuplicateScenarioAsync(db);

        var result = await apply.Handle(MachineId, [eventId], CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.DuplicateRequiresResolution, result.Results[0].Outcome);
        Assert.Equal(16, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
        Assert.Empty(await db.StockAdjustments.Where(x => x.Source == StockAdjustmentSource.Nayax).ToListAsync());
    }

    [Fact]
    public async Task Already_recorded_manually_reconciles_without_changing_storage_or_creating_a_second_adjustment()
    {
        using var db = CreateInMemoryDb(nameof(Already_recorded_manually_reconciles_without_changing_storage_or_creating_a_second_adjustment));
        var (_, _, resolve, eventId, manualAdjustmentId) = await SeedDuplicateScenarioAsync(db);

        var result = await resolve.Handle(
            MachineId, eventId, NayaxDuplicateResolutionChoice.AlreadyRecordedManually, CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.Reconciled, result.Outcome);
        Assert.Null(result.StockAdjustmentId);
        Assert.Equal(16, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
        Assert.Single(await db.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());

        var stored = await db.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Equal(NayaxStockEventProcessingStatus.Unprocessed, stored.ProcessingStatus);
        Assert.Equal(NayaxDuplicateResolution.ReconciledManually, stored.DuplicateResolution);
        Assert.Equal(manualAdjustmentId, stored.MatchedManualStockAdjustmentId);
        Assert.NotNull(stored.DuplicateResolvedAt);
    }

    [Fact]
    public async Task Repeating_already_recorded_manually_is_idempotent()
    {
        using var db = CreateInMemoryDb(nameof(Repeating_already_recorded_manually_is_idempotent));
        var (_, _, resolve, eventId, _) = await SeedDuplicateScenarioAsync(db);

        var first = await resolve.Handle(
            MachineId, eventId, NayaxDuplicateResolutionChoice.AlreadyRecordedManually, CancellationToken.None);
        var second = await resolve.Handle(
            MachineId, eventId, NayaxDuplicateResolutionChoice.AlreadyRecordedManually, CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.Reconciled, first.Outcome);
        Assert.Equal(NayaxStockEventApplyOutcome.Reconciled, second.Outcome);
        Assert.Equal(16, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
        Assert.Single(await db.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
    }

    [Fact]
    public async Task Apply_as_separate_restock_requires_the_explicit_choice_and_deducts_storage_exactly_once()
    {
        using var db = CreateInMemoryDb(nameof(Apply_as_separate_restock_requires_the_explicit_choice_and_deducts_storage_exactly_once));
        var (_, apply, resolve, eventId, manualAdjustmentId) = await SeedDuplicateScenarioAsync(db);

        // The ordinary apply path still refuses it; only the explicit resolution may override it.
        var ordinary = await apply.Handle(MachineId, [eventId], CancellationToken.None);
        Assert.Equal(NayaxStockEventApplyOutcome.DuplicateRequiresResolution, ordinary.Results[0].Outcome);

        var result = await resolve.Handle(
            MachineId, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Outcome);
        Assert.NotNull(result.StockAdjustmentId);
        Assert.Equal(12, (await db.Products.FindAsync(ProductId))!.QuantityInStock);

        var nayaxMovements = await db.StockAdjustments
            .Where(x => x.Reason == StockAdjustmentReason.MachineRefill && x.Source == StockAdjustmentSource.Nayax)
            .ToListAsync();
        Assert.Single(nayaxMovements);
        Assert.Equal(-4, nayaxMovements[0].QuantityChange);

        var stored = await db.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Equal(NayaxStockEventProcessingStatus.Applied, stored.ProcessingStatus);
        Assert.Equal(NayaxDuplicateResolution.AppliedAsSeparateRestock, stored.DuplicateResolution);
        Assert.Equal(manualAdjustmentId, stored.MatchedManualStockAdjustmentId);
        Assert.NotNull(stored.DuplicateResolvedAt);
    }

    [Fact]
    public async Task Repeating_apply_as_separate_restock_never_deducts_twice()
    {
        using var db = CreateInMemoryDb(nameof(Repeating_apply_as_separate_restock_never_deducts_twice));
        var (_, _, resolve, eventId, _) = await SeedDuplicateScenarioAsync(db);

        var first = await resolve.Handle(
            MachineId, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);
        var second = await resolve.Handle(
            MachineId, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.Applied, first.Outcome);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, second.Outcome);
        Assert.Equal(first.StockAdjustmentId, second.StockAdjustmentId);
        Assert.Equal(12, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
        Assert.Single(await db.StockAdjustments.Where(x => x.Source == StockAdjustmentSource.Nayax).ToListAsync());
    }

    [Fact]
    public async Task An_event_that_is_not_a_flagged_duplicate_cannot_be_resolved()
    {
        using var db = CreateInMemoryDb(nameof(An_event_that_is_not_a_flagged_duplicate_cannot_be_resolved));
        SeedCostedProduct(db, ProductId, "25g Nobby's Beef Jerky Hot", 20);
        await db.SaveChangesAsync();

        var nayax = NayaxClientReturning([
            StockAlert(1, "Product MDB: 13 | 25g Nobby's Beef Jerky Hot | 2")
        ]);
        var (sync, _) = UseCases(db, nayax.Object);
        var resolve = ResolveUseCase(db);

        var preview = await sync.Handle(MachineId, CancellationToken.None);
        var eventId = preview.Events[0].Id;
        Assert.False(preview.Events[0].IsPossibleDuplicate);

        var result = await resolve.Handle(
            MachineId, eventId, NayaxDuplicateResolutionChoice.AlreadyRecordedManually, CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.NotApplicable, result.Outcome);
        Assert.Equal(20, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
    }

    #endregion

    #region Explicit duplicate resolution: relational SQLite (issue #196)

    // "Apply as separate restock" is the one duplicate resolution that writes: it creates an
    // inventory movement and the event's resolution state in the same transactional operation. The
    // region above proves the decision logic; these three prove the parts only a relational database
    // can prove - that the movement and the resolution state commit together, that a repeat against
    // committed state cannot deduct again, and that a failure leaves nothing partially committed.

    [Fact]
    public async Task Apply_as_separate_restock_over_sqlite_commits_the_movement_and_its_resolution_state_together()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);

        int eventId;
        int manualAdjustmentId;
        int? returnedAdjustmentId;
        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            var (_, apply, resolve, seededEventId, seededManualId) = await SeedDuplicateScenarioAsync(db);
            eventId = seededEventId;
            manualAdjustmentId = seededManualId;

            // The ordinary apply path still refuses it; only the explicit resolution may override.
            var ordinary = await apply.Handle(MachineId, [eventId], CancellationToken.None);
            Assert.Equal(NayaxStockEventApplyOutcome.DuplicateRequiresResolution, ordinary.Results[0].Outcome);

            var result = await resolve.Handle(
                MachineId, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Outcome);
            returnedAdjustmentId = result.StockAdjustmentId;
            Assert.NotNull(returnedAdjustmentId);
        }

        // A fresh context reads only what the transaction actually committed.
        await using var verify = TestAppDbContext.Unrestricted(options);

        var nayaxMovement = Assert.Single(
            await verify.StockAdjustments.Where(x => x.Source == StockAdjustmentSource.Nayax).ToListAsync());
        Assert.Equal(StockAdjustmentReason.MachineRefill, nayaxMovement.Reason);
        Assert.Equal(-4, nayaxMovement.QuantityChange);
        Assert.Equal(MachineId, nayaxMovement.MachineId);
        Assert.Equal(returnedAdjustmentId, nayaxMovement.Id);

        // Storage: 20 seeded, 4 taken by the manual refill, 4 by this Nayax event - deducted once.
        var product = await verify.Products.FindAsync(ProductId);
        Assert.Equal(12, product!.QuantityInStock);

        // MachineRefill is an internal transfer: no COGS, and costing quantity/value are untouched
        // by either refill, at the product and in the committed ledger row itself.
        Assert.Equal(20, product.CostingQuantity);
        Assert.Equal(20m, product.InventoryValue);
        Assert.Equal(1m, product.AverageUnitCost);
        Assert.Equal(20, nayaxMovement.CostingQuantityAfter);
        Assert.Equal(20m, nayaxMovement.InventoryValueAfter);
        Assert.Empty(await verify.NayaxSales.Where(s => s.CostOfGoodsSold != null).ToListAsync());

        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Equal(NayaxStockEventProcessingStatus.Applied, stored.ProcessingStatus);
        Assert.Equal(nayaxMovement.Id, stored.StockAdjustmentId);
        Assert.Equal(NayaxDuplicateResolution.AppliedAsSeparateRestock, stored.DuplicateResolution);
        Assert.NotNull(stored.DuplicateResolvedAt);
        Assert.Equal(manualAdjustmentId, stored.MatchedManualStockAdjustmentId);
    }

    [Fact]
    public async Task Repeating_apply_as_separate_restock_over_sqlite_never_deducts_or_records_twice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);

        int eventId;
        int? firstAdjustmentId;
        DateTime? firstResolvedAt;
        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            var (_, _, resolve, seededEventId, _) = await SeedDuplicateScenarioAsync(db);
            eventId = seededEventId;

            var first = await resolve.Handle(
                MachineId, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);
            Assert.Equal(NayaxStockEventApplyOutcome.Applied, first.Outcome);
            firstAdjustmentId = first.StockAdjustmentId;
        }

        await using (var read = TestAppDbContext.Unrestricted(options))
            firstResolvedAt = (await read.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId)).DuplicateResolvedAt;

        // A second request, with its own context and store, sees only the committed state - so this
        // is persisted idempotency rather than a change tracker still holding the first result.
        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            var second = await ResolveUseCase(db).Handle(
                MachineId, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, second.Outcome);
            Assert.Equal(firstAdjustmentId, second.StockAdjustmentId);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Single(await verify.StockAdjustments.Where(x => x.Source == StockAdjustmentSource.Nayax).ToListAsync());
        Assert.Equal(12, (await verify.Products.FindAsync(ProductId))!.QuantityInStock);

        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Equal(NayaxDuplicateResolution.AppliedAsSeparateRestock, stored.DuplicateResolution);
        Assert.Equal(firstResolvedAt, stored.DuplicateResolvedAt);
        Assert.Equal(firstAdjustmentId, stored.StockAdjustmentId);
    }

    [Fact]
    public async Task A_failure_while_applying_as_separate_restock_rolls_back_the_movement_and_the_resolution_together()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);

        var failingRebuild = new Mock<IInventoryCostRebuildService>();
        failingRebuild
            .Setup(x => x.RebuildAsync(ProductId, null, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated rebuild failure"));

        int eventId;
        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            var (_, _, resolve, seededEventId, _) = await SeedDuplicateScenarioAsync(db, failingRebuild.Object);
            eventId = seededEventId;

            var result = await resolve.Handle(
                MachineId, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Error, result.Outcome);
            Assert.Null(result.StockAdjustmentId);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);

        // Nothing partially committed: no Nayax movement, storage still only reduced by the manual
        // refill, and the event still unresolved so the operator can retry the same decision.
        Assert.Empty(await verify.StockAdjustments.Where(x => x.Source == StockAdjustmentSource.Nayax).ToListAsync());
        Assert.Equal(16, (await verify.Products.FindAsync(ProductId))!.QuantityInStock);

        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Equal(NayaxStockEventProcessingStatus.Unprocessed, stored.ProcessingStatus);
        Assert.Null(stored.StockAdjustmentId);
        Assert.Equal(NayaxDuplicateResolution.None, stored.DuplicateResolution);
        Assert.Null(stored.DuplicateResolvedAt);
        Assert.Null(stored.MatchedManualStockAdjustmentId);
    }

    #endregion

    #region Nayax upstream failure

    [Fact]
    public async Task Nayax_upstream_failure_propagates_uncaught()
    {
        using var db = CreateInMemoryDb(nameof(Nayax_upstream_failure_propagates_uncaught));
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineLastAlertsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Inventory.Infrastructure.Nayax.NayaxUpstreamException(
                "GetMachineLastAlertsAsync", HttpMethod.Get, "machines/900/lastAlerts", System.Net.HttpStatusCode.BadGateway));
        var (sync, _) = UseCases(db, nayax.Object);

        await Assert.ThrowsAsync<Inventory.Infrastructure.Nayax.NayaxUpstreamException>(
            () => sync.Handle(MachineId, CancellationToken.None));
    }

    #endregion
}
