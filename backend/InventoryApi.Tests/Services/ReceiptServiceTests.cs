using System;
using System.IO;
using System.Threading.Tasks;
using InventoryApi.Data;
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
}
