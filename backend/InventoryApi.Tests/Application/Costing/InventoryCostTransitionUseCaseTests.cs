using Inventory.Application.Nayax;
using Inventory.Domain.Exceptions;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Time;
using Microsoft.EntityFrameworkCore;
using Moq;
using System.Net;
using Xunit;
using DomainBaselineSource = Inventory.Domain.Costing.InventoryCostBaselineSource;

namespace InventoryApi.Tests.Application.Costing;

/// <summary>
/// Orchestration of the inventory-cost transition use cases (issue #298, child 4 of #149): stored
/// previews, stale-preview rejection, repeat and expired applies, the preview scope, and a Nayax
/// upstream failure leaving nothing behind. Run over the temporary API-owned
/// <c>EfInventoryCostTransitionStore</c> on a fixed clock.
/// </summary>
public class InventoryCostTransitionUseCaseTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Preview_stores_a_single_product_draft_that_expires_after_thirty_minutes()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();

        var preview = await TestCostingUseCases.PreviewTransition(db, nayax.Client, new FakeClock(Now))
            .Handle(new(10, 1.25m, DomainBaselineSource.ManualEstimated));

        var draft = await db.InventoryCostTransitionPreviewDrafts.SingleAsync();
        Assert.Equal(preview.PreviewId, draft.Id);
        Assert.Equal(10, draft.ProductId);
        Assert.Equal(Now, draft.CreatedAt);
        Assert.Equal(Now.AddMinutes(30), draft.ExpiresAt);
        Assert.Null(draft.AppliedAt);
        Assert.Equal(Now, preview.CutoffAt);
        Assert.Equal(DomainBaselineSource.ManualEstimated, preview.CostSource);
        Assert.Equal(["Machine A", "Machine B"], preview.MachineStocks.Select(x => x.MachineName));
        Assert.All(preview.MachineStocks, x => Assert.Equal("Nayax PAR - MissingStockByMDB", x.Source));
        Assert.Equal(4, preview.LegacyReplayedPhysicalQuantity);
        Assert.Empty(db.InventoryCostTransitionBaselines);
    }

    [Fact]
    public async Task Preview_rejects_an_invalid_source_a_missing_product_and_a_product_with_a_baseline()
    {
        await using var db = await SeedAsync();
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline { ProductId = 20, CutoffAt = Now });
        await db.SaveChangesAsync();
        var preview = TestCostingUseCases.PreviewTransition(db, new NayaxStock().Client, new FakeClock(Now));

        var source = await Assert.ThrowsAsync<DomainValidationException>(() =>
            preview.Handle(new(10, 1m, (DomainBaselineSource)99)));
        var missing = await Assert.ThrowsAsync<DomainValidationException>(() =>
            preview.Handle(new(999, 1m, DomainBaselineSource.ManualAuthoritative)));
        var baseline = await Assert.ThrowsAsync<DomainValidationException>(() =>
            preview.Handle(new(20, 1m, DomainBaselineSource.ManualAuthoritative)));

        Assert.Equal("Select whether the opening cost is authoritative or estimated.", source.Message);
        Assert.Equal("Product 999 does not exist.", missing.Message);
        Assert.Equal("This product already has an inventory-cost transition baseline.", baseline.Message);
        Assert.Empty(db.InventoryCostTransitionPreviewDrafts);
    }

    [Fact]
    public async Task Apply_saves_the_baseline_as_previewed_marks_the_draft_applied_and_rebuilds_from_the_cutoff()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var clock = new FakeClock(Now);
        var preview = await TestCostingUseCases.PreviewTransition(db, nayax.Client, clock)
            .Handle(new(10, 1.25m, DomainBaselineSource.ManualAuthoritative));
        clock.UtcNow = Now.AddMinutes(10);

        var applied = await TestCostingUseCases.ApplyTransition(db, nayax.Client, clock: clock)
            .Handle(new(preview.PreviewId, Confirmed: true));

        Assert.Equivalent(preview, applied, strict: true);
        var baseline = await db.InventoryCostTransitionBaselines.Include(x => x.MachineStocks).SingleAsync();
        Assert.Equal(10, baseline.ProductId);
        Assert.Equal(Now, baseline.CutoffAt);
        Assert.Equal(preview.OpeningCostingQuantity, baseline.OpeningCostingQuantity);
        Assert.Equal(preview.InventoryValue, baseline.InventoryValue);
        Assert.Equal(InventoryCostBaselineSource.ManualAuthoritative, baseline.CostSource);
        Assert.Equal(preview.DataQualityNote, baseline.DataQualityNote);
        Assert.Equal(
            preview.MachineStocks.Select(x => (x.MachineId, x.MachineName, x.StockQuantity, x.Source)),
            baseline.MachineStocks.OrderBy(x => x.MachineId).Select(x => (x.MachineId, x.MachineName ?? "", x.StockQuantity, x.Source)));
        Assert.Equal(Now.AddMinutes(10), (await db.InventoryCostTransitionPreviewDrafts.SingleAsync()).AppliedAt);
        var product = await db.Products.SingleAsync(x => x.Id == 10);
        Assert.Equal(preview.OpeningCostingQuantity, product.CostingQuantity);
        Assert.Equal(preview.InventoryValue, product.InventoryValue);
    }

    [Fact]
    public async Task Repeat_apply_of_the_same_preview_is_rejected_and_saves_nothing_more()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var preview = await TestCostingUseCases.PreviewTransition(db, nayax.Client, new FakeClock(Now))
            .Handle(new(10, 1.25m, DomainBaselineSource.ManualAuthoritative));
        var apply = TestCostingUseCases.ApplyTransition(db, nayax.Client, clock: new FakeClock(Now));
        await apply.Handle(new(preview.PreviewId, Confirmed: true));

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            apply.Handle(new(preview.PreviewId, Confirmed: true)));

        Assert.Equal("This transition preview has already been applied.", exception.Message);
        Assert.Single(db.InventoryCostTransitionBaselines);
    }

    [Fact]
    public async Task An_expired_preview_is_rejected()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var preview = await TestCostingUseCases.PreviewTransition(db, nayax.Client, new FakeClock(Now))
            .Handle(new(10, 1.25m, DomainBaselineSource.ManualAuthoritative));

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.ApplyTransition(db, nayax.Client, clock: new FakeClock(Now.AddMinutes(31)))
                .Handle(new(preview.PreviewId, Confirmed: true)));

        Assert.Equal("The transition preview expired. Run the preview again.", exception.Message);
        Assert.Empty(db.InventoryCostTransitionBaselines);
    }

    [Fact]
    public async Task A_preview_is_only_applied_through_its_own_scope()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var single = await TestCostingUseCases.PreviewTransition(db, nayax.Client, new FakeClock(Now))
            .Handle(new(10, 1.25m, DomainBaselineSource.ManualAuthoritative));
        var batch = await TestCostingUseCases.PreviewAllTransitions(db, nayax.Client, new FakeClock(Now))
            .Handle(new(DomainBaselineSource.ManualAuthoritative));

        var singleAsBatch = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.ApplyAllTransitions(db, nayax.Client, clock: new FakeClock(Now))
                .Handle(new(single.PreviewId, Confirmed: true)));
        var batchAsSingle = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.ApplyTransition(db, nayax.Client, clock: new FakeClock(Now))
                .Handle(new(batch.PreviewId, Confirmed: true)));

        Assert.Equal("The all-products transition preview does not exist. Run the preview again.", singleAsBatch.Message);
        Assert.Equal("The transition preview does not exist. Run the preview again.", batchAsSingle.Message);
        Assert.Empty(db.InventoryCostTransitionBaselines);
    }

    [Fact]
    public async Task Apply_rejects_a_stale_preview_when_home_stock_changed_after_it()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var preview = await TestCostingUseCases.PreviewTransition(db, nayax.Client, new FakeClock(Now))
            .Handle(new(10, 1.25m, DomainBaselineSource.ManualAuthoritative));
        (await db.Products.SingleAsync(x => x.Id == 10)).QuantityInStock = 18;
        await db.SaveChangesAsync();

        await AssertStaleAsync(() => TestCostingUseCases.ApplyTransition(db, nayax.Client, clock: new FakeClock(Now))
            .Handle(new(preview.PreviewId, Confirmed: true)));
        Assert.Empty(db.InventoryCostTransitionBaselines);
        Assert.Null((await db.InventoryCostTransitionPreviewDrafts.SingleAsync()).AppliedAt);
    }

    [Fact]
    public async Task Apply_rejects_a_stale_preview_when_a_legacy_movement_was_recorded_after_it()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var preview = await TestCostingUseCases.PreviewTransition(db, nayax.Client, new FakeClock(Now))
            .Handle(new(10, 1.25m, DomainBaselineSource.ManualAuthoritative));
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = 1,
            Reason = StockAdjustmentReason.Correction,
            EffectiveAt = Now.AddDays(-1)
        });
        await db.SaveChangesAsync();

        await AssertStaleAsync(() => TestCostingUseCases.ApplyTransition(db, nayax.Client, clock: new FakeClock(Now))
            .Handle(new(preview.PreviewId, Confirmed: true)));
        Assert.Empty(db.InventoryCostTransitionBaselines);
    }

    [Fact]
    public async Task Apply_rejects_a_stale_preview_when_nayax_machine_stock_changed_after_it()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var preview = await TestCostingUseCases.PreviewTransition(db, nayax.Client, new FakeClock(Now))
            .Handle(new(10, 1.25m, DomainBaselineSource.ManualAuthoritative));
        nayax.MissingInMachineA = 5;

        await AssertStaleAsync(() => TestCostingUseCases.ApplyTransition(db, nayax.Client, clock: new FakeClock(Now))
            .Handle(new(preview.PreviewId, Confirmed: true)));
        Assert.Empty(db.InventoryCostTransitionBaselines);
    }

    [Fact]
    public async Task Preview_all_rejects_an_invalid_source_no_eligible_product_and_a_negative_average_cost()
    {
        await using var db = await SeedAsync();
        var previewAll = TestCostingUseCases.PreviewAllTransitions(db, new NayaxStock().Client, new FakeClock(Now));

        var source = await Assert.ThrowsAsync<DomainValidationException>(() => previewAll.Handle(new((DomainBaselineSource)0)));
        (await db.Products.SingleAsync(x => x.Id == 20)).AverageUnitCost = -1m;
        await db.SaveChangesAsync();
        var negative = await Assert.ThrowsAsync<DomainValidationException>(() =>
            previewAll.Handle(new(DomainBaselineSource.ManualAuthoritative)));
        db.InventoryCostTransitionBaselines.AddRange(
            new InventoryCostTransitionBaseline { ProductId = 10, CutoffAt = Now },
            new InventoryCostTransitionBaseline { ProductId = 20, CutoffAt = Now });
        await db.SaveChangesAsync();
        var none = await Assert.ThrowsAsync<DomainValidationException>(() =>
            previewAll.Handle(new(DomainBaselineSource.ManualAuthoritative)));

        Assert.Equal("Select whether the opening costs are authoritative or estimated.", source.Message);
        Assert.Equal("One or more products have a negative current average unit cost.", negative.Message);
        Assert.Equal("All products already have an inventory-cost transition baseline.", none.Message);
        Assert.Empty(db.InventoryCostTransitionPreviewDrafts);
    }

    [Fact]
    public async Task Preview_all_stores_one_all_products_draft_ordered_by_name_at_current_average_costs()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();

        var preview = await TestCostingUseCases.PreviewAllTransitions(db, nayax.Client, new FakeClock(Now))
            .Handle(new(DomainBaselineSource.ManualEstimated));

        Assert.Equal(["Drink", "Snack"], preview.Products.Select(x => x.ProductName));
        Assert.All(preview.Products, x => Assert.Equal(preview.PreviewId, x.PreviewId));
        Assert.All(preview.Products, x => Assert.Equal(Now, x.CutoffAt));
        Assert.Equal([2m, 1.25m], preview.Products.Select(x => x.AverageUnitCost));
        Assert.Equal(preview.Products.Sum(x => x.InventoryValue), preview.InventoryValue);
        var draft = await db.InventoryCostTransitionPreviewDrafts.SingleAsync();
        Assert.Equal(0, draft.ProductId);
        Assert.Equal(Now.AddMinutes(30), draft.ExpiresAt);
    }

    [Fact]
    public async Task Apply_all_saves_every_baseline_and_a_repeat_apply_is_rejected()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var preview = await TestCostingUseCases.PreviewAllTransitions(db, nayax.Client, new FakeClock(Now))
            .Handle(new(DomainBaselineSource.ManualAuthoritative));
        var applyAll = TestCostingUseCases.ApplyAllTransitions(db, nayax.Client, clock: new FakeClock(Now));

        var applied = await applyAll.Handle(new(preview.PreviewId, Confirmed: true));
        var repeat = await Assert.ThrowsAsync<DomainValidationException>(() =>
            applyAll.Handle(new(preview.PreviewId, Confirmed: true)));

        Assert.Equal(preview.PreviewId, applied.PreviewId);
        Assert.Equal([10L, 20L], await db.InventoryCostTransitionBaselines.Select(x => x.ProductId).OrderBy(x => x).ToListAsync());
        Assert.Equal("This transition preview has already been applied.", repeat.Message);
        Assert.Equal(2, await db.InventoryCostTransitionBaselines.CountAsync());
    }

    [Fact]
    public async Task Apply_all_without_confirmation_is_rejected()
    {
        await using var db = await SeedAsync();

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.ApplyAllTransitions(db, new NayaxStock().Client, clock: new FakeClock(Now))
                .Handle(new(Guid.NewGuid(), Confirmed: false)));

        Assert.Equal("Explicit confirmation is required to save all transition baselines.", exception.Message);
    }

    [Fact]
    public async Task Apply_all_rejects_a_stale_preview_when_an_average_cost_changed_after_it()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var preview = await TestCostingUseCases.PreviewAllTransitions(db, nayax.Client, new FakeClock(Now))
            .Handle(new(DomainBaselineSource.ManualAuthoritative));
        (await db.Products.SingleAsync(x => x.Id == 10)).AverageUnitCost = 1.30m;
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.ApplyAllTransitions(db, nayax.Client, clock: new FakeClock(Now))
                .Handle(new(preview.PreviewId, Confirmed: true)));

        Assert.Equal("The average unit cost for Snack changed after the preview. Run it again.", exception.Message);
        Assert.Empty(db.InventoryCostTransitionBaselines);
    }

    [Fact]
    public async Task Apply_all_rejects_a_stale_preview_when_machine_stock_changed_after_it()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var preview = await TestCostingUseCases.PreviewAllTransitions(db, nayax.Client, new FakeClock(Now))
            .Handle(new(DomainBaselineSource.ManualAuthoritative));
        nayax.MissingInMachineA = 0;

        await AssertStaleAsync(() => TestCostingUseCases.ApplyAllTransitions(db, nayax.Client, clock: new FakeClock(Now))
            .Handle(new(preview.PreviewId, Confirmed: true)));
        Assert.Empty(db.InventoryCostTransitionBaselines);
        Assert.Null((await db.InventoryCostTransitionPreviewDrafts.SingleAsync()).AppliedAt);
    }

    [Fact]
    public async Task Apply_all_rejects_a_preview_when_a_product_received_a_baseline_or_was_removed_after_it()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var previewAll = TestCostingUseCases.PreviewAllTransitions(db, nayax.Client, new FakeClock(Now));
        var applyAll = TestCostingUseCases.ApplyAllTransitions(db, nayax.Client, clock: new FakeClock(Now));
        var first = await previewAll.Handle(new(DomainBaselineSource.ManualAuthoritative));
        var second = await previewAll.Handle(new(DomainBaselineSource.ManualAuthoritative));
        db.Products.Remove(await db.Products.SingleAsync(x => x.Id == 20));
        await db.SaveChangesAsync();

        var removed = await Assert.ThrowsAsync<DomainValidationException>(() =>
            applyAll.Handle(new(first.PreviewId, Confirmed: true)));
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline { ProductId = 10, CutoffAt = Now });
        await db.SaveChangesAsync();
        var baselined = await Assert.ThrowsAsync<DomainValidationException>(() =>
            applyAll.Handle(new(second.PreviewId, Confirmed: true)));

        Assert.Equal("One or more previewed products no longer exist.", removed.Message);
        Assert.Equal("One or more products received a baseline after this preview. Run the preview again.", baselined.Message);
        Assert.Single(db.InventoryCostTransitionBaselines);
    }

    [Fact]
    public async Task A_nayax_upstream_failure_during_preview_propagates_and_stores_no_draft()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock { Failing = true };

        await Assert.ThrowsAsync<NayaxUpstreamException>(() =>
            TestCostingUseCases.PreviewTransition(db, nayax.Client, new FakeClock(Now))
                .Handle(new(10, 1.25m, DomainBaselineSource.ManualAuthoritative)));
        await Assert.ThrowsAsync<NayaxUpstreamException>(() =>
            TestCostingUseCases.PreviewAllTransitions(db, nayax.Client, new FakeClock(Now))
                .Handle(new(DomainBaselineSource.ManualAuthoritative)));

        Assert.Empty(db.InventoryCostTransitionPreviewDrafts);
    }

    [Fact]
    public async Task A_nayax_upstream_failure_during_apply_propagates_and_saves_no_baseline()
    {
        await using var db = await SeedAsync();
        var nayax = new NayaxStock();
        var single = await TestCostingUseCases.PreviewTransition(db, nayax.Client, new FakeClock(Now))
            .Handle(new(10, 1.25m, DomainBaselineSource.ManualAuthoritative));
        var batch = await TestCostingUseCases.PreviewAllTransitions(db, nayax.Client, new FakeClock(Now))
            .Handle(new(DomainBaselineSource.ManualAuthoritative));
        nayax.Failing = true;

        await Assert.ThrowsAsync<NayaxUpstreamException>(() =>
            TestCostingUseCases.ApplyTransition(db, nayax.Client, clock: new FakeClock(Now))
                .Handle(new(single.PreviewId, Confirmed: true)));
        await Assert.ThrowsAsync<NayaxUpstreamException>(() =>
            TestCostingUseCases.ApplyAllTransitions(db, nayax.Client, clock: new FakeClock(Now))
                .Handle(new(batch.PreviewId, Confirmed: true)));

        Assert.Empty(db.InventoryCostTransitionBaselines);
        Assert.All(await db.InventoryCostTransitionPreviewDrafts.ToListAsync(), x => Assert.Null(x.AppliedAt));
    }

    private static async Task AssertStaleAsync(Func<Task> apply)
    {
        var exception = await Assert.ThrowsAsync<DomainValidationException>(apply);
        Assert.Equal("Inventory data changed after the preview. Run the preview again before confirming.", exception.Message);
    }

    private static async Task<AppDbContext> SeedAsync()
    {
        var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Products.AddRange(
            new Product { Id = 10, Name = "Snack", QuantityInStock = 4, AverageUnitCost = 1.25m },
            new Product { Id = 20, Name = "Drink", QuantityInStock = 6, AverageUnitCost = 2m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = 4,
            Reason = StockAdjustmentReason.Restock,
            EffectiveAt = Now.AddDays(-10)
        });
        await db.SaveChangesAsync();
        return db;
    }

    /// <summary>Two Nayax machines stocking both products, with a switchable upstream failure.</summary>
    private sealed class NayaxStock
    {
        private readonly Mock<INayaxLynxClient> _mock = new();

        public NayaxStock()
        {
            _mock.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Failing
                    ? throw new NayaxUpstreamException("GetMachinesAsync", HttpMethod.Get, "machines", HttpStatusCode.ServiceUnavailable)
                    : new List<NayaxMachine>
                    {
                        new() { MachineID = 2, MachineName = "Machine B" },
                        new() { MachineID = 1, MachineName = "Machine A" }
                    });
            _mock.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new List<NayaxMachineProduct>
                {
                    new() { NayaxProductID = 10, PAR = 10, MissingStockByMDB = MissingInMachineA },
                    new() { NayaxProductID = 20, PAR = 6, MissingStockByMDB = 1 }
                });
            _mock.Setup(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new List<NayaxMachineProduct> { new() { NayaxProductID = 10, PAR = 8, MissingStockByMDB = 3 } });
        }

        public bool Failing { get; set; }

        public int MissingInMachineA { get; set; } = 4;

        public INayaxLynxClient Client => _mock.Object;
    }
}
