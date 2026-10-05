using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Reorder;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Adapters.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

/// <summary>
/// The former <c>ProductServiceTests</c>, retargeted onto the
/// <see cref="Inventory.Application.Products"/> use cases over the real EF adapters when issue #303
/// deleted the <c>ProductService</c> delegator the product endpoints used to call. The behaviour
/// covered is unchanged - the explicitly loaded stock-adjustment history, the create/update/delete
/// round trip, the restock-settings validation, the reorder-alert selection and ranking with
/// outstanding supplier orders, and the restock-to migration backfill - and the response-shape test
/// now asserts the API-owned <c>ProductResponse</c> the endpoints serialise instead of the entity.
/// </summary>
public class ProductUseCaseTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return TestAppDbContext.Unrestricted(options);
    }

    /// <summary>
    /// Relational SQLite test: each product's stock-adjustment history must come back from an
    /// explicit query rather than lazy loading (issue #52), so a caller reading a query-loaded
    /// product's <c>StockAdjustments</c> after this context is disposed still sees it.
    /// </summary>
    [Fact]
    public async Task ListProducts_IncludesStockAdjustmentHistoryWithoutLazyLoading()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var schema = TestAppDbContext.Unrestricted(options))
            await schema.Database.EnsureCreatedAsync();

        await using (var seed = TestAppDbContext.Unrestricted(options))
        {
            seed.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 5 });
            await seed.SaveChangesAsync();
            seed.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = 1,
                QuantityChange = 5,
                QuantityAfter = 5,
                Reason = StockAdjustmentReason.Restock,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.Unrestricted(options);
        var useCases = CreateUseCases(db);

        var products = await useCases.List.Handle(ProductCatalogFilter.None, null, CancellationToken.None);

        var product = Assert.Single(products);
        Assert.Single(product.StockAdjustments);
    }

    /// <summary>
    /// Issue #240 moved the product reads behind Application use cases and a read port; issue #303
    /// replaced the entity-shaped response with the API-owned <c>ProductResponse</c>. This pins that
    /// the whole response shape survives the round trip: the scalar catalogue/costing fields, the
    /// nested category and supplier, the stock-adjustment history, and the derived reorder values.
    /// The serialised bytes themselves are pinned by
    /// <c>InventoryApi.Tests.DTOs.ProductJsonContractTests</c>.
    /// </summary>
    [Fact]
    public async Task GetProduct_MapsTheCompleteProductResponseShape_IncludingNestedDetailAndDerivedValues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var schema = TestAppDbContext.Unrestricted(options))
            await schema.Database.EnsureCreatedAsync();

        await using (var seed = TestAppDbContext.Unrestricted(options))
        {
            seed.Categories.Add(new Category { Id = 3, Name = "Drinks", Description = "Cold" });
            seed.Suppliers.Add(new Supplier
            {
                Id = 4,
                Name = "Acme",
                ContactName = "Pat",
                Phone = "123",
                Email = "a@b.c",
                Address = "1 Road",
            });
            seed.Products.Add(new Product
            {
                Id = 1,
                Name = "Coke",
                Sku = "SKU-1",
                Description = "A can",
                UnitPrice = 3.50m,
                AverageUnitCost = 1.25m,
                CostingQuantity = 7,
                InventoryValue = 8.75m,
                QuantityInStock = 4,
                LowStockThreshold = 10,
                RestockTo = 20,
                Unit = "can",
                IsActive = true,
                CategoryId = 3,
                SupplierId = 4,
            });
            await seed.SaveChangesAsync();
            seed.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = 1,
                QuantityChange = 4,
                QuantityAfter = 4,
                UnitCost = 1.25m,
                TotalCost = 5m,
                Reason = StockAdjustmentReason.Restock,
                Source = StockAdjustmentSource.Nayax,
                Notes = "Initial",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.Unrestricted(options);

        var record = await CreateUseCases(db).Get.Handle(1, CancellationToken.None);

        Assert.NotNull(record);
        var product = ProductRecordResponseMapper.ToResponse(record);
        Assert.Equal("Coke", product.Name);
        Assert.Equal("SKU-1", product.Sku);
        Assert.Equal("A can", product.Description);
        Assert.Equal(3.50m, product.UnitPrice);
        Assert.Equal(1.25m, product.AverageUnitCost);
        Assert.Equal(7, product.CostingQuantity);
        Assert.Equal(8.75m, product.InventoryValue);
        Assert.Equal(4, product.QuantityInStock);
        Assert.Equal(10, product.LowStockThreshold);
        Assert.Equal(20, product.RestockTo);
        Assert.Equal("can", product.Unit);
        Assert.True(product.IsActive);
        Assert.Equal(3, product.CategoryId);
        Assert.Equal("Drinks", product.Category!.Name);
        Assert.Equal("Cold", product.Category.Description);
        Assert.Equal(4, product.SupplierId);
        Assert.Equal("Acme", product.Supplier!.Name);
        Assert.Equal("Pat", product.Supplier.ContactName);
        Assert.Equal("1 Road", product.Supplier.Address);

        var adjustment = Assert.Single(product.StockAdjustments);
        Assert.Equal(4, adjustment.QuantityChange);
        Assert.Equal(1.25m, adjustment.UnitCost);
        Assert.Equal(StockAdjustmentReason.Restock, adjustment.Reason);
        Assert.Equal(StockAdjustmentSource.Nayax, adjustment.Source);
        Assert.Equal("Initial", adjustment.Notes);

        // Derived reorder values: the plain listing and single read resolve no live machine need or
        // outstanding orders, so these follow from the persisted fields alone, exactly as before.
        Assert.Equal(0, product.MachineReplenishmentNeed);
        Assert.Equal(0m, product.OnOrderQuantity);
        Assert.Equal(4m, product.ProjectedStockForReorder);
        Assert.Equal(16m, product.NeedToOrder);
        Assert.True(product.IsLowStock);
        Assert.True(product.IsReorderAlert);
    }

    [Fact]
    public async Task Create_Update_Delete_Product_And_StockAdjustment_Created()
    {
        using var db = CreateDbContext("prod_test");
        var calculateReorderNeeds = new CalculateReorderNeeds(
            new Mock<INayaxLynxClient>().Object, new EfOutstandingSupplierOrderQuantityStore(db));
        var useCases = CreateUseCases(db, calculateReorderNeeds);

        var created = await useCases.Create.Handle(
            new ProductCreateFields("p1", null, null, 10m, 5, 1, 10, "unit", null, null, true, null),
            CancellationToken.None);

        Assert.True(created.IsValid);
        var product = await useCases.Get.Handle(created.ProductId!.Value, CancellationToken.None);
        Assert.NotNull(product);
        Assert.Equal("p1", product.Name);
        Assert.Equal(10, product.RestockTo);

        // Verify stock adjustment created
        var adjustments = await db.StockAdjustments.ToListAsync();
        Assert.Single(adjustments);

        var updated = await useCases.Update.Handle(
            product.Id, new ProductUpdateFields(null, null, 1, 12, "unit", null, true), CancellationToken.None);
        Assert.Equal(UpdateProductOutcome.Success, updated.Outcome);
        Assert.Equal(12, (await db.Products.FindAsync(product.Id))!.RestockTo);

        var deleted = await useCases.Delete.Handle(product.Id, CancellationToken.None);
        Assert.True(deleted);
    }

    [Fact]
    public async Task AddProductRestockTo_BackfillsExistingProductsFromLowStockThreshold()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"inventory-migration-{Guid.NewGuid()}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;

        try
        {
            await using (var db = TestAppDbContext.Unrestricted(options))
            {
                await db.Database.MigrateAsync("20260913011850_AddSupplierOrderReceiptAllocations");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Products" ("Name", "UnitPrice", "AverageUnitCost", "QuantityInStock", "LowStockThreshold", "IsActive", "CreatedAt", "UpdatedAt")
                    VALUES ('Legacy product', 1, 0, 7, 23, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                    """);

                await db.Database.MigrateAsync();
                var product = await db.Products.SingleAsync(product => product.Name == "Legacy product");

                Assert.Equal(23, product.RestockTo);
            }
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Theory]
    [InlineData(32, 3, 57, 114, 0, 85)]
    [InlineData(32, 3, 57, 114, 20, 65)]
    [InlineData(70, 0, 57, 114, 0, 0)]
    [InlineData(60, 3, 57, 114, 0, 57)]
    [InlineData(32, 3, 57, 114, 85, 0)]
    public void NeedToOrder_UsesProjectedStockForReorderAndOutstandingOrders(
        int stock, int machineNeed, int threshold, int restockTo, decimal onOrder, decimal expectedNeed)
    {
        var product = new Product
        {
            QuantityInStock = stock,
            MachineReplenishmentNeed = machineNeed,
            LowStockThreshold = threshold,
            RestockTo = restockTo,
            OnOrderQuantity = onOrder
        };

        Assert.Equal(expectedNeed, product.NeedToOrder);
        Assert.Equal(expectedNeed > 0, product.IsReorderAlert);
    }

    [Theory]
    [InlineData(2, 1, 10, 17, 12, 13, 0, false)] // Party Mix regression: outstanding order covers projected need
    [InlineData(2, 1, 10, 17, 8, 9, 8, true)]   // Incoming order is not sufficient
    [InlineData(2, 1, 10, 17, 9, 10, 7, true)]  // Exactly at threshold remains reorder-triggered (inclusive)
    [InlineData(5, 1, 4, 6, 0, 4, 2, true)]      // No outstanding order preserves existing behavior
    public void NeedToOrder_RegressionCases(
        int stock, int machineNeed, int threshold, int restockTo, decimal onOrder,
        decimal expectedProjectedStock, decimal expectedNeed, bool expectedAlert)
    {
        var product = new Product
        {
            QuantityInStock = stock,
            MachineReplenishmentNeed = machineNeed,
            LowStockThreshold = threshold,
            RestockTo = restockTo,
            OnOrderQuantity = onOrder
        };

        Assert.Equal(expectedProjectedStock, product.ProjectedStockForReorder);
        Assert.Equal(expectedNeed, product.NeedToOrder);
        Assert.Equal(expectedAlert, product.IsReorderAlert);
    }

    [Fact]
    public async Task LowStock_PartyMix_FullyCoveredByOutstandingOrder_IsNotReturned()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Party Mix", QuantityInStock = 2, LowStockThreshold = 10, RestockTo = 17 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12 } } });
        await db.SaveChangesAsync();

        var alerts = await LowStock(db);

        Assert.Empty(alerts);
    }

    [Fact]
    public async Task LowStock_OnOrderQuantity_IsRetainedOnReturnedProduct()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Party Mix", QuantityInStock = 2, LowStockThreshold = 10, RestockTo = 17 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 8 } } });
        await db.SaveChangesAsync();

        var product = Assert.Single(await LowStock(db));

        Assert.Equal(8m, product.OnOrderQuantity);
        Assert.Equal(7m, product.NeedToOrder);
        Assert.True(product.IsReorderAlert);
    }

    /// <summary>
    /// Issue #47: proves the wiring through the extracted <see cref="CalculateReorderNeeds"/> use case
    /// still reproduces the exact aggregate <c>MachineReplenishmentNeed</c> the legacy sequential loop
    /// produced - summed across multiple machines reporting the same product, and unaffected by a
    /// machine-product entry with no matching local product.
    /// </summary>
    [Fact]
    public async Task LowStock_AggregatesMachineReplenishmentNeed_AcrossMultipleMachinesAndIgnoresUnmappedMachineProducts()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.AddRange(
            new Product { Id = 1, Name = "Coke", QuantityInStock = 10, LowStockThreshold = 5, RestockTo = 20 },
            new Product { Id = 2, Name = "Chips", QuantityInStock = 10, LowStockThreshold = 5, RestockTo = 20 });
        await db.SaveChangesAsync();

        var nayaxMock = new Mock<INayaxLynxClient>();
        nayaxMock.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1 }, new() { MachineID = 2 } });
        nayaxMock.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 1, MissingStockByMDB = 3 },
                new() { NayaxProductID = 999, MissingStockByMDB = 7 }, // no matching local product; must be ignored
            });
        nayaxMock.Setup(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 1, MissingStockByMDB = 4 } });
        var calculateReorderNeeds = new CalculateReorderNeeds(nayaxMock.Object, new EfOutstandingSupplierOrderQuantityStore(db));
        var useCases = CreateUseCases(db, calculateReorderNeeds);

        var alerts = await useCases.LowStock.Handle(ProductCatalogFilter.None, CancellationToken.None);

        var coke = Assert.Single(alerts);
        Assert.Equal("Coke", coke.Name);
        Assert.Equal(7, coke.MachineReplenishmentNeed);
    }

    [Fact]
    public async Task Create_RejectsInvalidRestockSettings()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var create = CreateUseCases(db).Create;

        foreach (var fields in new[]
        {
            new ProductCreateFields("Coke", null, null, 1m, 0, 10, 9, "unit", null, null, true, null),
            new ProductCreateFields("Coke", null, null, 1m, 0, -1, 0, "unit", null, null, true, null),
            new ProductCreateFields("Coke", null, null, 1m, 0, 0, -1, "unit", null, null, true, null),
        })
        {
            var result = await create.Handle(fields, CancellationToken.None);

            Assert.False(result.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(result.ValidationError));
            Assert.Null(result.ProductId);
        }

        Assert.Empty(await db.Products.ToListAsync());
    }

    [Fact]
    public async Task Update_RejectsInvalidRestockSettings()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", LowStockThreshold = 5, RestockTo = 10 });
        await db.SaveChangesAsync();

        var result = await CreateUseCases(db).Update.Handle(
            1, new ProductUpdateFields(null, null, 10, 9, "unit", null, true), CancellationToken.None);

        Assert.Equal(UpdateProductOutcome.Invalid, result.Outcome);
        Assert.Equal("Restock To must be greater than or equal to the Low Stock Threshold.", result.ValidationError);
    }

    [Fact]
    public async Task LowStock_OrdersByNeedToOrderDescendingThenName()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.AddRange(
            new Product { Id = 1, Name = "Zulu", QuantityInStock = 10, LowStockThreshold = 20, RestockTo = 40 },
            new Product { Id = 2, Name = "Alpha", QuantityInStock = 10, LowStockThreshold = 20, RestockTo = 40 },
            new Product { Id = 3, Name = "Middle", QuantityInStock = 10, LowStockThreshold = 20, RestockTo = 30 });
        await db.SaveChangesAsync();

        var alerts = await LowStock(db);

        Assert.Equal(new[] { "Alpha", "Zulu", "Middle" }, alerts.Select(product => product.Name));
    }

    [Fact]
    public async Task LowStock_OpenOrder_ReducesNeedToOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 4, LowStockThreshold = 20, RestockTo = 20 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12 } } });
        await db.SaveChangesAsync();

        var product = Assert.Single(await LowStock(db));
        Assert.Equal(12m, product.OnOrderQuantity);
        Assert.Equal(4m, product.NeedToOrder);
    }

    [Fact]
    public async Task LowStock_FullyCoveredOrder_DoesNotAppearInNeedsOrdering()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 4, LowStockThreshold = 16, RestockTo = 16 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12 } } });
        await db.SaveChangesAsync();

        Assert.Empty(await LowStock(db));
    }

    [Fact]
    public async Task LowStock_PartialOutstandingOrder_UsesOnlyOutstandingQuantity()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 4, LowStockThreshold = 20, RestockTo = 20 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Status = SupplierOrderStatus.PartiallyReceived, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12, QuantityReceived = 8 } } });
        await db.SaveChangesAsync();

        var product = Assert.Single(await LowStock(db));
        Assert.Equal(4m, product.OnOrderQuantity);
        Assert.Equal(12m, product.NeedToOrder);
    }

    [Fact]
    public async Task LowStock_CancelledOrder_DoesNotCountAsInboundStock()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 4, LowStockThreshold = 20, RestockTo = 20 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Status = SupplierOrderStatus.Cancelled, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12 } } });
        await db.SaveChangesAsync();

        var product = Assert.Single(await LowStock(db));
        Assert.Equal(0m, product.OnOrderQuantity);
        Assert.Equal(16m, product.NeedToOrder);
    }

    private static Task<IReadOnlyList<ProductRecord>> LowStock(AppDbContext db) =>
        CreateUseCases(db).LowStock.Handle(ProductCatalogFilter.None, CancellationToken.None);

    private static ProductUseCases CreateUseCases(AppDbContext db)
    {
        var nayaxMock = new Mock<INayaxLynxClient>();
        nayaxMock.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>());
        var calculateReorderNeeds = new CalculateReorderNeeds(nayaxMock.Object, new EfOutstandingSupplierOrderQuantityStore(db));
        return CreateUseCases(db, calculateReorderNeeds);
    }

    private static ProductUseCases CreateUseCases(AppDbContext db, CalculateReorderNeeds calculateReorderNeeds)
    {
        var store = new EfProductStore(db, TestCostingUseCases.Rebuild(db));
        var catalog = new EfProductCatalogStore(db);
        var listLowStockProducts = new ListLowStockProducts(catalog, calculateReorderNeeds);
        return new ProductUseCases(
            new ListProducts(catalog, listLowStockProducts),
            new GetProduct(catalog),
            listLowStockProducts,
            new CreateProduct(store),
            new UpdateProduct(store),
            new DeleteProduct(store));
    }

    /// <summary>
    /// The product use cases the controller is composed from, bundled only so these tests can build
    /// them in one step. It holds no behaviour of its own.
    /// </summary>
    private sealed record ProductUseCases(
        ListProducts List,
        GetProduct Get,
        ListLowStockProducts LowStock,
        CreateProduct Create,
        UpdateProduct Update,
        DeleteProduct Delete);
}
