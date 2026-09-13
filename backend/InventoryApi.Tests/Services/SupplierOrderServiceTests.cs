using System;
using System.Linq;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Services;

public class SupplierOrderServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetById_ReturnsOrderWithSupplierProductsAndLines()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.Products.Add(new Product { Id = 2, Name = "Caramello" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            OrderDate = new DateTime(2026, 9, 1),
            ExpectedDate = new DateTime(2026, 9, 10),
            Reference = "Order #42",
            Lines = new[]
            {
                new SupplierOrderLine { ProductId = 1, QuantityOrdered = 24, UnitPrice = 0.50m },
                new SupplierOrderLine { ProductId = 2, QuantityOrdered = 12, UnitPrice = 1.00m }
            }
        });
        await db.SaveChangesAsync();

        var service = new SupplierOrderService(db);
        var order = await service.GetById(1);

        Assert.NotNull(order);
        Assert.Equal(1, order.Id);
        Assert.NotNull(order.Supplier);
        Assert.Equal("Costco", order.Supplier.Name);
        Assert.Equal(2, order.Lines.Count);
        var lines = order.Lines.ToList();
        Assert.Equal("Coke", lines[0].Product?.Name);
        Assert.Equal(24m, lines[0].QuantityOrdered);
        Assert.Equal(0.50m, lines[0].UnitPrice);
        Assert.Equal("Caramello", lines[1].Product?.Name);
        Assert.Equal(12m, lines[1].QuantityOrdered);
        Assert.Equal(1.00m, lines[1].UnitPrice);
    }

    [Fact]
    public async Task GetById_ReturnsNullForUnknownId()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        
        var service = new SupplierOrderService(db);
        var order = await service.GetById(999);

        Assert.Null(order);
    }

    [Fact]
    public async Task GetById_CalculatesOutstandingQuantityCorrectly()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Lines = new[]
            {
                new SupplierOrderLine { ProductId = 1, QuantityOrdered = 24, QuantityReceived = 6 }
            }
        });
        await db.SaveChangesAsync();

        var service = new SupplierOrderService(db);
        var order = await service.GetById(1);

        Assert.NotNull(order);
        var lines = order.Lines.ToList();
        Assert.Equal(24m, lines[0].QuantityOrdered);
        Assert.Equal(6m, lines[0].QuantityReceived);
        Assert.Equal(18m, lines[0].OutstandingQuantity);
    }

    [Fact]
    public async Task GetById_OutstandingQuantityIsZeroForFullyReceivedOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Status = SupplierOrderStatus.Received,
            Lines = new[]
            {
                new SupplierOrderLine { ProductId = 1, QuantityOrdered = 24, QuantityReceived = 24 }
            }
        });
        await db.SaveChangesAsync();

        var service = new SupplierOrderService(db);
        var order = await service.GetById(1);

        Assert.NotNull(order);
        var lines = order.Lines.ToList();
        Assert.Equal(0m, lines[0].OutstandingQuantity);
    }

    [Fact]
    public async Task GetById_OutstandingQuantityIsZeroForCancelledOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Status = SupplierOrderStatus.Cancelled,
            Lines = new[]
            {
                new SupplierOrderLine { ProductId = 1, QuantityOrdered = 24, QuantityReceived = 0 }
            }
        });
        await db.SaveChangesAsync();

        var service = new SupplierOrderService(db);
        var order = await service.GetById(1);

        Assert.NotNull(order);
        var lines = order.Lines.ToList();
        Assert.Equal(0m, lines[0].OutstandingQuantity);
    }

    [Fact]
    public async Task GetActive_ReturnsOnlyNonCancelledAndNonReceivedOrders()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.AddRange(
            new SupplierOrder { Id = 1, SupplierId = 1, Status = SupplierOrderStatus.Ordered, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } },
            new SupplierOrder { Id = 2, SupplierId = 1, Status = SupplierOrderStatus.PartiallyReceived, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } },
            new SupplierOrder { Id = 3, SupplierId = 1, Status = SupplierOrderStatus.Received, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } },
            new SupplierOrder { Id = 4, SupplierId = 1, Status = SupplierOrderStatus.Cancelled, Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } }
        );
        await db.SaveChangesAsync();

        var service = new SupplierOrderService(db);
        var orders = await service.GetActive();

        Assert.Equal(2, orders.Count());
        Assert.True(orders.All(o => o.Id == 1 || o.Id == 2));
    }

    [Fact]
    public async Task Create_CreatesOrderWithLines()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.Products.Add(new Product { Id = 2, Name = "Caramello" });
        await db.SaveChangesAsync();

        var service = new SupplierOrderService(db);
        var dto = new SupplierOrderCreateDto(
            1,
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 10),
            "Order #42",
            "Priority",
            new[]
            {
                new SupplierOrderLineCreateDto(1, 24, 0.50m),
                new SupplierOrderLineCreateDto(2, 12, 1.00m)
            }
        );
        var order = await service.Create(dto);

        Assert.NotNull(order);
        Assert.Equal(1, order.SupplierId);
        Assert.Equal("Order #42", order.Reference);
        Assert.Equal("Priority", order.Notes);
        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(SupplierOrderStatus.Ordered, order.Status);
    }

    [Fact]
    public async Task Cancel_CancelsOrderSuccessfully()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Status = SupplierOrderStatus.Ordered,
            Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } }
        });
        await db.SaveChangesAsync();

        var service = new SupplierOrderService(db);
        var result = await service.Cancel(1);

        Assert.True(result);
        var order = await db.SupplierOrders.FindAsync(1);
        Assert.Equal(SupplierOrderStatus.Cancelled, order!.Status);
    }

    [Fact]
    public async Task Cancel_ReturnsFalseForUnknownId()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        
        var service = new SupplierOrderService(db);
        var result = await service.Cancel(999);

        Assert.False(result);
    }

    [Fact]
    public async Task Cancel_ReturnsFalseForAlreadyCancelledOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Status = SupplierOrderStatus.Cancelled,
            Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } }
        });
        await db.SaveChangesAsync();

        var service = new SupplierOrderService(db);
        var result = await service.Cancel(1);

        Assert.False(result);
    }

    [Fact]
    public async Task Cancel_ReturnsFalseForReceivedOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Costco" });
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Status = SupplierOrderStatus.Received,
            Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10, QuantityReceived = 10 } }
        });
        await db.SaveChangesAsync();

        var service = new SupplierOrderService(db);
        var result = await service.Cancel(1);

        Assert.False(result);
    }
}
