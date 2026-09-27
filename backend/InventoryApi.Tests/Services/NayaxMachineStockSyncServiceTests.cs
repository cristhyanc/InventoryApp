using Inventory.Application.Nayax;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

/// <summary>
/// The Sync Restock reconciliation workflow (issue #183): import Nayax Event 501 stock-adjustment
/// alerts, resolve them to a local product via Machine + MDB, preview the storage impact, and apply
/// only explicitly accepted events through the existing inventory/costing movement logic.
/// </summary>
public class NayaxMachineStockSyncServiceTests
{
    private const long MachineId = 900;
    private const long ProductId = 200;
    private static readonly DateTime EventTime = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateInMemoryDb(string dbName) =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options);

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

    private static NayaxMachineAlert StockAlert(long eventId, string eventData, DateTime? timestamp = null) => new()
    {
        EventID = eventId,
        MachineID = MachineId,
        EventCode = Inventory.Domain.Nayax.NayaxMachineAlertEventCodes.StockAdjustForMachine,
        EventName = "Stock Adjust for Machine",
        EventData = eventData,
        EventTimestamp = timestamp ?? EventTime
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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);

        var evt = Assert.Single(preview.Events);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, evt.MatchStatus);
        Assert.Contains("mismatch", evt.NeedsReviewReason, StringComparison.OrdinalIgnoreCase);

        var applyResult = await svc.ApplyAsync(MachineId, [evt.Id]);
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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var first = await svc.SyncAsync(MachineId);
        Assert.Equal(1, first.NewEventCount);
        var eventId = first.Events[0].Id;

        var applied = await svc.ApplyAsync(MachineId, [eventId]);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, applied.Results[0].Outcome);

        // Re-fetching the same alert must not create a second event or a second deduction.
        var second = await svc.SyncAsync(MachineId);
        Assert.Equal(0, second.NewEventCount);
        Assert.Empty(second.Events);
        Assert.Equal("No new Nayax stock-adjustment alerts to review.", second.Message);

        Assert.Equal(18, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
        Assert.Single(await db.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
        Assert.Single(await db.NayaxMachineStockEvents.ToListAsync());
    }

    [Fact]
    public async Task No_new_alerts_produces_a_clear_empty_state_message()
    {
        using var db = CreateInMemoryDb(nameof(No_new_alerts_produces_a_clear_empty_state_message));
        var nayax = NayaxClientReturning([]);
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);
        var result = await svc.ApplyAsync(MachineId, [preview.Events[0].Id]);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);
        var previewEvent = preview.Events[0];
        Assert.True(previewEvent.IsInsufficientStock);
        Assert.Equal(4, previewEvent.AvailableStorageQuantity);
        Assert.Equal(1, previewEvent.UnaccountedDifference);

        var result = await svc.ApplyAsync(MachineId, [previewEvent.Id]);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);
        var previewEvent = preview.Events[0];
        Assert.True(previewEvent.IsDiscrepancy);
        Assert.Equal(-3, previewEvent.ParsedQuantity);

        var result = await svc.ApplyAsync(MachineId, [previewEvent.Id]);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);
        Assert.Equal(2, preview.Events.Count);
        var ids = preview.Events.Select(e => e.Id).ToArray();

        var result = await svc.ApplyAsync(MachineId, ids);

        var validResult = result.Results.Single(r => r.EventId == preview.Events.Single(e => e.MatchStatus == NayaxStockEventMatchStatus.Matched).Id);
        var invalidResult = result.Results.Single(r => r.EventId == preview.Events.Single(e => e.MatchStatus == NayaxStockEventMatchStatus.NeedsReview).Id);

        Assert.Equal(NayaxStockEventApplyOutcome.Applied, validResult.Outcome);
        Assert.Equal(NayaxStockEventApplyOutcome.NotMatched, invalidResult.Outcome);
        Assert.Equal(8, (await db.Products.FindAsync(ProductId))!.QuantityInStock);
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

        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(
            db, nayax.Object, new InventoryCostService(db), failingRebuild.Object);

        var preview = await svc.SyncAsync(MachineId);
        var result = await svc.ApplyAsync(MachineId, [preview.Events[0].Id]);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);
        var preview = await svc.SyncAsync(MachineId);
        await svc.ApplyAsync(MachineId, [preview.Events[0].Id]);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);

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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        var preview = await svc.SyncAsync(MachineId);

        var evt = Assert.Single(preview.Events);
        Assert.False(evt.IsPossibleDuplicate);
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
        INayaxMachineStockSyncService svc = new NayaxMachineStockSyncService(db, nayax.Object);

        await Assert.ThrowsAsync<Inventory.Infrastructure.Nayax.NayaxUpstreamException>(() => svc.SyncAsync(MachineId));
    }

    #endregion
}
