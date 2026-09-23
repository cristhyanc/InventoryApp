using InventoryApi.Controllers;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public sealed class OperatingExpensesControllerTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    // Deliberately a different folder from the content root so the tests below can prove a
    // supporting document never lands anywhere static-file middleware could serve it.
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [Fact]
    public async Task Create_without_attachment_leaves_attachment_fields_empty()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db);

        var result = await controller.Create(CreateDto(), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var expense = Assert.IsType<OperatingExpense>(created.Value);
        Assert.Null(expense.AttachmentFileName);
        Assert.Null(expense.AttachmentStoredFileName);
        Assert.Null(expense.AttachmentContentType);
        Assert.Null(expense.AttachmentFileSizeBytes);
    }

    [Theory]
    [InlineData("invoice.pdf", "application/pdf")]
    [InlineData("invoice.png", "image/png")]
    public async Task Create_with_attachment_saves_and_serves_document(string fileName, string contentType)
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db);

        var result = await controller.CreateWithAttachment(
            CreateDto(), CreateFile(fileName), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var expense = Assert.IsType<OperatingExpense>(created.Value);
        Assert.Equal(fileName, expense.AttachmentFileName);
        Assert.Equal(contentType, expense.AttachmentContentType);
        Assert.Equal(3, expense.AttachmentFileSizeBytes);
        Assert.True(File.Exists(AttachmentPath(expense.AttachmentStoredFileName!)));

        var document = await controller.GetAttachment(expense.Id, CancellationToken.None);
        var file = Assert.IsType<PhysicalFileResult>(document);
        Assert.Equal(contentType, file.ContentType);
        Assert.Equal(fileName, file.FileDownloadName);
    }

    [Fact]
    public async Task Create_rejects_an_invalid_attachment_extension()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db);

        var result = await controller.CreateWithAttachment(
            CreateDto(), CreateFile("invoice.exe"), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Supporting document must be an image or PDF.", badRequest.Value);
        Assert.Empty(db.OperatingExpenses);
    }

    [Fact]
    public async Task Replacing_attachment_deletes_the_previous_file_after_saving()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db);
        var created = await CreateExpenseWithAttachment(controller, "old.pdf");
        var oldPath = AttachmentPath(created.AttachmentStoredFileName!);

        var result = await controller.UpdateWithAttachment(
            created.Id, CreateDto(description: "Updated"), CreateFile("new.png"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var updated = await db.OperatingExpenses.SingleAsync();
        Assert.Equal("new.png", updated.AttachmentFileName);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(AttachmentPath(updated.AttachmentStoredFileName!)));
    }

    [Fact]
    public async Task Failed_attachment_replacement_preserves_the_existing_file()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db);
        var created = await CreateExpenseWithAttachment(controller, "old.pdf");
        var oldPath = AttachmentPath(created.AttachmentStoredFileName!);

        var result = await controller.UpdateWithAttachment(
            created.Id, CreateDto(description: "Updated"), CreateFile("new.exe"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.True(File.Exists(oldPath));
        var persisted = await db.OperatingExpenses.AsNoTracking().SingleAsync();
        Assert.Equal("old.pdf", persisted.AttachmentFileName);
    }

    [Fact]
    public async Task Delete_removes_the_attachment_file()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db);
        var created = await CreateExpenseWithAttachment(controller, "invoice.pdf");
        var path = AttachmentPath(created.AttachmentStoredFileName!);

        var result = await controller.Delete(created.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(File.Exists(path));
        Assert.Empty(db.OperatingExpenses);
    }

    [Fact]
    public async Task Attachment_is_stored_outside_the_static_web_root()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db);

        var created = await CreateExpenseWithAttachment(controller, "invoice.pdf");

        var storedFileName = created.AttachmentStoredFileName!;
        Assert.True(File.Exists(AttachmentPath(storedFileName)));
        Assert.False(File.Exists(Path.Combine(_webRoot, "expenses", storedFileName)));
        Assert.False(Directory.Exists(Path.Combine(_webRoot, "expenses")));
    }

    [Fact]
    public async Task Attachment_stored_before_protected_storage_is_still_served()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db);
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        var legacyFolder = Path.Combine(_webRoot, "expenses");
        Directory.CreateDirectory(legacyFolder);
        await File.WriteAllBytesAsync(Path.Combine(legacyFolder, storedFileName), new byte[] { 1, 2, 3 });
        db.OperatingExpenses.Add(new OperatingExpense
        {
            ExpenseDate = DateTime.UtcNow.Date,
            Category = OperatingExpenseCategory.Insurance,
            Description = "Legacy attachment",
            AmountExGst = 10m,
            GstAmount = 1m,
            TotalAmount = 11m,
            AttachmentFileName = "legacy.pdf",
            AttachmentStoredFileName = storedFileName,
            AttachmentContentType = "application/pdf",
            AttachmentFileSizeBytes = 3
        });
        await db.SaveChangesAsync();
        var expenseId = (await db.OperatingExpenses.AsNoTracking().SingleAsync()).Id;

        var document = await controller.GetAttachment(expenseId, CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(document);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("legacy.pdf", file.FileDownloadName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
        if (Directory.Exists(_webRoot)) Directory.Delete(_webRoot, recursive: true);
    }

    private string AttachmentPath(string storedFileName) =>
        Path.Combine(_contentRoot, "protected-files", "expenses", storedFileName);

    private AppDbContext CreateDbContext() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private OperatingExpensesController CreateController(AppDbContext db) =>
        new(db, new TestWebHostEnvironment(_contentRoot, _webRoot));

    private async Task<OperatingExpense> CreateExpenseWithAttachment(
        OperatingExpensesController controller, string fileName)
    {
        var result = await controller.CreateWithAttachment(
            CreateDto(), CreateFile(fileName), CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        return Assert.IsType<OperatingExpense>(created.Value);
    }

    private static OperatingExpenseDto CreateDto(string description = "Monthly insurance") =>
        new(DateTime.UtcNow.Date, OperatingExpenseCategory.Insurance, description, 10m, 1m, 11m);

    private static IFormFile CreateFile(string fileName) =>
        new FormFile(new MemoryStream(new byte[] { 1, 2, 3 }), 0, 3, "attachment", fileName);

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
