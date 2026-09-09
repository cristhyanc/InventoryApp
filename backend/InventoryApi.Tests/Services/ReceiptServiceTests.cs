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

public class ReceiptServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Upload_Saves_Receipt()
    {
        using var db = CreateDbContext("receipt_test");
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);

        IReceiptService svc = new ReceiptService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(3);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default)).Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var receipt = await svc.Upload(fileMock.Object, "t", null, null, null, null, null, null);
        Assert.NotNull(receipt);
        var stored = await db.Receipts.FindAsync(receipt.Id);
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task Update_Changes_Receipt_MetaData()
    {
        using var db = CreateDbContext("receipt_update_test");
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);

        IReceiptService svc = new ReceiptService(db, envMock.Object);

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
    public async Task Upload_Posts_receipt_items_to_storage_without_machine_refill()
    {
        using var db = CreateDbContext("receipt_items_test");
        db.Products.AddRange(
            new Product { Id = 1, Name = "M&M", QuantityInStock = 2 },
            new Product { Id = 2, Name = "Coke", QuantityInStock = 4 });
        await db.SaveChangesAsync();
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IReceiptService svc = new ReceiptService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var receipt = await svc.Upload(fileMock.Object, "Purchase", null, 65.40m, null, null, null, null,
            new[] { new ReceiptItemDto(1, 24m, 1.00m), new ReceiptItemDto(2, 36m, 1.15m) });

        Assert.NotNull(receipt);
        Assert.Equal(24, (await db.Products.FindAsync(1L))!.QuantityInStock);
        Assert.Equal(36, (await db.Products.FindAsync(2L))!.QuantityInStock);
        Assert.Equal(2, await db.ReceiptItems.CountAsync());
        Assert.Equal(2, await db.StockAdjustments.CountAsync(x => x.Reason == StockAdjustmentReason.Restock));
        Assert.Empty(await db.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
    }

    [Fact]
    public async Task Update_and_delete_receipt_maintain_linked_purchase_movement_without_reversal()
    {
        using var db = CreateDbContext("receipt_edit_test");
        db.Products.Add(new Product { Id = 1, Name = "M&M", QuantityInStock = 0 });
        await db.SaveChangesAsync();
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IReceiptService svc = new ReceiptService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var receipt = await svc.Upload(fileMock.Object, "Purchase", null, 10m, null, null, null, null,
            new[] { new ReceiptItemDto(1, 10m, 1m) });
        Assert.NotNull(receipt);
        Assert.Equal(10, (await db.Products.FindAsync(1L))!.QuantityInStock);
        var originalItemId = (await db.ReceiptItems.SingleAsync()).Id;

        await svc.Update(receipt!.Id, "Purchase", null, 12m, null, null, null, null,
            new[] { new ReceiptItemDto(1, 10m, 2m) });
        Assert.Equal(10, (await db.Products.FindAsync(1L))!.QuantityInStock);
        Assert.Equal(2m, (await db.Products.FindAsync(1L))!.AverageUnitCost);
        var item = await db.ReceiptItems.SingleAsync();
        Assert.Equal(originalItemId, item.Id);
        var movement = await db.StockAdjustments.SingleAsync(x => x.ReceiptItemId.HasValue);
        Assert.Equal(item.Id, movement.ReceiptItemId);
        Assert.Equal(2m, movement.UnitCost);

        Assert.True(await svc.Delete(receipt.Id));
        Assert.Equal(0, (await db.Products.FindAsync(1L))!.QuantityInStock);
        Assert.Empty(await db.StockAdjustments.ToListAsync());
    }

    [Fact]
    public async Task Upload_uses_weighted_average_cost_and_records_cost_ledger()
    {
        using var db = CreateDbContext("receipt_avco_test");
        db.Products.Add(new Product { Id = 1, Name = "M&M", QuantityInStock = 10, AverageUnitCost = 2.10m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 1,
            QuantityChange = 10,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2.10m,
            EffectiveAt = DateTime.UtcNow.AddMinutes(-1),
            Notes = "Initial stock on product creation"
        });
        await db.SaveChangesAsync();
        var envMock = new Mock<IWebHostEnvironment>();
        var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(temp);
        envMock.Setup(e => e.WebRootPath).Returns(temp);
        envMock.Setup(e => e.ContentRootPath).Returns(temp);
        IReceiptService svc = new ReceiptService(db, envMock.Object);

        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        await svc.Upload(fileMock.Object, "Purchase", null, null, null, null, null, null,
            new[] { new ReceiptItemDto(1, 24m, 1.00m) });

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
        IReceiptService svc = new ReceiptService(db, envMock.Object, rebuild.Object);
        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream stream, System.Threading.CancellationToken ct) => content.CopyToAsync(stream, ct));

        var receipt = await svc.Upload(
            fileMock.Object,
            "Purchase",
            null,
            totalAmount: 27m,
            deliveryCost: 5m,
            packageCost: 2m,
            purchaseDate: null,
            supplierId: null,
            items: new[] { new ReceiptItemDto(1, 10m, 2m) });

        Assert.NotNull(receipt);
        Assert.Null(receipt!.Notes);
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
        IReceiptService svc = new ReceiptService(db, envMock.Object);
        var content = new MemoryStream(new byte[] { 1 });
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1);
        fileMock.Setup(f => f.FileName).Returns("t.jpg");
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
        fileMock.Setup(f => f.OpenReadStream()).Returns(content);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), default))
            .Returns((Stream s, System.Threading.CancellationToken ct) => content.CopyToAsync(s, ct));

        var receipt = await svc.Upload(
            fileMock.Object,
            "Purchase",
            null,
            20m,
            null,
            null,
            cutoff.AddMinutes(30),
            null,
            new[] { new ReceiptItemDto(1, 10m, 2m) });

        Assert.NotNull(receipt);
        var product = await db.Products.SingleAsync();
        Assert.Equal(29, product.QuantityInStock);
        Assert.Equal(40, product.CostingQuantity);
        Assert.Equal(57.50m, product.InventoryValue);
        Assert.Equal(1.4375m, product.AverageUnitCost);
        Assert.Null((await db.StockAdjustments.OrderBy(x => x.EffectiveAt).FirstAsync()).UnitCost);
    }
}
