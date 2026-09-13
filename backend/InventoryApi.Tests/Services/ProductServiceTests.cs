using System;
using System.Collections.Generic;
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

        var dto = new ProductCreateDto("p1", null, null, 10m, 5, 1, "unit", null, null, true);
        var product = await svc.Create(new ProductCreateDto("p1", null, null, 10m, 5, 1, "unit", null, null, true));

        Assert.NotNull(product);
        Assert.Equal("p1", product.Name);

        // Verify stock adjustment created
        var adjustments = await db.StockAdjustments.ToListAsync();
        Assert.Single(adjustments);

        var updateDto = new ProductUpdateDto("p1-up", null, null, 12m, 1, "unit", null, null, true);
        var ok = await svc.Update(product.Id, updateDto);
        Assert.True(ok);

        var deleted = await svc.Delete(product.Id);
        Assert.True(deleted);
    }

    [Fact]
    public async Task LowStock_OpenOrder_ReducesRemainingReorderRequirement()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 4, LowStockThreshold = 20 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12 } } });
        await db.SaveChangesAsync();

        var alerts = await CreateService(db).LowStock();
        var product = Assert.Single(alerts);
        Assert.Equal(12m, product.OnOrderQuantity);
        Assert.Equal(4m, product.ReorderShortfall);
    }

    [Fact]
    public async Task LowStock_FullyCoveredOrder_DoesNotAppearInNeedsOrdering()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 4, LowStockThreshold = 16 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12 } } });
        await db.SaveChangesAsync();

        Assert.Empty(await CreateService(db).LowStock());
    }

    [Fact]
    public async Task LowStock_PartialOutstandingOrder_UsesOnlyOutstandingQuantity()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 4, LowStockThreshold = 20 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Status = SupplierOrderStatus.PartiallyReceived, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12, QuantityReceived = 8 } } });
        await db.SaveChangesAsync();

        var product = Assert.Single(await CreateService(db).LowStock());
        Assert.Equal(4m, product.OnOrderQuantity);
        Assert.Equal(12m, product.ReorderShortfall);
    }

    [Fact]
    public async Task LowStock_CancelledOrder_DoesNotCountAsInboundStock()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 4, LowStockThreshold = 20 });
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Status = SupplierOrderStatus.Cancelled, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 12 } } });
        await db.SaveChangesAsync();

        var product = Assert.Single(await CreateService(db).LowStock());
        Assert.Equal(0m, product.OnOrderQuantity);
        Assert.Equal(16m, product.ReorderShortfall);
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
