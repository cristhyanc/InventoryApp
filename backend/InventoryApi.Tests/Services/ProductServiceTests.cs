using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class ProductServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Create_Update_Delete_Product_And_StockAdjustment_Created()
    {
        using var db = CreateDbContext("prod_test");
        var nayaxMock = new Mock<INayaxLynxClient>();
        IProductService svc = new ProductService(db, nayaxMock.Object);

        var dto = new ProductCreateDto("p1", null, null, 10m, 5, 1, 10, "unit", null, null, true);
        var product = await svc.Create(new ProductCreateDto("p1", null, null, 10m, 5, 1, 10, "unit", null, null, true));

        Assert.NotNull(product);
        Assert.Equal("p1", product.Name);
        Assert.Equal(10, product.RestockTo);

        // Verify stock adjustment created
        var adjustments = await db.StockAdjustments.ToListAsync();
        Assert.Single(adjustments);

        var updateDto = new ProductUpdateDto("p1-up", null, null, 12m, 1, 12, "unit", null, null, true);
        var ok = await svc.Update(product.Id, updateDto);
        Assert.True(ok);
        Assert.Equal(12, (await db.Products.FindAsync(product.Id))!.RestockTo);

        var deleted = await svc.Delete(product.Id);
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
            await using (var db = new AppDbContext(options))
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

        var alerts = await CreateService(db).LowStock();

        Assert.Empty(alerts);
    }

    [Fact]
    public async Task LowStock_OnOrderQuantity_IsRetainedOnReturnedProduct()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Party Mix", QuantityInStock = 2, LowStockThreshold = 10, RestockTo = 17 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 8 } } });
        await db.SaveChangesAsync();

        var product = Assert.Single(await CreateService(db).LowStock());

        Assert.Equal(8m, product.OnOrderQuantity);
        Assert.Equal(7m, product.NeedToOrder);
        Assert.True(product.IsReorderAlert);
    }

    [Fact]
    public async Task Create_RejectsInvalidRestockSettings()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Create(
            new ProductCreateDto("Coke", null, null, 1m, 0, 10, 9, "unit", null, null, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Create(
            new ProductCreateDto("Coke", null, null, 1m, 0, -1, 0, "unit", null, null, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Create(
            new ProductCreateDto("Coke", null, null, 1m, 0, 0, -1, "unit", null, null, true)));
    }

    [Fact]
    public async Task Update_RejectsInvalidRestockSettings()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", LowStockThreshold = 5, RestockTo = 10 });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(db).Update(1,
            new ProductUpdateDto("Coke", null, null, 1m, 10, 9, "unit", null, null, true)));
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

        var alerts = (await CreateService(db).LowStock()).ToList();

        Assert.Equal(new[] { "Alpha", "Zulu", "Middle" }, alerts.Select(product => product.Name));
    }

    [Fact]
    public async Task LowStock_OpenOrder_ReducesNeedToOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 4, LowStockThreshold = 20, RestockTo = 20 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12 } } });
        await db.SaveChangesAsync();

        var alerts = await CreateService(db).LowStock();
        var product = Assert.Single(alerts);
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

        Assert.Empty(await CreateService(db).LowStock());
    }

    [Fact]
    public async Task LowStock_PartialOutstandingOrder_UsesOnlyOutstandingQuantity()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 4, LowStockThreshold = 20, RestockTo = 20 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Status = SupplierOrderStatus.PartiallyReceived, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12, QuantityReceived = 8 } } });
        await db.SaveChangesAsync();

        var product = Assert.Single(await CreateService(db).LowStock());
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

        var product = Assert.Single(await CreateService(db).LowStock());
        Assert.Equal(0m, product.OnOrderQuantity);
        Assert.Equal(16m, product.NeedToOrder);
    }

    [Fact]
    public async Task SupplierOrder_Create_RejectsFractionalQuantities()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Supplier" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        await db.SaveChangesAsync();

        var service = new SupplierOrderService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Create(
            new SupplierOrderCreateDto(1, DateTime.UtcNow, null, null, null,
                new[] { new SupplierOrderLineCreateDto(1, 1.5m) })));
    }

    private static ProductService CreateService(AppDbContext db)
    {
        var nayaxMock = new Mock<INayaxLynxClient>();
        nayaxMock.Setup(client => client.GetMachinesAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>());
        return new ProductService(db, nayaxMock.Object);
    }

}
