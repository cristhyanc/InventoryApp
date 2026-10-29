using Inventory.Application.Costing;
using Inventory.Application.Purchases;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Issue #831: a purchase write naming a product that has never been costed - no transition
/// baseline, no costing quantity, no inventory value and no movements, which is what product
/// creation with no opening stock and the catalogue review leave behind. Its rebuild compares the
/// stored physical quantity with the replayed movement history, so the purchase write has to move
/// the stored quantity with the movement it writes, in the same transaction, the way every other
/// movement writer already does. Relational (SQLite) because the refusal must roll back a real
/// transaction.
/// </summary>
public sealed class EfPurchaseStoreNeverCostedProductTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long CostedProduct = 1;
    private const long FreshProduct = 2;
    private const long OtherBusinessProduct = 3;
    private static readonly DateTime PurchaseDate = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfPurchaseStoreNeverCostedProductTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UpdateAsync_adding_a_never_costed_product_records_one_movement_and_its_stock_and_cost()
    {
        var (purchaseId, existingLineId) = await SeedPurchaseOfCostedProductAsync(freshProductStock: 0);
        var before = await SnapshotCostedLineAsync();

        await UpdateAsync(purchaseId,
            new PurchaseItemInput(CostedProduct, 4m, 1.50m, null, existingLineId),
            new PurchaseItemInput(FreshProduct, 10m, 2.50m));

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var freshLine = await verify.ReceiptItems.SingleAsync(item => item.ProductId == FreshProduct);
        Assert.Equal(purchaseId, freshLine.ReceiptId);
        var movement = await verify.StockAdjustments.SingleAsync(x => x.ProductId == FreshProduct);
        Assert.Equal(freshLine.Id, movement.ReceiptItemId);
        Assert.Equal(StockAdjustmentReason.Restock, movement.Reason);
        Assert.Equal(10, movement.QuantityChange);
        Assert.Equal(2.50m, movement.UnitCost);
        Assert.Equal(25m, movement.TotalCost);
        Assert.Equal(PurchaseDate, movement.EffectiveAt);
        Assert.Equal(10, movement.QuantityAfter);
        Assert.Equal(10, movement.CostingQuantityAfter);
        Assert.Equal(25m, movement.InventoryValueAfter);

        var product = await verify.Products.SingleAsync(p => p.Id == FreshProduct);
        Assert.Equal(10, product.QuantityInStock);
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(25m, product.InventoryValue);
        Assert.Equal(2.50m, product.AverageUnitCost);

        Assert.Equal(before, await SnapshotCostedLineAsync());
    }

    [Fact]
    public async Task UpdateAsync_repeating_an_unchanged_edit_does_not_duplicate_the_new_products_stock()
    {
        var (purchaseId, existingLineId) = await SeedPurchaseOfCostedProductAsync(freshProductStock: 0);
        await UpdateAsync(purchaseId,
            new PurchaseItemInput(CostedProduct, 4m, 1.50m, null, existingLineId),
            new PurchaseItemInput(FreshProduct, 10m, 2.50m));
        int freshLineId;
        await using (var read = TestAppDbContext.For(_options, BusinessA))
            freshLineId = (await read.ReceiptItems.SingleAsync(item => item.ProductId == FreshProduct)).Id;

        await UpdateAsync(purchaseId,
            new PurchaseItemInput(CostedProduct, 4m, 1.50m, null, existingLineId),
            new PurchaseItemInput(FreshProduct, 10m, 2.50m, null, freshLineId));

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Equal(freshLineId, (await verify.ReceiptItems.SingleAsync(item => item.ProductId == FreshProduct)).Id);
        Assert.Equal(1, await verify.StockAdjustments.CountAsync(x => x.ProductId == FreshProduct));
        var product = await verify.Products.SingleAsync(p => p.Id == FreshProduct);
        Assert.Equal(10, product.QuantityInStock);
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(25m, product.InventoryValue);
        Assert.Equal(4, (await verify.Products.SingleAsync(p => p.Id == CostedProduct)).QuantityInStock);
    }

    [Fact]
    public async Task UpdateAsync_changing_and_then_removing_the_new_products_line_moves_its_stock_with_it()
    {
        var (purchaseId, existingLineId) = await SeedPurchaseOfCostedProductAsync(freshProductStock: 0);
        await UpdateAsync(purchaseId,
            new PurchaseItemInput(CostedProduct, 4m, 1.50m, null, existingLineId),
            new PurchaseItemInput(FreshProduct, 10m, 2.50m));
        int freshLineId;
        await using (var read = TestAppDbContext.For(_options, BusinessA))
            freshLineId = (await read.ReceiptItems.SingleAsync(item => item.ProductId == FreshProduct)).Id;

        await UpdateAsync(purchaseId,
            new PurchaseItemInput(CostedProduct, 4m, 1.50m, null, existingLineId),
            new PurchaseItemInput(FreshProduct, 6m, 2.50m, null, freshLineId));
        await using (var verify = TestAppDbContext.For(_options, BusinessA))
        {
            var product = await verify.Products.SingleAsync(p => p.Id == FreshProduct);
            Assert.Equal(6, product.QuantityInStock);
            Assert.Equal(6, product.CostingQuantity);
            Assert.Equal(15m, product.InventoryValue);
        }

        await UpdateAsync(purchaseId, new PurchaseItemInput(CostedProduct, 4m, 1.50m, null, existingLineId));
        await using var after = TestAppDbContext.For(_options, BusinessA);
        Assert.Empty(await after.StockAdjustments.Where(x => x.ProductId == FreshProduct).ToListAsync());
        var removed = await after.Products.SingleAsync(p => p.Id == FreshProduct);
        Assert.Equal(0, removed.QuantityInStock);
        Assert.Equal(0, removed.CostingQuantity);
        Assert.Equal(0m, removed.InventoryValue);
    }

    [Fact]
    public async Task UpdateAsync_still_refuses_and_rolls_back_a_never_costed_product_whose_stock_does_not_reconcile()
    {
        // Three units on the shelf with no movement, baseline or costing state behind them: the
        // MissingOpening guard must keep refusing this product, edit or no edit.
        var (purchaseId, existingLineId) = await SeedPurchaseOfCostedProductAsync(freshProductStock: 3);
        var before = await SnapshotCostedLineAsync();

        var refusal = await Assert.ThrowsAsync<InventoryCostDataQualityException>(() => UpdateAsync(purchaseId,
            new PurchaseItemInput(CostedProduct, 4m, 1.50m, null, existingLineId),
            new PurchaseItemInput(FreshProduct, 10m, 2.50m)));
        Assert.Contains($"Product New product ({FreshProduct}) stored physical quantity 13 does not reconcile to its movement history (10).", refusal.Message);

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Empty(await verify.ReceiptItems.Where(item => item.ProductId == FreshProduct).ToListAsync());
        Assert.Empty(await verify.StockAdjustments.Where(x => x.ProductId == FreshProduct).ToListAsync());
        var product = await verify.Products.SingleAsync(p => p.Id == FreshProduct);
        Assert.Equal(3, product.QuantityInStock);
        Assert.Null(product.CostingQuantity);
        Assert.Null(product.InventoryValue);
        Assert.Equal(before, await SnapshotCostedLineAsync());
    }

    [Fact]
    public async Task UpdateAsync_cannot_add_another_businesss_product_and_leaves_its_stock_alone()
    {
        var (purchaseId, existingLineId) = await SeedPurchaseOfCostedProductAsync(freshProductStock: 0);
        await using (var b = TestAppDbContext.For(_options, BusinessB))
        {
            b.Products.Add(new Product { Id = OtherBusinessProduct, Name = "Business B product" });
            await b.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(purchaseId,
            new PurchaseItemInput(CostedProduct, 4m, 1.50m, null, existingLineId),
            new PurchaseItemInput(OtherBusinessProduct, 10m, 2.50m)));

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await verify.StockAdjustments.Where(x => x.ProductId == OtherBusinessProduct).ToListAsync());
        Assert.Equal(0, (await verify.Products.SingleAsync(p => p.Id == OtherBusinessProduct)).QuantityInStock);
        Assert.Equal(1, await verify.ReceiptItems.CountAsync(item => item.ReceiptId == purchaseId));
    }

    [Fact]
    public async Task CreateAsync_of_a_never_costed_product_records_its_stock_and_cost()
    {
        await using (var seed = TestAppDbContext.For(_options, BusinessA))
        {
            seed.Products.Add(new Product { Id = FreshProduct, Name = "New product" });
            await seed.SaveChangesAsync();
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed).CreateAsync(
                new PurchaseFields("Restock", null, null, null, null, PurchaseDate, null),
                [new PurchaseItemInput(FreshProduct, 10m, 2.50m)],
                new PurchaseFileMetadata("stored.jpg", "image/jpeg", "receipt.jpg", 3),
                supplierOrderCompletion: null,
                attachmentReservation: null,
                CancellationToken.None);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Equal(1, await verify.StockAdjustments.CountAsync(x => x.ProductId == FreshProduct && x.ReceiptItemId.HasValue));
        var product = await verify.Products.SingleAsync(p => p.Id == FreshProduct);
        Assert.Equal(10, product.QuantityInStock);
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(25m, product.InventoryValue);
    }

    [Fact]
    public async Task CreateAsync_still_refuses_unrecorded_stock_even_when_it_equals_the_purchased_quantity()
    {
        // Ten units with nothing behind them, then a purchase of ten more: twenty on the shelf, ten
        // in the history. This used to be accepted by coincidence and lose the purchased units.
        await using (var seed = TestAppDbContext.For(_options, BusinessA))
        {
            seed.Products.Add(new Product { Id = FreshProduct, Name = "New product", QuantityInStock = 10 });
            await seed.SaveChangesAsync();
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var refusal = await Assert.ThrowsAsync<InventoryCostDataQualityException>(() =>
                new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed).CreateAsync(
                    new PurchaseFields("Restock", null, null, null, null, PurchaseDate, null),
                    [new PurchaseItemInput(FreshProduct, 10m, 2.50m)],
                    new PurchaseFileMetadata("stored.jpg", "image/jpeg", "receipt.jpg", 3),
                    supplierOrderCompletion: null,
                    attachmentReservation: null,
                    CancellationToken.None));
            Assert.Contains($"Product New product ({FreshProduct}) stored physical quantity 20 does not reconcile to its movement history (10).", refusal.Message);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Empty(await verify.Receipts.ToListAsync());
        Assert.Empty(await verify.StockAdjustments.ToListAsync());
        var product = await verify.Products.SingleAsync(p => p.Id == FreshProduct);
        Assert.Equal(10, product.QuantityInStock);
        Assert.Null(product.CostingQuantity);
    }

    /// <summary>
    /// A purchase of 4 units of an already-costed product (it has a transition baseline), and a
    /// second product with no baseline, no costing state and no movements, holding
    /// <paramref name="freshProductStock"/> units.
    /// </summary>
    private async Task<(int PurchaseId, int ExistingLineId)> SeedPurchaseOfCostedProductAsync(int freshProductStock)
    {
        await using (var seed = TestAppDbContext.For(_options, BusinessA))
        {
            seed.Products.Add(new Product { Id = CostedProduct, Name = "Costed product" });
            seed.Products.Add(new Product { Id = FreshProduct, Name = "New product", QuantityInStock = freshProductStock });
            seed.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
            {
                ProductId = CostedProduct,
                CutoffAt = PurchaseDate.AddDays(-1),
                HomeStockQuantity = 0,
                CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(_options, BusinessA);
        var purchase = await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed).CreateAsync(
            new PurchaseFields("Restock", null, null, null, null, PurchaseDate, null),
            [new PurchaseItemInput(CostedProduct, 4m, 1.50m)],
            new PurchaseFileMetadata("stored.jpg", "image/jpeg", "receipt.jpg", 3),
            supplierOrderCompletion: null,
            attachmentReservation: null,
            CancellationToken.None);
        return (purchase.Id, purchase.Items.Single().Id);
    }

    private async Task UpdateAsync(int purchaseId, params PurchaseItemInput[] items)
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);
        await store.UpdateAsync(
            purchaseId, new PurchaseFields("Restock", null, null, null, null, PurchaseDate, null), items, CancellationToken.None);
    }

    private async Task<string> SnapshotCostedLineAsync()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var line = await db.ReceiptItems.AsNoTracking().SingleAsync(item => item.ProductId == CostedProduct);
        var movement = await db.StockAdjustments.AsNoTracking().SingleAsync(x => x.ProductId == CostedProduct);
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == CostedProduct);
        return $"{line.Id}|{line.Quantity}|{line.UnitCost}|{movement.Id}|{movement.QuantityChange}|{movement.UnitCost}|" +
            $"{product.QuantityInStock}|{product.CostingQuantity}|{product.InventoryValue}|{product.AverageUnitCost}";
    }
}
