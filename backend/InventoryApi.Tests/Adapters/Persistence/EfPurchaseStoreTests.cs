using Inventory.Application.Costing;
using Inventory.Application.Purchases;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests for <see cref="EfPurchaseStore"/>'s transaction and ordering behaviour:
/// a real multi-order FIFO allocation (ordering the SQL provider itself must honour) and a failed
/// delete rolling back every step it already performed - something an EF Core InMemory database,
/// which never runs a real transaction, cannot prove (issue #281).
/// </summary>
public sealed class EfPurchaseStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfPurchaseStoreTests()
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

    private static PurchaseFileMetadata FileMetadata() => new("stored.jpg", "image/jpeg", "receipt.jpg", 3);

    [Fact]
    public async Task CreateAsync_allocates_the_oldest_outstanding_supplier_order_first()
    {
        await using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.Products.Add(new Product { Id = 1, Name = "Coke" });
            seed.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
            {
                ProductId = 1,
                CutoffAt = DateTime.UtcNow.AddDays(-1),
                HomeStockQuantity = 0,
                CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            });
            seed.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
            seed.SupplierOrders.AddRange(
                new SupplierOrder { SupplierId = 1, OrderDate = new DateTime(2026, 1, 1), Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } },
                new SupplierOrder { SupplierId = 1, OrderDate = new DateTime(2026, 1, 2), Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } });
            await seed.SaveChangesAsync();
        }

        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db));
            await store.CreateAsync(
                new PurchaseFields("Restock", null, null, null, null, new DateTime(2026, 1, 3), 1),
                [new PurchaseItemInput(1, 12m, 1m)],
                FileMetadata(),
                CancellationToken.None);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var lines = await verify.SupplierOrderLines.Include(line => line.SupplierOrder)
            .OrderBy(line => line.SupplierOrder.OrderDate).ToListAsync();
        Assert.Equal(10m, lines[0].QuantityReceived);
        Assert.Equal(2m, lines[1].QuantityReceived);
    }

    [Fact]
    public async Task DeleteAsync_rolls_back_every_step_when_the_inventory_cost_rebuild_fails()
    {
        int purchaseId;
        await using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 10, AverageUnitCost = 1m });
            await seed.SaveChangesAsync();
            var purchase = new Purchase
            {
                Title = "Restock",
                FileName = "receipt.jpg",
                StoredFileName = "stored.jpg",
                ContentType = "image/jpeg",
                PurchaseDate = DateTime.UtcNow,
                Items = { new PurchaseItem { ProductId = 1, Quantity = 10, UnitCost = 1m } },
            };
            seed.Receipts.Add(purchase);
            await seed.SaveChangesAsync();
            seed.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = 1,
                ReceiptItemId = purchase.Items.Single().Id,
                QuantityChange = 10,
                Reason = StockAdjustmentReason.Restock,
                UnitCost = 1m,
                TotalCost = 10m,
                EffectiveAt = purchase.PurchaseDate,
            });
            await seed.SaveChangesAsync();
            purchaseId = purchase.Id;
        }

        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var failingRebuild = new Mock<IRebuildProductCost>();
            failingRebuild
                .Setup(x => x.RebuildAsync(It.IsAny<long>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("rebuild failure"));
            var store = new EfPurchaseStore(db, failingRebuild.Object);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(purchaseId, CancellationToken.None));
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.NotNull(await verify.Receipts.FindAsync(purchaseId));
        Assert.NotEmpty(await verify.StockAdjustments.Where(x => x.ProductId == 1).ToListAsync());
    }
}
