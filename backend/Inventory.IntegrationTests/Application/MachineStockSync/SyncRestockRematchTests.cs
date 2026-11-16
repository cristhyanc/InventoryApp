using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// Re-matching a Sync Restock event after its product mapping is fixed (issue #1248).
///
/// <para>A match used to be decided once, at import. An alert imported while its MDB position did
/// not exist in Nayax, or while no local product carried that position's Nayax product id, became
/// Needs Review and stayed there for ever - so the operator mapped the product, came back, and had
/// to enter the refill by hand anyway. The events still waiting for a match are now re-evaluated
/// whenever the list loads and again, under the write lock, when an event is applied.</para>
///
/// <para>These run against relational SQLite rather than the EF InMemory provider, because what
/// they prove includes what was actually committed to the event row and what the apply wrote inside
/// its own <c>BEGIN IMMEDIATE</c> transaction.</para>
/// </summary>
public class SyncRestockRematchTests : IAsyncLifetime
{
    private const int SellerBusinessId = 1;

    /// <summary>The machine's Nayax number: what the provider's alerts carry.</summary>
    private const long MachineId = 900;

    /// <summary>The same machine's local id (issue #741): what a movement records.</summary>
    private const long LocalMachineId = 31;

    private const int Mdb = 13;
    private const long NayaxProductId = 200;
    private const long LocalProductId = 4200;
    private const int OpeningStock = 20;
    private const int RefillQuantity = 2;
    private const string ProductName = "25g Nobby's Beef Jerky Hot";

    private static readonly MachineIdentity Machine = new(LocalMachineId, MachineId);
    private static readonly DateTime EventTime = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<AppDbContext> _options = null!;

    /// <summary>
    /// The machine's MDB positions as Nayax currently reports them. It is mutable because that is
    /// the fix under test: the operator maps the position and the next list load sees it.
    /// </summary>
    private readonly List<NayaxMachineProduct> _machineProducts = [];

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var setup = TestAppDbContext.Unrestricted(_options);
        await setup.Database.EnsureCreatedAsync();
        setup.Businesses.Add(new Business
        {
            Id = SellerBusinessId,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = EventTime,
        });
        setup.Machines.Add(new LocalMachine
        {
            BusinessId = SellerBusinessId,
            Id = LocalMachineId,
            Name = "Machine 900",
            ExternalMachineId = NayaxExternalId.From(MachineId),
        });
        await setup.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>
    /// The issue's own scenario: the position exists in Nayax, but no local product carries its
    /// Nayax id, so the import leaves the event Needs Review and the ordinary apply refuses it.
    /// Recording the id on the product is all the operator does; the next load of the list shows
    /// the event matched and it applies normally, taking its units out of storage exactly once.
    /// </summary>
    [Fact]
    public async Task An_event_needing_review_for_a_missing_product_link_is_rematched_and_applicable()
    {
        _machineProducts.Add(Position());
        await using var db = TestAppDbContext.For(_options, SellerBusinessId);
        SeedCostedProduct(db, LocalProductId, ProductName, nayaxProductId: null);
        await db.SaveChangesAsync();

        var (sync, apply) = UseCases(db);

        var imported = Assert.Single((await sync.Handle(Machine, CancellationToken.None)).Events);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, imported.MatchStatus);
        Assert.Null(imported.MatchedProductId);
        Assert.Contains("No local product is mapped to Nayax product", imported.NeedsReviewReason);

        var refused = await apply.Handle(Machine, [imported.Id], CancellationToken.None);
        Assert.Equal(NayaxStockEventApplyOutcome.NotMatched, refused.Results[0].Outcome);
        Assert.Equal(OpeningStock, (await db.Products.FindAsync(LocalProductId))!.QuantityInStock);

        // The operator records the Nayax id on the local product. Nothing else changes: the same
        // alert, already imported, is not re-fetched or re-created.
        (await db.Products.FindAsync(LocalProductId))!.ExternalProductId = NayaxExternalId.From(NayaxProductId);
        await db.SaveChangesAsync();

        var reloaded = Assert.Single((await sync.Handle(Machine, CancellationToken.None)).Events);
        Assert.Equal(imported.Id, reloaded.Id);
        Assert.Equal(NayaxStockEventMatchStatus.Matched, reloaded.MatchStatus);
        Assert.Equal(LocalProductId, reloaded.MatchedProductId);
        Assert.Equal(ProductName, reloaded.MatchedProductName);
        Assert.Null(reloaded.NeedsReviewReason);

        var applied = await apply.Handle(Machine, [imported.Id], CancellationToken.None);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, applied.Results[0].Outcome);
        Assert.NotNull(applied.Results[0].StockAdjustmentId);

        await using var verify = TestAppDbContext.For(_options, SellerBusinessId);
        Assert.Equal(
            OpeningStock - RefillQuantity,
            (await verify.Products.SingleAsync(p => p.Id == LocalProductId)).QuantityInStock);

        var movement = Assert.Single(await verify.StockAdjustments
            .Where(x => x.Reason == StockAdjustmentReason.MachineRefill && x.Source == StockAdjustmentSource.Nayax)
            .ToListAsync());
        Assert.Equal(-RefillQuantity, movement.QuantityChange);

        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == imported.Id);
        Assert.Equal(NayaxStockEventProcessingStatus.Applied, stored.ProcessingStatus);
        Assert.Equal(LocalProductId, stored.MatchedProductId);
        Assert.Equal(movement.Id, stored.StockAdjustmentId);
    }

    /// <summary>
    /// The other half of the same defect, fixed on the provider's side: the alert arrives for an
    /// MDB the machine does not report at all, and the operator then maps that position in Nayax.
    /// The reason stored while it was unresolvable is replaced rather than left behind.
    /// </summary>
    [Fact]
    public async Task An_event_needing_review_for_an_unknown_mdb_is_rematched_once_the_position_exists()
    {
        await using var db = TestAppDbContext.For(_options, SellerBusinessId);
        SeedCostedProduct(db, LocalProductId, ProductName, NayaxProductId);
        await db.SaveChangesAsync();

        var (sync, apply) = UseCases(db);

        var imported = Assert.Single((await sync.Handle(Machine, CancellationToken.None)).Events);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, imported.MatchStatus);
        Assert.Equal($"MDB {Mdb} is not a known position on machine {MachineId}.", imported.NeedsReviewReason);

        _machineProducts.Add(Position());

        var reloaded = Assert.Single((await sync.Handle(Machine, CancellationToken.None)).Events);
        Assert.Equal(NayaxStockEventMatchStatus.Matched, reloaded.MatchStatus);
        Assert.Equal(LocalProductId, reloaded.MatchedProductId);
        Assert.Null(reloaded.NeedsReviewReason);

        var applied = await apply.Handle(Machine, [imported.Id], CancellationToken.None);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, applied.Results[0].Outcome);
    }

    /// <summary>
    /// The apply's own re-match, inside its write lock: the mapping is fixed after the list was
    /// loaded, so the event the operator ticked is still stored as Needs Review when the apply
    /// starts. It is re-resolved and applied in the same transaction, and the refreshed match is
    /// committed with the movement it decided.
    /// </summary>
    [Fact]
    public async Task An_apply_rematches_the_event_it_is_about_to_write()
    {
        await using var db = TestAppDbContext.For(_options, SellerBusinessId);
        SeedCostedProduct(db, LocalProductId, ProductName, NayaxProductId);
        await db.SaveChangesAsync();

        var (sync, apply) = UseCases(db);

        var imported = Assert.Single((await sync.Handle(Machine, CancellationToken.None)).Events);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, imported.MatchStatus);

        // Mapped only now, with no second list load in between: the apply is the first thing to see
        // the position exist.
        _machineProducts.Add(Position());

        var applied = await apply.Handle(Machine, [imported.Id], CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.Applied, applied.Results[0].Outcome);

        await using var verify = TestAppDbContext.For(_options, SellerBusinessId);
        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == imported.Id);
        Assert.Equal(NayaxStockEventMatchStatus.Matched, stored.MatchStatus);
        Assert.Equal(LocalProductId, stored.MatchedProductId);
        Assert.Null(stored.NeedsReviewReason);
        Assert.Equal(NayaxStockEventProcessingStatus.Applied, stored.ProcessingStatus);
        Assert.Equal(
            OpeningStock - RefillQuantity,
            (await verify.Products.SingleAsync(p => p.Id == LocalProductId)).QuantityInStock);
    }

    /// <summary>
    /// An event the operator already settled is never re-matched, whichever way it was settled: an
    /// applied event's match is the record of which product's storage moved, and one dismissed as
    /// "already recorded manually" was dismissed deliberately. Both are left exactly as they are
    /// even though the mapping now resolves them to a different product.
    /// </summary>
    [Fact]
    public async Task Applied_and_reconciled_events_are_never_rematched()
    {
        await using var db = TestAppDbContext.For(_options, SellerBusinessId);
        SeedCostedProduct(db, LocalProductId, ProductName, NayaxProductId);
        var otherProduct = SeedCostedProduct(db, 4300, "Another product", nayaxProductId: null);

        // Applied against a product the mapping no longer names, with its movement recorded.
        var applied = SeedEvent(db, nayaxEventLogId: 10);
        applied.MatchStatus = NayaxStockEventMatchStatus.Matched;
        applied.MatchedProductId = otherProduct.Id;
        applied.ProcessingStatus = NayaxStockEventProcessingStatus.Applied;
        applied.ProcessedAt = EventTime;
        applied.StockAdjustmentId = null;

        // Dismissed as already recorded manually while it was still unmatched (issue #242 allows
        // exactly that), so re-matching it would present a settled event as applicable again.
        var reconciled = SeedEvent(db, nayaxEventLogId: 11);
        reconciled.MatchStatus = NayaxStockEventMatchStatus.NeedsReview;
        reconciled.NeedsReviewReason = "Unresolvable when it was dismissed.";
        reconciled.DuplicateResolution = NayaxDuplicateResolution.ReconciledManually;
        reconciled.DuplicateResolvedAt = EventTime;
        await db.SaveChangesAsync();

        _machineProducts.Add(Position());
        var (sync, _) = UseCases(db);

        await sync.Handle(Machine, CancellationToken.None, includeReconciled: true);

        await using var verify = TestAppDbContext.For(_options, SellerBusinessId);
        var storedApplied = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == applied.Id);
        Assert.Equal(NayaxStockEventMatchStatus.Matched, storedApplied.MatchStatus);
        Assert.Equal(otherProduct.Id, storedApplied.MatchedProductId);

        var storedReconciled = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == reconciled.Id);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, storedReconciled.MatchStatus);
        Assert.Null(storedReconciled.MatchedProductId);
        Assert.Equal("Unresolvable when it was dismissed.", storedReconciled.NeedsReviewReason);
    }

    private static NayaxMachineProduct Position() =>
        new() { NayaxProductID = NayaxProductId, MDBCode = Mdb, ProductName = ProductName };

    private (SyncMachineStockFromNayax Sync, ApplyMachineStockSync Apply) UseCases(AppDbContext db)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineLastAlertsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new NayaxMachineAlert
                {
                    EventLogId = 1,
                    MachineId = MachineId,
                    EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
                    EventDescription = "Stock Adjust for Machine",
                    EventData = $"Adjusted Stock, Product MDB: {Mdb} | {ProductName} | {RefillQuantity}",
                    EventDateTimeGmt = EventTime,
                    EventDateTimeVmc = DateTime.SpecifyKind(EventTime.AddHours(10), DateTimeKind.Unspecified),
                },
            ]);

        // Read through the list each time, so a position mapped between two calls is seen by the
        // second one - which is the whole point of reading the mapping fresh.
        nayax.Setup(x => x.GetMachineProductsFreshAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [.. _machineProducts]);

        var store = new EfMachineStockEventStore(
            db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db));
        var productLinks = new EfNayaxProductLinkStore(db);

        return (
            new SyncMachineStockFromNayax(
                nayax.Object,
                store,
                TestMachineStockSyncUseCases.Rematch(store, productLinks, nayax.Object),
                new FakeClock(EventTime),
                new FakeNayaxConnectionStore("op", "token", NayaxConnectionStatus.Ready),
                new FakeMachineRestockSyncCoverageStore(),
                new FakeBusinessCalendar(EventTime)),
            TestMachineStockSyncUseCases.Apply(
                store, productLinks, new RecordingMachineStockSyncLog(), nayax.Object));
    }

    private static Product SeedCostedProduct(
        AppDbContext db, long productId, string name, long? nayaxProductId)
    {
        var product = new Product
        {
            Id = productId,
            ExternalProductId = nayaxProductId is { } id ? NayaxExternalId.From(id) : null,
            Name = name,
            Unit = "each",
            QuantityInStock = OpeningStock,
            CostingQuantity = OpeningStock,
            InventoryValue = OpeningStock * 1m,
            AverageUnitCost = 1m,
        };
        db.Products.Add(product);
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = productId,
            QuantityChange = OpeningStock,
            QuantityAfter = OpeningStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 1m,
            EffectiveAt = EventTime.AddDays(-2),
        });
        return product;
    }

    private static NayaxMachineStockEvent SeedEvent(AppDbContext db, long nayaxEventLogId)
    {
        var stored = new NayaxMachineStockEvent
        {
            NayaxEventLogId = nayaxEventLogId,
            MachineId = MachineId,
            EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
            EventDateTimeGmt = EventTime,
            EventDateTimeVmc = EventTime.AddHours(10),
            RawEventData = $"Product MDB: {Mdb} | {ProductName} | {RefillQuantity}",
            ParsedMdb = Mdb,
            ParsedProductName = ProductName,
            ParsedQuantity = RefillQuantity,
        };
        db.NayaxMachineStockEvents.Add(stored);
        return stored;
    }
}
