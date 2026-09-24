using InventoryApi.Controllers;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

/// <summary>
/// The tenant boundary around uploaded business documents (issue #64).
///
/// <see cref="Adapters.Persistence.BusinessDataIsolationTests"/> proves the database rows of one business are
/// invisible to another. A document is the harder case, because the bytes live outside the
/// database: the row could be hidden while the file stayed reachable. These tests therefore go
/// through the real retrieval paths - <see cref="PurchaseService.GetFile"/> and
/// <see cref="OperatingExpensesController.GetAttachment"/> - and grant the attacker everything
/// short of a valid membership: the parent record's id, the server-generated stored file name,
/// and the exact path of the file on disk.
///
/// Each test asserts the file is still physically present after the refusal. Without that, a
/// test would pass just as happily against a bug that deleted the document or never wrote it,
/// which would prove nothing about isolation.
///
/// Business A is 1 and business B is 2, matching the other tenancy tests.
/// </summary>
public sealed class ProtectedDocumentTenantIsolationTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public ProtectedDocumentTenantIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
        if (Directory.Exists(_webRoot)) Directory.Delete(_webRoot, recursive: true);
    }

    #region Purchase documents

    /// <summary>
    /// The owner must actually be able to read its own document, otherwise every refusal below
    /// could be explained by the document never having been stored at all.
    /// </summary>
    [Fact]
    public async Task A_business_can_download_its_own_purchase_document()
    {
        var (purchaseId, storedFileName) = await UploadPurchaseDocumentAsync(BusinessB);

        await using var db = TestAppDbContext.For(_options, BusinessB);
        var (content, contentType, fileName) = await new PurchaseService(db, Environment()).GetFile(purchaseId);

        Assert.Equal(new byte[] { 1, 2, 3 }, content);
        Assert.Equal("image/jpeg", contentType);
        Assert.Equal("receipt.jpg", fileName);
        Assert.True(File.Exists(PurchaseDocumentPath(storedFileName)));
    }

    /// <summary>
    /// Knowing the purchase id is the whole attack: the request looks ordinary and the id is
    /// small and guessable. The parent purchase is tenant-filtered, so it resolves to nothing and
    /// the file is never opened.
    /// </summary>
    [Fact]
    public async Task Business_A_cannot_download_business_B_purchase_document_by_its_id()
    {
        var (purchaseId, storedFileName) = await UploadPurchaseDocumentAsync(BusinessB);
        var path = PurchaseDocumentPath(storedFileName);

        await using var db = TestAppDbContext.For(_options, BusinessA);
        var (content, contentType, fileName) = await new PurchaseService(db, Environment()).GetFile(purchaseId);

        Assert.Null(content);
        Assert.Null(contentType);
        Assert.Null(fileName);
        Assert.True(File.Exists(path), "business B's document must still exist; it was refused, not consumed.");
    }

    /// <summary>
    /// Deletion is the destructive half of the same lookup. A refused read that still deleted the
    /// other business's file would be a worse bug than the leak it prevented.
    /// </summary>
    [Fact]
    public async Task Business_A_cannot_delete_business_B_purchase_document()
    {
        var (purchaseId, storedFileName) = await UploadPurchaseDocumentAsync(BusinessB);
        var path = PurchaseDocumentPath(storedFileName);

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.False(await new PurchaseService(db, Environment()).Delete(purchaseId));
        }

        Assert.True(File.Exists(path), "business B's document must survive another business's delete.");

        await using var unrestricted = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(BusinessB, (await unrestricted.Receipts.SingleAsync(r => r.Id == purchaseId)).BusinessId);
    }

    /// <summary>
    /// The stored file name is the last thing an attacker might hope to use directly: it names a
    /// real file on a path this process can read. It is not an entry point - nothing serves bytes
    /// without first resolving the tenant-owned parent row - and no API accepts a stored file name
    /// as input, so a caller cannot point its own purchase at another business's file either.
    /// </summary>
    [Fact]
    public async Task Knowing_the_stored_file_name_and_path_does_not_expose_another_business_document()
    {
        var (_, storedFileName) = await UploadPurchaseDocumentAsync(BusinessB);
        var path = PurchaseDocumentPath(storedFileName);

        Assert.True(File.Exists(path), "the document exists on disk; only the tenant boundary protects it.");

        await using var db = TestAppDbContext.For(_options, BusinessA);

        // Business A can see no purchase at all, so it has no id to ask about and the stored
        // name it somehow learned refers to a row that does not exist for it.
        Assert.Empty(await db.Receipts.ToListAsync());
        Assert.Null(await db.Receipts.FirstOrDefaultAsync(r => r.StoredFileName == storedFileName));
    }

    #endregion

    #region Operating-expense attachments

    [Fact]
    public async Task A_business_can_download_its_own_expense_attachment()
    {
        var (expenseId, storedFileName) = await SeedExpenseAttachmentAsync(BusinessB);

        await using var db = TestAppDbContext.For(_options, BusinessB);
        var result = await ExpensesController(db).GetAttachment(expenseId, CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(ExpenseAttachmentPath(storedFileName), file.FileName);
        Assert.Equal("application/pdf", file.ContentType);
    }

    [Fact]
    public async Task Business_A_cannot_download_business_B_expense_attachment_by_its_id()
    {
        var (expenseId, storedFileName) = await SeedExpenseAttachmentAsync(BusinessB);
        var path = ExpenseAttachmentPath(storedFileName);

        await using var db = TestAppDbContext.For(_options, BusinessA);
        var result = await ExpensesController(db).GetAttachment(expenseId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.True(File.Exists(path), "business B's attachment must still exist after the refusal.");
    }

    /// <summary>
    /// The delete endpoint resolves the expense with <c>FindAsync</c>, which is worth pinning
    /// down separately: a key lookup is the one read that could plausibly skip the query filter.
    /// </summary>
    [Fact]
    public async Task Business_A_cannot_delete_business_B_expense_attachment_by_its_id()
    {
        var (expenseId, storedFileName) = await SeedExpenseAttachmentAsync(BusinessB);
        var path = ExpenseAttachmentPath(storedFileName);

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.IsType<NotFoundResult>(await ExpensesController(db).Delete(expenseId, CancellationToken.None));
        }

        Assert.True(File.Exists(path), "business B's attachment must survive another business's delete.");

        await using var unrestricted = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(BusinessB, (await unrestricted.OperatingExpenses.SingleAsync(e => e.Id == expenseId)).BusinessId);
    }

    #endregion

    #region Helpers

    private TestWebHostEnvironment Environment() => new(_contentRoot, _webRoot);

    private OperatingExpensesController ExpensesController(AppDbContext db) => new(db, Environment());

    private string PurchaseDocumentPath(string storedFileName) => Path.Combine(
        _contentRoot, ProtectedFileStorage.RootFolderName, ProtectedFileStorage.PurchaseDocumentsCategory, storedFileName);

    private string ExpenseAttachmentPath(string storedFileName) => Path.Combine(
        _contentRoot, ProtectedFileStorage.RootFolderName, ProtectedFileStorage.ExpenseAttachmentsCategory, storedFileName);

    /// <summary>
    /// Uploads through the real scoped service, so the stored document is owned exactly the way a
    /// genuine request would own it rather than by a hand-written BusinessId.
    /// </summary>
    private async Task<(int PurchaseId, string StoredFileName)> UploadPurchaseDocumentAsync(int businessId)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        var purchase = await new PurchaseService(db, Environment())
            .Upload(CreateFile("receipt.jpg"), "Purchase", null, null, null, null, null, null);

        Assert.NotNull(purchase);
        Assert.Equal(businessId, purchase!.BusinessId);
        return (purchase.Id, purchase.StoredFileName!);
    }

    private async Task<(int ExpenseId, string StoredFileName)> SeedExpenseAttachmentAsync(int businessId)
    {
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        var folder = Path.Combine(
            _contentRoot, ProtectedFileStorage.RootFolderName, ProtectedFileStorage.ExpenseAttachmentsCategory);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, storedFileName), new byte[] { 4, 5, 6 });

        await using var db = TestAppDbContext.For(_options, businessId);
        var expense = new OperatingExpense
        {
            Description = "Site power",
            ExpenseDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            AttachmentFileName = "invoice.pdf",
            AttachmentStoredFileName = storedFileName,
            AttachmentContentType = "application/pdf",
            AttachmentFileSizeBytes = 3,
        };
        db.OperatingExpenses.Add(expense);
        await db.SaveChangesAsync();

        Assert.Equal(businessId, expense.BusinessId);
        return (expense.Id, storedFileName);
    }

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

    #endregion
}
