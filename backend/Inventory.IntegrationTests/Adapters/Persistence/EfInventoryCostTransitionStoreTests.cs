using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Domain.Exceptions;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using DomainBaselineSource = Inventory.Domain.Costing.InventoryCostBaselineSource;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The <see cref="EfInventoryCostTransitionStore"/> adapter and the transition use cases
/// (issue #298) over relational SQLite. The central business query filter and the ownership stamp
/// on save (issue #64) are what keep a transition from reading another business's products,
/// movements, baselines or previews, or writing a baseline for them; the adapter adds no business
/// filter of its own. The apply transaction is relational, so its rollback is proven here too.
/// </summary>
public class EfInventoryCostTransitionStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long ProductA = 100;
    private const long ProductB = 200;
    private static readonly DateTime Now = new(2026, 9, 1, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Preview_and_apply_all_read_and_write_only_the_callers_business()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);
        var nayax = Nayax();

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var preview = await TestCostingUseCases.PreviewAllTransitions(db, nayax, new FakeClock(Now))
                .Handle(new(DomainBaselineSource.ManualAuthoritative));

            var product = Assert.Single(preview.Products);
            Assert.Equal(ProductA, product.ProductId);
            Assert.Equal(10, product.HomeStockQuantity);
            Assert.Equal(3, product.MachineStockQuantity);
            Assert.Equal(10, product.LegacyReplayedPhysicalQuantity);

            await TestCostingUseCases.ApplyAllTransitions(db, nayax, clock: new FakeClock(Now))
                .Handle(new(preview.PreviewId, Confirmed: true));
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var baseline = await verify.InventoryCostTransitionBaselines.Include(x => x.MachineStocks).SingleAsync();
        Assert.Equal(ProductA, baseline.ProductId);
        Assert.Equal(BusinessA, baseline.BusinessId);
        Assert.All(baseline.MachineStocks, x => Assert.Equal(BusinessA, x.BusinessId));
        Assert.Equal(BusinessA, (await verify.InventoryCostTransitionPreviewDrafts.SingleAsync()).BusinessId);
        var productA = await verify.Products.SingleAsync(x => x.Id == ProductA);
        Assert.Equal(13, productA.CostingQuantity);
        Assert.Equal(26m, productA.InventoryValue);
        var productB = await verify.Products.SingleAsync(x => x.Id == ProductB);
        Assert.Null(productB.CostingQuantity);
        Assert.Null(productB.InventoryValue);
    }

    [Fact]
    public async Task A_business_cannot_preview_another_businesss_product_or_apply_its_preview()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);
        var nayax = Nayax();
        InventoryCostTransitionPreview previewA;
        InventoryCostTransitionBatchPreview batchA;
        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            previewA = await TestCostingUseCases.PreviewTransition(a, nayax, new FakeClock(Now))
                .Handle(new(ProductA, 2m, DomainBaselineSource.ManualAuthoritative));
            batchA = await TestCostingUseCases.PreviewAllTransitions(a, nayax, new FakeClock(Now))
                .Handle(new(DomainBaselineSource.ManualAuthoritative));
        }

        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            var foreignProduct = await Assert.ThrowsAsync<DomainValidationException>(() =>
                TestCostingUseCases.PreviewTransition(b, nayax, new FakeClock(Now))
                    .Handle(new(ProductA, 2m, DomainBaselineSource.ManualAuthoritative)));
            var foreignPreview = await Assert.ThrowsAsync<DomainValidationException>(() =>
                TestCostingUseCases.ApplyTransition(b, nayax, clock: new FakeClock(Now))
                    .Handle(new(previewA.PreviewId, Confirmed: true)));
            var foreignBatch = await Assert.ThrowsAsync<DomainValidationException>(() =>
                TestCostingUseCases.ApplyAllTransitions(b, nayax, clock: new FakeClock(Now))
                    .Handle(new(batchA.PreviewId, Confirmed: true)));

            Assert.Equal($"Product {ProductA} does not exist.", foreignProduct.Message);
            Assert.Equal("The transition preview does not exist. Run the preview again.", foreignPreview.Message);
            Assert.Equal("The all-products transition preview does not exist. Run the preview again.", foreignBatch.Message);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(verify.InventoryCostTransitionBaselines);
        Assert.All(await verify.InventoryCostTransitionPreviewDrafts.ToListAsync(), x => Assert.Null(x.AppliedAt));
    }

    [Fact]
    public async Task Another_businesss_baseline_neither_blocks_nor_counts_toward_the_callers_transition()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);
        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            b.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline { ProductId = ProductB, CutoffAt = Now });
            await b.SaveChangesAsync();
        }

        await using var a = TestAppDbContext.For(options, BusinessA);
        var store = new EfInventoryCostTransitionStore(a);

        Assert.False(await store.AnyBaselineAsync([ProductA, ProductB], CancellationToken.None));
        Assert.Equal([ProductA], (await store.ListProductsWithoutBaselineAsync(CancellationToken.None)).Select(x => x.Id));
        Assert.Equal([ProductA], (await store.ListProductsAsync([ProductA, ProductB], CancellationToken.None)).Select(x => x.Id));
        Assert.Null(await store.GetProductAsync(ProductB, CancellationToken.None));
        Assert.Equal(
            new Dictionary<long, int> { [ProductA] = 10 },
            await store.SumPhysicalMovementsAsync([ProductA, ProductB], Now, CancellationToken.None));
    }

    [Fact]
    public async Task A_caller_without_a_business_reads_nothing_and_cannot_store_a_preview()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var denied = TestAppDbContext.Denied(options))
        {
            var store = new EfInventoryCostTransitionStore(denied);
            Assert.Empty(await store.ListProductsWithoutBaselineAsync(CancellationToken.None));
            Assert.Null(await store.GetProductAsync(ProductA, CancellationToken.None));
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                TestCostingUseCases.PreviewAllTransitions(denied, Nayax(), new FakeClock(Now))
                    .Handle(new(DomainBaselineSource.ManualAuthoritative)));
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(verify.InventoryCostTransitionPreviewDrafts);
    }

    [Fact]
    public async Task A_failed_post_transition_rebuild_rolls_back_the_baselines_and_the_applied_mark()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);
        var nayax = Nayax();
        var rebuild = new Mock<IRebuildProductCost>();
        rebuild.Setup(x => x.RebuildAsync(It.IsAny<long>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InventoryCostDataQualityException("Rebuild failed."));

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var single = await TestCostingUseCases.PreviewTransition(db, nayax, new FakeClock(Now))
                .Handle(new(ProductA, 2m, DomainBaselineSource.ManualAuthoritative));
            await Assert.ThrowsAsync<InventoryCostDataQualityException>(() =>
                TestCostingUseCases.ApplyTransition(db, nayax, rebuild.Object, new FakeClock(Now))
                    .Handle(new(single.PreviewId, Confirmed: true)));
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var batch = await TestCostingUseCases.PreviewAllTransitions(db, nayax, new FakeClock(Now))
                .Handle(new(DomainBaselineSource.ManualAuthoritative));
            await Assert.ThrowsAsync<InventoryCostDataQualityException>(() =>
                TestCostingUseCases.ApplyAllTransitions(db, nayax, rebuild.Object, new FakeClock(Now))
                    .Handle(new(batch.PreviewId, Confirmed: true)));
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(verify.InventoryCostTransitionBaselines);
        Assert.Empty(verify.InventoryCostTransitionMachineStocks);
        var drafts = await verify.InventoryCostTransitionPreviewDrafts.ToListAsync();
        Assert.Equal(2, drafts.Count);
        Assert.All(drafts, x => Assert.Null(x.AppliedAt));
    }

    private static INayaxLynxClient Nayax()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = ProductA, PAR = 5, MissingStockByMDB = 2 },
                new() { NayaxProductID = ProductB, PAR = 9, MissingStockByMDB = 0 }
            });
        return nayax.Object;
    }

    private static async Task<DbContextOptions<AppDbContext>> SeedTwoBusinessesAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = TestAppDbContext.Unrestricted(options))
            await setup.Database.EnsureCreatedAsync();

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            a.Products.Add(new Product { Id = ProductA, Name = "Coke Zero", QuantityInStock = 10, AverageUnitCost = 2m });
            a.StockAdjustments.Add(Restock(ProductA, 10));
            await a.SaveChangesAsync();
        }

        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            b.Products.Add(new Product { Id = ProductB, Name = "Business B Product", QuantityInStock = 5, AverageUnitCost = 9m });
            b.StockAdjustments.Add(Restock(ProductB, 5));
            await b.SaveChangesAsync();
        }

        return options;
    }

    private static StockAdjustment Restock(long productId, int quantity) => new()
    {
        ProductId = productId,
        QuantityChange = quantity,
        QuantityAfter = quantity,
        Reason = StockAdjustmentReason.Restock,
        EffectiveAt = Now.AddDays(-30),
    };
}
