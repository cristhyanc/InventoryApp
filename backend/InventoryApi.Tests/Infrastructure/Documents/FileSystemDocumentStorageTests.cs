using System.Text;
using Inventory.Application.Documents;
using Inventory.Infrastructure.Documents;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Documents;

/// <summary>
/// The filesystem document-storage adapter behind <see cref="IDocumentStorage"/> (issue #39,
/// checkpoint 1). These tests pin the storage behaviour the application has always had, because
/// the port is a refactoring boundary rather than a storage migration: new documents must still
/// land outside the static web root, documents uploaded before protected storage existed must
/// still be readable from the web root, and a stored name must never reach outside its category
/// folder.
/// </summary>
public sealed class FileSystemDocumentStorageTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly FileSystemDocumentStorage _storage;

    public FileSystemDocumentStorageTests()
    {
        _storage = new FileSystemDocumentStorage(new FileSystemDocumentStorageOptions
        {
            ContentRootPath = _contentRoot,
            WebRootPath = _webRoot,
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
        if (Directory.Exists(_webRoot)) Directory.Delete(_webRoot, recursive: true);
    }

    [Theory]
    [InlineData(DocumentCategory.PurchaseDocument, "receipts")]
    [InlineData(DocumentCategory.ExpenseAttachment, "expenses")]
    public async Task Saved_document_lands_in_protected_storage_and_never_in_the_web_root(
        DocumentCategory category, string folderName)
    {
        var storedFileName = $"{Guid.NewGuid()}.pdf";

        await _storage.SaveAsync(category, storedFileName, Content("saved"));

        Assert.True(File.Exists(Path.Combine(_contentRoot, "protected-files", folderName, storedFileName)));
        Assert.False(File.Exists(Path.Combine(_webRoot, folderName, storedFileName)));
        Assert.False(Directory.Exists(Path.Combine(_webRoot, folderName)));
    }

    [Fact]
    public async Task Saved_document_is_read_back_through_the_port()
    {
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        await _storage.SaveAsync(DocumentCategory.PurchaseDocument, storedFileName, Content("saved"));

        await using var document = await _storage.OpenReadAsync(DocumentCategory.PurchaseDocument, storedFileName);

        Assert.NotNull(document);
        Assert.Equal(5, document!.ByteLength);
        Assert.Equal("saved", await ReadAllAsync(document));
    }

    /// <summary>
    /// The legacy fallback is the reason documents uploaded before protected storage still work.
    /// Removing it would silently orphan every one of them, so it stays until they have been
    /// migrated and the fallback is deliberately retired.
    /// </summary>
    [Theory]
    [InlineData(DocumentCategory.PurchaseDocument, "receipts")]
    [InlineData(DocumentCategory.ExpenseAttachment, "expenses")]
    public async Task Legacy_web_root_document_is_still_readable(DocumentCategory category, string folderName)
    {
        var storedFileName = await WriteLegacyDocumentAsync(folderName, "legacy");

        await using var document = await _storage.OpenReadAsync(category, storedFileName);

        Assert.NotNull(document);
        Assert.Equal("legacy", await ReadAllAsync(document!));
    }

    /// <summary>
    /// Protected storage wins over the legacy location, so a document that has been copied into
    /// protected storage is read from there even while the old copy is still on disk awaiting a
    /// separate, deliberate deletion.
    /// </summary>
    [Fact]
    public async Task Protected_document_is_preferred_over_a_legacy_file_of_the_same_name()
    {
        var storedFileName = await WriteLegacyDocumentAsync("receipts", "legacy");
        await _storage.SaveAsync(DocumentCategory.PurchaseDocument, storedFileName, Content("protected"));

        await using var document = await _storage.OpenReadAsync(DocumentCategory.PurchaseDocument, storedFileName);

        Assert.Equal("protected", await ReadAllAsync(document!));
    }

    [Fact]
    public async Task Delete_removes_the_protected_document_and_reports_that_it_did()
    {
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        await _storage.SaveAsync(DocumentCategory.PurchaseDocument, storedFileName, Content("saved"));
        var path = Path.Combine(_contentRoot, "protected-files", "receipts", storedFileName);

        Assert.True(await _storage.DeleteAsync(DocumentCategory.PurchaseDocument, storedFileName));

        Assert.False(File.Exists(path));
        Assert.Null(await _storage.OpenReadAsync(DocumentCategory.PurchaseDocument, storedFileName));
    }

    [Fact]
    public async Task Delete_removes_a_legacy_web_root_document()
    {
        var storedFileName = await WriteLegacyDocumentAsync("expenses", "legacy");
        var path = Path.Combine(_webRoot, "expenses", storedFileName);

        Assert.True(await _storage.DeleteAsync(DocumentCategory.ExpenseAttachment, storedFileName));

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// One category must not be able to touch another's documents: a purchase document and an
    /// expense attachment with the same stored name are two different documents.
    /// </summary>
    [Fact]
    public async Task Delete_does_not_reach_into_another_category()
    {
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        await _storage.SaveAsync(DocumentCategory.ExpenseAttachment, storedFileName, Content("expense"));

        Assert.False(await _storage.DeleteAsync(DocumentCategory.PurchaseDocument, storedFileName));

        Assert.True(File.Exists(Path.Combine(_contentRoot, "protected-files", "expenses", storedFileName)));
    }

    #region Missing documents

    [Fact]
    public async Task Missing_document_reads_as_null_rather_than_throwing()
    {
        Assert.Null(await _storage.OpenReadAsync(DocumentCategory.PurchaseDocument, $"{Guid.NewGuid()}.pdf"));
    }

    [Fact]
    public async Task Deleting_a_missing_document_reports_false_rather_than_throwing()
    {
        Assert.False(await _storage.DeleteAsync(DocumentCategory.PurchaseDocument, $"{Guid.NewGuid()}.pdf"));
    }

    /// <summary>
    /// A record with no document at all is the common case for an operating expense, so an
    /// absent stored name must behave exactly like an absent document.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Absent_stored_name_behaves_like_a_missing_document(string? storedFileName)
    {
        Assert.Null(await _storage.OpenReadAsync(DocumentCategory.ExpenseAttachment, storedFileName));
        Assert.False(await _storage.DeleteAsync(DocumentCategory.ExpenseAttachment, storedFileName));
    }

    #endregion

    #region Path safety

    /// <summary>
    /// No endpoint accepts a stored file name, but the adapter still treats one as untrusted: a
    /// crafted value must not read, delete or overwrite anything outside its category folder.
    /// The file planted here sits two levels up, where a naive join would land.
    /// </summary>
    [Theory]
    [InlineData("../../secret.txt")]
    [InlineData("..\\..\\secret.txt")]
    public async Task A_traversing_stored_name_cannot_read_or_delete_a_file_outside_the_storage_folders(
        string traversingName)
    {
        Directory.CreateDirectory(_contentRoot);
        var outsidePath = Path.Combine(_contentRoot, "secret.txt");
        await File.WriteAllTextAsync(outsidePath, "not a business document");

        Assert.Null(await _storage.OpenReadAsync(DocumentCategory.PurchaseDocument, traversingName));
        Assert.False(await _storage.DeleteAsync(DocumentCategory.PurchaseDocument, traversingName));

        Assert.True(File.Exists(outsidePath));
        Assert.Equal("not a business document", await File.ReadAllTextAsync(outsidePath));
    }

    [Theory]
    [InlineData("../../secret.txt")]
    [InlineData("..\\..\\secret.txt")]
    public async Task A_traversing_stored_name_cannot_overwrite_a_file_outside_the_storage_folders(
        string traversingName)
    {
        Directory.CreateDirectory(_contentRoot);
        var outsidePath = Path.Combine(_contentRoot, "secret.txt");
        await File.WriteAllTextAsync(outsidePath, "not a business document");

        await _storage.SaveAsync(DocumentCategory.PurchaseDocument, traversingName, Content("replaced"));

        Assert.Equal("not a business document", await File.ReadAllTextAsync(outsidePath));

        // Whatever the crafted name reduced to - which differs between Windows and Linux path
        // rules - it was written inside the category folder and nowhere else.
        var categoryFolder = Path.Combine(_contentRoot, "protected-files", "receipts");
        Assert.Single(Directory.GetFiles(categoryFolder));
        Assert.Empty(Directory.GetDirectories(categoryFolder));
    }

    [Fact]
    public async Task An_absolute_stored_name_is_reduced_to_its_file_name()
    {
        Directory.CreateDirectory(_contentRoot);
        var outsidePath = Path.Combine(_contentRoot, "absolute.txt");
        await File.WriteAllTextAsync(outsidePath, "not a business document");

        await _storage.SaveAsync(DocumentCategory.PurchaseDocument, outsidePath, Content("saved"));

        Assert.Equal("not a business document", await File.ReadAllTextAsync(outsidePath));
        Assert.True(File.Exists(Path.Combine(_contentRoot, "protected-files", "receipts", "absolute.txt")));
    }

    #endregion

    #region Write failures

    /// <summary>
    /// Stored names are server-generated and unique, so an existing document means something has
    /// gone wrong. Overwriting it would destroy another record's document, so the write fails
    /// and leaves the stored document exactly as it was.
    /// </summary>
    [Fact]
    public async Task Saving_over_an_existing_document_fails_and_leaves_it_untouched()
    {
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        await _storage.SaveAsync(DocumentCategory.PurchaseDocument, storedFileName, Content("original"));

        await Assert.ThrowsAsync<IOException>(() =>
            _storage.SaveAsync(DocumentCategory.PurchaseDocument, storedFileName, Content("replacement")));

        await using var document = await _storage.OpenReadAsync(DocumentCategory.PurchaseDocument, storedFileName);
        Assert.Equal("original", await ReadAllAsync(document!));
    }

    /// <summary>
    /// A document half-written by a failed upload must not survive as a plausible-looking file:
    /// its metadata row was never saved, so nothing would ever clean it up.
    /// </summary>
    [Fact]
    public async Task A_failed_write_leaves_no_partial_document_behind()
    {
        var storedFileName = $"{Guid.NewGuid()}.pdf";

        await Assert.ThrowsAsync<InvalidOperationException>(() => _storage.SaveAsync(
            DocumentCategory.PurchaseDocument, storedFileName, new FailingStream()));

        Assert.False(File.Exists(Path.Combine(_contentRoot, "protected-files", "receipts", storedFileName)));
        Assert.Null(await _storage.OpenReadAsync(DocumentCategory.PurchaseDocument, storedFileName));
    }

    #endregion

    #region Configuration

    /// <summary>
    /// When the host supplies no web root the adapter assumes the conventional
    /// <c>{ContentRoot}/wwwroot</c>, which is where the legacy documents of a default-configured
    /// host sit.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_web_root_falls_back_to_the_conventional_wwwroot_folder()
    {
        var storage = new FileSystemDocumentStorage(new FileSystemDocumentStorageOptions
        {
            ContentRootPath = _contentRoot,
        });
        var folder = Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot", "receipts"));
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, storedFileName), "legacy");

        await using var document = await storage.OpenReadAsync(DocumentCategory.PurchaseDocument, storedFileName);

        Assert.Equal("legacy", await ReadAllAsync(document!));
    }

    #endregion

    #region Helpers

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static async Task<string> ReadAllAsync(DocumentContent document)
    {
        using var reader = new StreamReader(document.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task<string> WriteLegacyDocumentAsync(string folderName, string text)
    {
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        var folder = Directory.CreateDirectory(Path.Combine(_webRoot, folderName));
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, storedFileName), text);
        return storedFileName;
    }

    /// <summary>A source stream that fails part way through, like an interrupted upload.</summary>
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The upload was interrupted.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion
}
