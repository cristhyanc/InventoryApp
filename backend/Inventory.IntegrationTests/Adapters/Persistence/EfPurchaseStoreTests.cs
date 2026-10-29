using Inventory.Application.Costing;
using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
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

    /// <summary>
    /// Issue #833: the conflict lookup reports the product's name beside its ID and cutoff, so the
    /// refusal the operator sees names the product to fix. Which product is reported - the first
    /// conflicting one, in the given order - is unchanged.
    /// </summary>
    [Fact]
    public async Task FindConflictingCostTransitionBaselineAsync_reports_the_conflicting_products_name()
    {
        var cutoff = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.Products.AddRange(
                new Product { Id = 1, Name = "Coke" },
                new Product { Id = 2, Name = "Chips" });
            seed.InventoryCostTransitionBaselines.AddRange(
                new InventoryCostTransitionBaseline
                {
                    ProductId = 1,
                    CutoffAt = cutoff,
                    HomeStockQuantity = 0,
                    CostSource = InventoryCostBaselineSource.ManualAuthoritative,
                },
                new InventoryCostTransitionBaseline
                {
                    ProductId = 2,
                    CutoffAt = cutoff.AddDays(1),
                    HomeStockQuantity = 0,
                    CostSource = InventoryCostBaselineSource.ManualAuthoritative,
                });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.Unrestricted(_options);
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);

        Assert.Equal(
            (2L, "Chips", cutoff.AddDays(1)),
            await store.FindConflictingCostTransitionBaselineAsync([2, 1], cutoff, CancellationToken.None));
        Assert.Null(await store.FindConflictingCostTransitionBaselineAsync(
            [1, 2], cutoff.AddDays(2), CancellationToken.None));
    }

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
            var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);
            await store.CreateAsync(
                new PurchaseFields("Restock", null, null, null, null, new DateTime(2026, 1, 3), 1),
                [new PurchaseItemInput(1, 12m, 1m)],
                FileMetadata(),
                supplierOrderCompletion: null,
                attachmentReservation: null,
                CancellationToken.None);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var lines = await verify.SupplierOrderLines.Include(line => line.SupplierOrder)
            .OrderBy(line => line.SupplierOrder.OrderDate).ToListAsync();
        Assert.Equal(10m, lines[0].QuantityReceived);
        Assert.Equal(2m, lines[1].QuantityReceived);
    }

    /// <summary>
    /// The persistence half of the per-line price entry of issue #707: the Add Purchase form
    /// submits the unrounded quotient of the total paid over the line's units, so the stored unit
    /// cost has to keep enough precision for the line amount
    /// (<see cref="PurchaseGstPolicy.LineAmount"/>) to come back to the amount that was entered.
    /// $100.00 over 300 units is the case the issue names, and it is asserted here on a real SQLite
    /// database rather than on an InMemory one, because the round trip through the provider's
    /// <c>decimal</c> mapping is the part in question - a stored 0.3333 would be a cent short.
    /// </summary>
    [Fact]
    public async Task CreateAsync_keeps_an_unrounded_unit_cost_so_the_line_amount_reproduces_the_entered_total()
    {
        const decimal enteredLineTotal = 100.00m;
        const decimal quantity = 300m;
        var submittedUnitCost = 0.3333333333333333m;  // what the form's JSON number carries

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
            await seed.SaveChangesAsync();
        }

        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);
            await store.CreateAsync(
                new PurchaseFields("Box of 300", null, enteredLineTotal, null, null, DateTime.UtcNow, null),
                [new PurchaseItemInput(1, quantity, submittedUnitCost)],
                FileMetadata(),
                supplierOrderCompletion: null,
                attachmentReservation: null,
                CancellationToken.None);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var item = await verify.ReceiptItems.SingleAsync();
        Assert.Equal(quantity, item.Quantity);
        Assert.Equal(enteredLineTotal, PurchaseGstPolicy.LineAmount(item.Quantity, item.UnitCost));
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
                // All four together, or all four null (issue #976): the table's check constraint
                // refuses a half-recorded document, so a seeded one states its size as well.
                FileName = "receipt.jpg",
                StoredFileName = "stored.jpg",
                ContentType = "image/jpeg",
                FileSizeBytes = 3,
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
            var store = new EfPurchaseStore(db, failingRebuild.Object, TestClocks.Fixed);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(purchaseId, CancellationToken.None));
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.NotNull(await verify.Receipts.FindAsync(purchaseId));
        Assert.NotEmpty(await verify.StockAdjustments.Where(x => x.ProductId == 1).ToListAsync());
    }
}
