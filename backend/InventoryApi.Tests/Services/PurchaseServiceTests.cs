using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class PurchaseServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Upload_ReceivesMatchingSupplierOrderLine()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, supplierId: 1, productId: 1, quantity: 24);
        await db.SaveChangesAsync();

        await UploadPurchase(CreateService(db), supplierId: 1, productId: 1, quantity: 24);

        var line = await db.SupplierOrderLines.SingleAsync();
        Assert.Equal(24m, line.QuantityReceived);
        Assert.Equal(SupplierOrderStatus.Received, (await db.SupplierOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Upload_PartialPurchase_LeavesOutstandingQuantity()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, supplierId: 1, productId: 1, quantity: 24);
        await db.SaveChangesAsync();

        await UploadPurchase(CreateService(db), supplierId: 1, productId: 1, quantity: 18);

        var line = await db.SupplierOrderLines.SingleAsync();
        Assert.Equal(18m, line.QuantityReceived);
        Assert.Equal(6m, line.QuantityOrdered - line.QuantityReceived);
        Assert.Equal(SupplierOrderStatus.PartiallyReceived, (await db.SupplierOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Upload_SecondPurchase_CompletesOutstandingOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, supplierId: 1, productId: 1, quantity: 24);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        await UploadPurchase(service, 1, 1, 18);
        await UploadPurchase(service, 1, 1, 6);

        var line = await db.SupplierOrderLines.SingleAsync();
        Assert.Equal(24m, line.QuantityReceived);
        Assert.Equal(SupplierOrderStatus.Received, (await db.SupplierOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Upload_ExcessPurchase_ClosesOrderAndKeepsPurchaseStockMovement()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, supplierId: 1, productId: 1, quantity: 6);
        await db.SaveChangesAsync();

        await UploadPurchase(CreateService(db), 1, 1, 10);

        Assert.Equal(6m, (await db.SupplierOrderLines.SingleAsync()).QuantityReceived);
        Assert.Equal(6m, (await db.SupplierOrderReceiptAllocations.SingleAsync()).QuantityApplied);
        Assert.Equal(10, (await db.Products.FindAsync(1L))!.QuantityInStock);
        Assert.Single(await db.StockAdjustments.Where(item => item.Reason == StockAdjustmentReason.Restock).ToListAsync());
    }

    [Fact]
    public async Task Upload_ReceivingOrder_DoesNotCreateDuplicateStockMovements()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, supplierId: 1, productId: 1, quantity: 10);
        await db.SaveChangesAsync();

        await UploadPurchase(CreateService(db), 1, 1, 10);

        Assert.Single(await db.StockAdjustments.Where(item => item.ProductId == 1).ToListAsync());
    }

    [Fact]
    public async Task Upload_MultipleOpenOrders_ConsumesOldestOutstandingFirst()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        AddTransitionBaseline(db, 1, 0);
        SeedSupplier(db, 1);
        db.SupplierOrders.AddRange(
            new SupplierOrder { SupplierId = 1, OrderDate = new DateTime(2026, 1, 1), Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } },
            new SupplierOrder { SupplierId = 1, OrderDate = new DateTime(2026, 1, 2), Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 10 } } });
        await db.SaveChangesAsync();

        await UploadPurchase(CreateService(db), 1, 1, 12);

        var lines = await db.SupplierOrderLines.Include(line => line.SupplierOrder).OrderBy(line => line.SupplierOrder.OrderDate).ToListAsync();
        Assert.Equal(10m, lines[0].QuantityReceived);
        Assert.Equal(2m, lines[1].QuantityReceived);
    }

    [Fact]
    public async Task Upload_DifferentSupplierOrder_IsNotMatched()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, supplierId: 2, productId: 1, quantity: 10);
        SeedSupplier(db, 1);
        await db.SaveChangesAsync();

        await UploadPurchase(CreateService(db), 1, 1, 10);

        Assert.Equal(0m, (await db.SupplierOrderLines.SingleAsync()).QuantityReceived);
    }

    [Fact]
    public async Task Delete_ReopensSupplierOrderAndRemovesFulfillmentAllocation()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, 1, 1, 10);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        await UploadPurchase(service, 1, 1, 10);
        var purchaseId = (await db.Receipts.SingleAsync()).Id;

        Assert.True(await service.Delete(purchaseId));

        Assert.Equal(0m, (await db.SupplierOrderLines.SingleAsync()).QuantityReceived);
        Assert.Equal(SupplierOrderStatus.Ordered, (await db.SupplierOrders.SingleAsync()).Status);
        Assert.Empty(await db.SupplierOrderReceiptAllocations.ToListAsync());
    }

    [Fact]
    public async Task Update_ReducingPurchaseQuantity_ReconcilesFulfillment()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, 1, 1, 24);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        await UploadPurchase(service, 1, 1, 24);
        var purchase = await db.Receipts.SingleAsync();

        await service.Update(purchase.Id, null, null, null, null, null, null, 1,
            new[] { new PurchaseItemDto(1, 18m, 1m) });

        Assert.Equal(18m, (await db.SupplierOrderLines.SingleAsync()).QuantityReceived);
        Assert.Equal(SupplierOrderStatus.PartiallyReceived, (await db.SupplierOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Update_IncreasingPurchaseQuantity_ConsumesAdditionalOutstandingQuantity()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, 1, 1, 24);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        await UploadPurchase(service, 1, 1, 18);
        var purchase = await db.Receipts.SingleAsync();

        await service.Update(purchase.Id, null, null, null, null, null, null, 1,
            new[] { new PurchaseItemDto(1, 24m, 1m) });

        Assert.Equal(24m, (await db.SupplierOrderLines.SingleAsync()).QuantityReceived);
        Assert.Equal(SupplierOrderStatus.Received, (await db.SupplierOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Update_ChangingSupplier_RemovesOldSupplierFulfillment()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, 1, 1, 10);
        SeedSupplier(db, 2);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        await UploadPurchase(service, 1, 1, 10);
        var purchase = await db.Receipts.SingleAsync();

        await service.Update(purchase.Id, null, null, null, null, null, null, 2,
            new[] { new PurchaseItemDto(1, 10m, 1m) });

        Assert.Equal(0m, (await db.SupplierOrderLines.SingleAsync()).QuantityReceived);
        Assert.Empty(await db.SupplierOrderReceiptAllocations.ToListAsync());
    }

    [Fact]
    public async Task Update_ChangingProducts_RematchesFulfillment()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, 1, 1, 10);
        db.Products.Add(new Product { Id = 2, Name = "Water" });
        AddTransitionBaseline(db, 2, 0);
        db.SupplierOrders.Add(new SupplierOrder { SupplierId = 1, Lines = { new SupplierOrderLine { ProductId = 2, QuantityOrdered = 10 } } });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        await UploadPurchase(service, 1, 1, 10);
        var purchase = await db.Receipts.SingleAsync();

        await service.Update(purchase.Id, null, null, null, null, null, null, 1,
            new[] { new PurchaseItemDto(2, 10m, 1m) });

        var lines = await db.SupplierOrderLines.OrderBy(line => line.ProductId).ToListAsync();
        Assert.Equal(0m, lines[0].QuantityReceived);
        Assert.Equal(10m, lines[1].QuantityReceived);
    }

    [Fact]
    public async Task Upload_HistoricalPurchase_DoesNotFulfillFutureOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, 1, 1, 10);
        var order = db.SupplierOrders.Local.Single();
        order.OrderDate = new DateTime(2026, 9, 13);
        db.InventoryCostTransitionBaselines.Local.Single().CutoffAt = new DateTime(2026, 9, 1);
        await db.SaveChangesAsync();

        await UploadPurchase(CreateService(db), 1, 1, 10, new DateTime(2026, 9, 5));

        Assert.Equal(0m, (await db.SupplierOrderLines.SingleAsync()).QuantityReceived);
        Assert.Equal(SupplierOrderStatus.Ordered, (await db.SupplierOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Upload_DuplicateProductLines_CapsFulfillmentAtOrderQuantity()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, 1, 1, 10);
        await db.SaveChangesAsync();

        await UploadPurchaseItems(CreateService(db), 1,
            new PurchaseItemDto(1, 8m, 1m),
            new PurchaseItemDto(1, 8m, 1m));

        var line = await db.SupplierOrderLines.SingleAsync();
        Assert.Equal(10m, line.QuantityReceived);
        Assert.Equal(10m, await db.SupplierOrderReceiptAllocations.SumAsync(allocation => allocation.QuantityApplied));
        Assert.Equal(16, (await db.Products.FindAsync(1L))!.QuantityInStock);
        Assert.Equal(2, await db.StockAdjustments.CountAsync(movement => movement.Reason == StockAdjustmentReason.Restock));
    }

    [Fact]
    public async Task Upload_SameCalendarDateWithDifferentTimes_FulfillsOrder()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        SeedOrder(db, 1, 1, 10);
        db.SupplierOrders.Local.Single().OrderDate = new DateTime(2026, 9, 13, 23, 0, 0);
        db.InventoryCostTransitionBaselines.Local.Single().CutoffAt = new DateTime(2026, 9, 1);
        await db.SaveChangesAsync();

        await UploadPurchase(CreateService(db), 1, 1, 10, new DateTime(2026, 9, 13, 0, 1, 0));

        Assert.Equal(10m, (await db.SupplierOrderLines.SingleAsync()).QuantityReceived);
        Assert.Equal(SupplierOrderStatus.Received, (await db.SupplierOrders.SingleAsync()).Status);
    }

    private static void SeedOrder(AppDbContext db, int supplierId, long productId, decimal quantity)
    {
        db.Products.Add(new Product { Id = productId, Name = "Coke" });
        AddTransitionBaseline(db, productId, 0);
        SeedSupplier(db, supplierId);
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = supplierId,
            Lines = { new SupplierOrderLine { ProductId = productId, QuantityOrdered = quantity } }
        });
    }

    private static void SeedSupplier(AppDbContext db, int supplierId)
    {
        if (db.Suppliers.Local.All(supplier => supplier.Id != supplierId))
            db.Suppliers.Add(new Supplier { Id = supplierId, Name = $"Supplier {supplierId}" });
    }

    private static IPurchaseService CreateService(AppDbContext db)
    {
        var environment = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        environment.Setup(item => item.WebRootPath).Returns(temp);
        environment.Setup(item => item.ContentRootPath).Returns(temp);
        return new PurchaseService(db, environment.Object);
    }

    private static async Task UploadPurchase(IPurchaseService service, int supplierId, long productId, decimal quantity, DateTime? purchaseDate = null)
    {
        await UploadPurchaseItems(service, supplierId, new[] { new PurchaseItemDto(productId, quantity, 1m) }, purchaseDate);
    }

    private static async Task UploadPurchaseItems(IPurchaseService service, int supplierId, params PurchaseItemDto[] items)
    {
        await UploadPurchaseItems(service, supplierId, items, null);
    }

    private static async Task UploadPurchaseItems(IPurchaseService service, int supplierId, PurchaseItemDto[] items, DateTime? purchaseDate)
    {
        var content = new MemoryStream(new byte[] { 1 });
        var file = new Mock<IFormFile>();
        file.Setup(item => item.Length).Returns(1);
        file.Setup(item => item.FileName).Returns("purchase.jpg");
        file.Setup(item => item.ContentType).Returns("image/jpeg");
        file.Setup(item => item.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream stream, System.Threading.CancellationToken token) => content.CopyToAsync(stream, token));
        await service.Upload(file.Object, "Purchase", null, null, null, null, purchaseDate, supplierId,
            items);
    }

    [Fact]
    public async Task Upload_Saves_Purchase()
    {
        using var db = CreateDbContext("purchase_test");
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);

        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(3);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default)).Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var purchase = await svc.Upload(fileMock.Object, "t", null, null, null, null, null, null);
        Assert.NotNull(purchase);
        var stored = await db.Receipts.FindAsync(purchase.Id);
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task Update_Changes_Purchase_MetaData()
    {
        using var db = CreateDbContext("purchase_update_test");
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);

        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(3);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default)).Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var created = await svc.Upload(fileMock.Object, "Original", "Notes", 10m, 1.5m, 2.5m, null, null);
        Assert.NotNull(created);

        var updated = await svc.Update(created!.Id, "Updated", "New notes", 12m, 3m, 4m, new DateTime(2024, 1, 1), null);
        Assert.NotNull(updated);
        Assert.Equal("Updated", updated.Title);
        Assert.Equal(3m, updated.DeliveryCost);
        Assert.Equal(4m, updated.PackageCost);
        Assert.Equal(new DateTime(2024, 1, 1), updated.PurchaseDate);
    }

    [Fact]
    public async Task Upload_Posts_purchase_items_to_storage_without_machine_refill()
    {
        using var db = CreateDbContext("purchase_items_test");
        db.Products.AddRange(
            new Product { Id = 1, Name = "M&M", QuantityInStock = 2 },
            new Product { Id = 2, Name = "Coke", QuantityInStock = 4 });
        AddTransitionBaseline(db, 1, 2);
        AddTransitionBaseline(db, 2, 4);
        await db.SaveChangesAsync();
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var purchase = await svc.Upload(fileMock.Object, "Purchase", null, 65.40m, null, null, null, null,
            new[] { new PurchaseItemDto(1, 24m, 1.00m), new PurchaseItemDto(2, 36m, 1.15m) });

        Assert.NotNull(purchase);
        Assert.Equal(26, (await db.Products.FindAsync(1L))!.QuantityInStock);
        Assert.Equal(40, (await db.Products.FindAsync(2L))!.QuantityInStock);
        Assert.Equal(2, await db.ReceiptItems.CountAsync());
        Assert.Equal(2, await db.StockAdjustments.CountAsync(x => x.Reason == StockAdjustmentReason.Restock));
        Assert.Empty(await db.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
    }

    [Fact]
    public async Task Update_and_delete_purchase_maintain_linked_movement_without_reversal()
    {
        using var db = CreateDbContext("purchase_edit_test");
        db.Products.Add(new Product { Id = 1, Name = "M&M", QuantityInStock = 0 });
        AddTransitionBaseline(db, 1, 0);
        await db.SaveChangesAsync();
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var purchase = await svc.Upload(fileMock.Object, "Purchase", null, 10m, null, null, null, null,
            new[] { new PurchaseItemDto(1, 10m, 1m) });
        Assert.NotNull(purchase);
        Assert.Equal(10, (await db.Products.FindAsync(1L))!.QuantityInStock);
        var originalItemId = (await db.ReceiptItems.SingleAsync()).Id;

        await svc.Update(purchase!.Id, "Purchase", null, 12m, null, null, null, null,
            new[] { new PurchaseItemDto(1, 10m, 2m) });
        Assert.Equal(10, (await db.Products.FindAsync(1L))!.QuantityInStock);
        Assert.Equal(2m, (await db.Products.FindAsync(1L))!.AverageUnitCost);
        var item = await db.ReceiptItems.SingleAsync();
        Assert.Equal(originalItemId, item.Id);
        var movement = await db.StockAdjustments.SingleAsync(x => x.ReceiptItemId.HasValue);
        Assert.Equal(item.Id, movement.ReceiptItemId);
        Assert.Equal(2m, movement.UnitCost);

        Assert.True(await svc.Delete(purchase.Id));
        Assert.Equal(0, (await db.Products.FindAsync(1L))!.QuantityInStock);
        Assert.Empty(await db.StockAdjustments.ToListAsync());
    }

    [Fact]
    public async Task Upload_uses_weighted_average_cost_and_records_cost_ledger()
    {
        using var db = CreateDbContext("purchase_avco_test");
        db.Products.Add(new Product { Id = 1, Name = "M&M", QuantityInStock = 10, AverageUnitCost = 2.10m });
        AddTransitionBaseline(db, 1, 10, 10, 21m, 2.10m);
        await db.SaveChangesAsync();
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        await svc.Upload(fileMock.Object, "Purchase", null, null, null, null, null, null,
            new[] { new PurchaseItemDto(1, 24m, 1.00m) });

        var product = await db.Products.FindAsync(1L);
        Assert.Equal(34, product!.QuantityInStock);
        Assert.Equal((10m * 2.10m + 24m) / 34m, product.AverageUnitCost);
        var movement = await db.StockAdjustments.SingleAsync(x => x.ReceiptItemId.HasValue);
        Assert.Equal(1.00m, movement.UnitCost);
        Assert.Equal(24.00m, movement.TotalCost);
    }

    [Fact]
    public async Task Upload_total_validation_includes_delivery_and_package_fees()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 1, Name = "M&M" });
        await db.SaveChangesAsync();
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        var rebuild = new Mock<IInventoryCostRebuildService>();
        rebuild.Setup(x => x.RebuildAsync(
                It.IsAny<long>(),
                It.IsAny<DateTime?>(),
                It.IsAny<bool>(),
                It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((long productId, DateTime? _, bool dryRun, System.Threading.CancellationToken _) =>
                new InventoryCostRebuildResult { ProductId = productId, DryRun = dryRun });
        IPurchaseService svc = new PurchaseService(db, envMock.Object, rebuild.Object);
        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream stream, System.Threading.CancellationToken ct) => content.CopyToAsync(stream, ct));

        var purchase = await svc.Upload(
            fileMock.Object,
            "Purchase",
            null,
            totalAmount: 27m,
            deliveryCost: 5m,
            packageCost: 2m,
            purchaseDate: null,
            supplierId: null,
            items: new[] { new PurchaseItemDto(1, 10m, 2m) });

        Assert.NotNull(purchase);
        Assert.Null(purchase!.Notes);
    }

    [Fact]
    public async Task Upload_after_transition_ignores_invalid_legacy_history()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var cutoff = DateTime.UtcNow.AddHours(-1);
        db.Products.Add(new Product { Id = 1, Name = "M&M", QuantityInStock = 19, AverageUnitCost = 1.25m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 1,
            QuantityChange = 31,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = null,
            EffectiveAt = cutoff.AddDays(-30)
        });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = 1,
            CutoffAt = cutoff,
            HomeStockQuantity = 19,
            MachineStockQuantity = 11,
            OpeningCostingQuantity = 30,
            AverageUnitCost = 1.25m,
            InventoryValue = 37.50m,
            CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            LegacyReplayedPhysicalQuantity = 31,
            LegacyPhysicalDiscrepancy = -12,
            DataQualityNote = "Legacy discrepancy retired at cutover."
        });
        await db.SaveChangesAsync();
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IPurchaseService svc = new PurchaseService(db, envMock.Object);
        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var purchase = await svc.Upload(
            fileMock.Object,
            "Purchase",
            null,
            20m,
            null,
            null,
            cutoff.AddMinutes(30),
            null,
            new[] { new PurchaseItemDto(1, 10m, 2m) });

        Assert.NotNull(purchase);
        var product = await db.Products.SingleAsync();
        Assert.Equal(29, product.QuantityInStock);
        Assert.Equal(40, product.CostingQuantity);
        Assert.Equal(57.50m, product.InventoryValue);
        Assert.Equal(1.4375m, product.AverageUnitCost);
        Assert.Null((await db.StockAdjustments.OrderBy(x => x.EffectiveAt).FirstAsync()).UnitCost);
    }

    [Fact]
    public async Task Upload_preserves_user_notes_when_total_mismatches()
    {
        // Regression test: Purchase.Notes should never be mutated by validation warning
        using var db = CreateDbContext("purchase_notes_test");
        db.Products.Add(new Product { Id = 1, Name = "M&M", QuantityInStock = 0 });
        AddTransitionBaseline(db, 1, 0);
        await db.SaveChangesAsync();

        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        const string userNotes = "Purchased during Costco promotion";
        var purchase = await svc.Upload(fileMock.Object, "Purchase", userNotes,
            30m, // totalAmount - intentionally high (items=20, no delivery/package, so calculated=20)
            null, null, null, null,
            new[] { new PurchaseItemDto(1, 20m, 1m) });

        Assert.NotNull(purchase);
        // Notes must not contain warning text
        Assert.Equal(userNotes, purchase.Notes);
        Assert.DoesNotContain("Warning", purchase.Notes ?? "");
        Assert.DoesNotContain("calculated total", purchase.Notes ?? "");
    }

    [Fact]
    public async Task ComputeValidation_detects_total_mismatch()
    {
        // Items = 20, no delivery/package, but entered total = 30
        // Calculated total = 20, difference = 10 (exceeds 0.02 tolerance)
        using var db = CreateDbContext("purchase_validation_mismatch_test");
        db.Products.Add(new Product { Id = 1, Name = "M&M", QuantityInStock = 0 });
        AddTransitionBaseline(db, 1, 0);
        await db.SaveChangesAsync();

        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var purchase = await svc.Upload(fileMock.Object, "Purchase", null,
            30m, null, null, null, null,
            new[] { new PurchaseItemDto(1, 20m, 1m) });

        Assert.NotNull(purchase);
        var validation = svc.ComputeValidation(purchase);
        
        Assert.NotNull(validation);
        Assert.True(validation.HasTotalMismatch);
        Assert.Equal(20m, validation.CalculatedItemSubtotal);
        Assert.Equal(20m, validation.CalculatedTotal);
        Assert.Equal(10m, validation.TotalDifference);
    }

    [Fact]
    public async Task ComputeValidation_no_mismatch_when_totals_match()
    {
        // Items = 20, delivery = 5, package = 2, entered total = 27
        // Calculated total = 27, no mismatch
        using var db = CreateDbContext("purchase_validation_match_test");
        db.Products.Add(new Product { Id = 1, Name = "M&M", QuantityInStock = 0 });
        AddTransitionBaseline(db, 1, 0);
        await db.SaveChangesAsync();

        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var purchase = await svc.Upload(fileMock.Object, "Purchase", null,
            27m, 5m, 2m, null, null,
            new[] { new PurchaseItemDto(1, 20m, 1m) });

        Assert.NotNull(purchase);
        var validation = svc.ComputeValidation(purchase);
        
        Assert.NotNull(validation);
        Assert.False(validation.HasTotalMismatch);
        Assert.Equal(20m, validation.CalculatedItemSubtotal);
        Assert.Equal(27m, validation.CalculatedTotal);
        Assert.Null(validation.TotalDifference);
    }

    [Fact]
    public async Task Update_preserves_user_notes_and_computes_validation()
    {
        // Ensure update also preserves notes and provides new validation
        using var db = CreateDbContext("purchase_update_notes_test");
        db.Products.Add(new Product { Id = 1, Name = "M&M", QuantityInStock = 0 });
        AddTransitionBaseline(db, 1, 0);
        await db.SaveChangesAsync();

        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        const string userNotes = "Supplier note: fragile items";
        var purchase = await svc.Upload(fileMock.Object, "Purchase", userNotes,
            20m, null, null, null, null,
            new[] { new PurchaseItemDto(1, 20m, 1m) });
        Assert.NotNull(purchase);

        // Update with mismatched total
        var updated = await svc.Update(purchase.Id, null, userNotes, 25m, null, null, null, null,
            new[] { new PurchaseItemDto(1, 20m, 1m) });

        Assert.NotNull(updated);
        Assert.Equal(userNotes, updated.Notes);
        var validation = svc.ComputeValidation(updated);
        Assert.True(validation!.HasTotalMismatch);
    }

    private static void AddTransitionBaseline(AppDbContext db, long productId, int homeStockQuantity,
        int openingCostingQuantity = 0, decimal inventoryValue = 0m, decimal averageUnitCost = 0m) =>
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = productId,
            CutoffAt = DateTime.UtcNow.AddDays(-1),
            HomeStockQuantity = homeStockQuantity,
            OpeningCostingQuantity = openingCostingQuantity,
            InventoryValue = inventoryValue,
            AverageUnitCost = averageUnitCost,
            CostSource = InventoryCostBaselineSource.ManualAuthoritative
        });

    [Fact]
    public async Task ComputeValidation_empty_items_with_delivery_package_detects_mismatch()
    {
        // Empty items, delivery = 5, package = 2, entered total = 20
        // Calculated = 0 + 5 + 2 = 7
        // Difference = 13 (exceeds tolerance)
        using var db = CreateDbContext("purchase_empty_items_mismatch_test");
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var purchase = await svc.Upload(fileMock.Object, "Delivery Only", null,
            20m, 5m, 2m, null, null,
            Array.Empty<PurchaseItemDto>());

        Assert.NotNull(purchase);
        var validation = svc.ComputeValidation(purchase);
        
        Assert.NotNull(validation);
        Assert.True(validation.HasTotalMismatch);
        Assert.Equal(0m, validation.CalculatedItemSubtotal);
        Assert.Equal(7m, validation.CalculatedTotal);
        Assert.Equal(13m, validation.TotalDifference);
    }

    [Fact]
    public async Task ComputeValidation_empty_items_null_total_no_mismatch()
    {
        // Empty items, no delivery/package, null total
        // Should not compute validation
        using var db = CreateDbContext("purchase_empty_items_null_total_test");
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IPurchaseService svc = new PurchaseService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var purchase = await svc.Upload(fileMock.Object, "No Total", null,
            null, null, null, null, null,
            Array.Empty<PurchaseItemDto>());

        Assert.NotNull(purchase);
        var validation = svc.ComputeValidation(purchase);
        
        Assert.NotNull(validation);
        Assert.False(validation.HasTotalMismatch);
        Assert.Null(validation.CalculatedItemSubtotal);
        Assert.Null(validation.CalculatedTotal);
        Assert.Null(validation.TotalDifference);
    }
}
