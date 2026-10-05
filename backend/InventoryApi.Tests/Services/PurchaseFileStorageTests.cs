using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Infrastructure.Documents;
using InventoryApi.Adapters.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

/// <summary>
/// Proves where purchase documents live on disk. They must stay outside the static web
/// root: static-file middleware does not run controller authorization, so a document under
/// <c>wwwroot</c> would be downloadable by anyone who knows its generated file name,
/// defeating the <c>[Authorize]</c> boundary on <c>PurchasesController</c>.
/// </summary>
public sealed class PurchaseFileStorageTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [Fact]
    public async Task Uploaded_document_is_stored_outside_the_static_web_root()
    {
        using var db = CreateDbContext();
        var useCases = CreateUseCases(db);

        var purchase = await Upload(useCases, CreateFile("receipt.jpg"));

        Assert.NotNull(purchase);
        var storedFileName = purchase!.StoredFileName;
        Assert.True(File.Exists(Path.Combine(_contentRoot, "protected-files", "receipts", storedFileName)));
        Assert.False(File.Exists(Path.Combine(_webRoot, "receipts", storedFileName)));
        Assert.False(Directory.Exists(Path.Combine(_webRoot, "receipts")));
    }

    [Fact]
    public async Task Document_stored_before_protected_storage_is_still_served_and_deleted()
    {
        using var db = CreateDbContext();
        var useCases = CreateUseCases(db);
        var storedFileName = $"{Guid.NewGuid()}.jpg";
        var legacyFolder = Path.Combine(_webRoot, "receipts");
        Directory.CreateDirectory(legacyFolder);
        var legacyPath = Path.Combine(legacyFolder, storedFileName);
        await File.WriteAllBytesAsync(legacyPath, new byte[] { 1, 2, 3 });
        db.Receipts.Add(new Purchase
        {
            Title = "Legacy purchase",
            FileName = "legacy.jpg",
            StoredFileName = storedFileName,
            ContentType = "image/jpeg",
            FileSizeBytes = 3,
            PurchaseDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var purchaseId = (await db.Receipts.AsNoTracking().SingleAsync()).Id;

        var file = await useCases.GetFile.Handle(purchaseId, CancellationToken.None);

        Assert.NotNull(file);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.Content);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("legacy.jpg", file.FileName);

        Assert.True(await useCases.Delete.Handle(purchaseId, CancellationToken.None));
        Assert.False(File.Exists(legacyPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
        if (Directory.Exists(_webRoot)) Directory.Delete(_webRoot, recursive: true);
    }

    private static AppDbContext CreateDbContext() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private PurchaseDocumentUseCases CreateUseCases(AppDbContext db)
    {
        IDocumentStorage documents = new FileSystemDocumentStorage(new FileSystemDocumentStorageOptions
        {
            ContentRootPath = _contentRoot,
            WebRootPath = _webRoot,
        });
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db));
        return new PurchaseDocumentUseCases(
            new UploadPurchase(store, documents, new FakeClock(DateTime.UtcNow)),
            new GetPurchaseFile(store, documents),
            new DeletePurchase(store, documents));
    }

    /// <summary>
    /// Uploads exactly as <c>PurchasesController.Upload</c> does, adapting the posted
    /// <see cref="IFormFile"/> to the Application layer's <see cref="PurchaseFileInput"/> port.
    /// </summary>
    private static Task<PurchaseRecord?> Upload(PurchaseDocumentUseCases useCases, IFormFile file) =>
        useCases.Upload.Handle(
            new PurchaseFileInput(file.FileName, file.ContentType, file.Length, file.OpenReadStream),
            new PurchaseFields("Purchase", null, null, null, null, null, null),
            [],
            CancellationToken.None);

    /// <summary>
    /// The three purchase-document use cases these tests exercise, bundled only so they can be
    /// built in one step. It holds no behaviour of its own.
    /// </summary>
    private sealed record PurchaseDocumentUseCases(
        UploadPurchase Upload,
        GetPurchaseFile GetFile,
        DeletePurchase Delete);

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
}
