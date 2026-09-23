using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

/// <summary>
/// Proves where purchase receipt documents live on disk. They must stay outside the static
/// web root: static-file middleware does not run controller authorization, so a document
/// under <c>wwwroot</c> would be downloadable by anyone who knows its generated file name,
/// defeating the <c>[Authorize]</c> boundary on <c>ReceiptsController</c>.
/// </summary>
public sealed class ReceiptFileStorageTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [Fact]
    public async Task Uploaded_document_is_stored_outside_the_static_web_root()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);

        var receipt = await service.Upload(CreateFile("receipt.jpg"), "Purchase", null, null, null, null, null, null);

        Assert.NotNull(receipt);
        var storedFileName = receipt!.StoredFileName;
        Assert.True(File.Exists(Path.Combine(_contentRoot, "protected-files", "receipts", storedFileName)));
        Assert.False(File.Exists(Path.Combine(_webRoot, "receipts", storedFileName)));
        Assert.False(Directory.Exists(Path.Combine(_webRoot, "receipts")));
    }

    [Fact]
    public async Task Document_stored_before_protected_storage_is_still_served_and_deleted()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);
        var storedFileName = $"{Guid.NewGuid()}.jpg";
        var legacyFolder = Path.Combine(_webRoot, "receipts");
        Directory.CreateDirectory(legacyFolder);
        var legacyPath = Path.Combine(legacyFolder, storedFileName);
        await File.WriteAllBytesAsync(legacyPath, new byte[] { 1, 2, 3 });
        db.Receipts.Add(new Receipt
        {
            Title = "Legacy purchase",
            FileName = "legacy.jpg",
            StoredFileName = storedFileName,
            ContentType = "image/jpeg",
            FileSizeBytes = 3,
            PurchaseDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var receiptId = (await db.Receipts.AsNoTracking().SingleAsync()).Id;

        var (content, contentType, fileName) = await service.GetFile(receiptId);

        Assert.Equal(new byte[] { 1, 2, 3 }, content);
        Assert.Equal("image/jpeg", contentType);
        Assert.Equal("legacy.jpg", fileName);

        Assert.True(await service.Delete(receiptId));
        Assert.False(File.Exists(legacyPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
        if (Directory.Exists(_webRoot)) Directory.Delete(_webRoot, recursive: true);
    }

    private static AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private IReceiptService CreateService(AppDbContext db) =>
        new ReceiptService(db, new TestWebHostEnvironment(_contentRoot, _webRoot));

    private static IFormFile CreateFile(string fileName)
    {
        var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var file = new Mock<IFormFile>();
        file.Setup(x => x.Length).Returns(content.Length);
        file.Setup(x => x.FileName).Returns(fileName);
        file.Setup(x => x.ContentType).Returns("image/jpeg");
        file.Setup(x => x.OpenReadStream()).Returns(content);
        file.Setup(x => x.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((Stream target, CancellationToken token) => content.CopyToAsync(target, token));
        return file.Object;
    }

    private sealed class TestWebHostEnvironment(string contentRoot, string webRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "InventoryApi.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = webRoot;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
