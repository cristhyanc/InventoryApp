using System.Text;
using Inventory.Application.Documents;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Documents;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Documents;

/// <summary>
/// The Azure Blob document-storage adapter (issue #39, checkpoint 2).
///
/// These tests run against an in-memory stand-in for the container, so the suite needs no Azure
/// account, credentials or network. What they exercise is everything the adapter decides: which
/// blob a call addresses, what happens when it is missing or already there, and - the reason the
/// adapter exists in this shape - that the tenant prefix comes from the trusted business scope
/// and from nowhere else.
///
/// Business A is 1 and business B is 2, matching the other tenancy tests.
/// </summary>
public sealed class AzureBlobDocumentStorageTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    #region Save

    [Fact]
    public async Task A_saved_purchase_document_lands_under_the_current_business_purchases_prefix()
    {
        var container = new FakeBlobContainer();
        var storage = StorageFor(container, BusinessA);

        await storage.SaveAsync(DocumentCategory.PurchaseDocument, "receipt.jpg", Content("saved"));

        Assert.Equal(["tenants/1/purchases/receipt.jpg"], container.BlobNames);
    }

    [Fact]
    public async Task A_saved_expense_attachment_lands_under_the_current_business_expenses_prefix()
    {
        var container = new FakeBlobContainer();
        var storage = StorageFor(container, BusinessB);

        await storage.SaveAsync(DocumentCategory.ExpenseAttachment, "invoice.pdf", Content("saved"));

        Assert.Equal(["tenants/2/expenses/invoice.pdf"], container.BlobNames);
    }

    /// <summary>
    /// Stored names are server-generated and unique, so an existing blob means something is
    /// wrong. Overwriting it would destroy the document another record points at.
    /// </summary>
    [Fact]
    public async Task Saving_over_an_existing_document_fails_and_leaves_it_untouched()
    {
        var container = new FakeBlobContainer();
        var storage = StorageFor(container, BusinessA);
        await storage.SaveAsync(DocumentCategory.PurchaseDocument, "receipt.jpg", Content("original"));

        await Assert.ThrowsAsync<IOException>(() =>
            storage.SaveAsync(DocumentCategory.PurchaseDocument, "receipt.jpg", Content("replacement")));

        await using var document = await storage.OpenReadAsync(DocumentCategory.PurchaseDocument, "receipt.jpg");
        Assert.Equal("original", await ReadAllAsync(document!));
    }

    /// <summary>
    /// A blob becomes readable only when its upload is committed, so a failed upload leaves
    /// nothing behind to find. The adapter deliberately does not delete on failure: the only
    /// blob that could exist at that name is one another record already owns.
    /// </summary>
    [Fact]
    public async Task A_failed_upload_leaves_no_document_behind()
    {
        var container = new FakeBlobContainer
        {
            FailCreateWith = new InvalidOperationException("The upload was interrupted."),
        };
        var storage = StorageFor(container, BusinessA);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.SaveAsync(DocumentCategory.PurchaseDocument, "receipt.jpg", Content("saved")));

        Assert.Empty(container.BlobNames);
        Assert.Null(await storage.OpenReadAsync(DocumentCategory.PurchaseDocument, "receipt.jpg"));
    }

    [Fact]
    public async Task A_stored_name_that_reduces_to_nothing_usable_is_refused_rather_than_stored()
    {
        var container = new FakeBlobContainer();
        var storage = StorageFor(container, BusinessA);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.SaveAsync(DocumentCategory.PurchaseDocument, "..", Content("saved")));

        Assert.Empty(container.BlobNames);
    }

    #endregion

    #region Read

    [Fact]
    public async Task A_saved_document_is_read_back_with_its_length_and_last_modified_time()
    {
        var container = new FakeBlobContainer
        {
            LastModified = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero),
        };
        var storage = StorageFor(container, BusinessA);
        await storage.SaveAsync(DocumentCategory.PurchaseDocument, "receipt.jpg", Content("saved"));

        await using var document = await storage.OpenReadAsync(DocumentCategory.PurchaseDocument, "receipt.jpg");

        Assert.NotNull(document);
        Assert.Equal(5, document!.ByteLength);
        Assert.Equal(new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero), document.LastModified);
        Assert.Equal("saved", await ReadAllAsync(document));
    }

    [Fact]
    public async Task A_missing_document_reads_as_null_rather_than_throwing()
    {
        var storage = StorageFor(new FakeBlobContainer(), BusinessA);

        Assert.Null(await storage.OpenReadAsync(DocumentCategory.PurchaseDocument, "absent.jpg"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    public async Task An_unusable_stored_name_behaves_like_a_missing_document(string? storedFileName)
    {
        var container = new FakeBlobContainer();
        var storage = StorageFor(container, BusinessA);

        Assert.Null(await storage.OpenReadAsync(DocumentCategory.ExpenseAttachment, storedFileName));
        Assert.False(await storage.DeleteAsync(DocumentCategory.ExpenseAttachment, storedFileName));
        Assert.Empty(container.Requested);
    }

    /// <summary>
    /// The two categories are separate documents even under one business, so a purchase document
    /// must not be reachable through the expense category.
    /// </summary>
    [Fact]
    public async Task One_category_cannot_read_another_categorys_document()
    {
        var container = new FakeBlobContainer();
        var storage = StorageFor(container, BusinessA);
        await storage.SaveAsync(DocumentCategory.ExpenseAttachment, "same.pdf", Content("expense"));

        Assert.Null(await storage.OpenReadAsync(DocumentCategory.PurchaseDocument, "same.pdf"));
        Assert.False(await storage.DeleteAsync(DocumentCategory.PurchaseDocument, "same.pdf"));
        Assert.Equal(["tenants/1/expenses/same.pdf"], container.BlobNames);
    }

    #endregion

    #region Delete

    [Fact]
    public async Task Delete_removes_the_current_business_document_and_reports_that_it_did()
    {
        var container = new FakeBlobContainer();
        var storage = StorageFor(container, BusinessA);
        await storage.SaveAsync(DocumentCategory.PurchaseDocument, "receipt.jpg", Content("saved"));

        Assert.True(await storage.DeleteAsync(DocumentCategory.PurchaseDocument, "receipt.jpg"));

        Assert.Empty(container.BlobNames);
        Assert.Null(await storage.OpenReadAsync(DocumentCategory.PurchaseDocument, "receipt.jpg"));
    }

    [Fact]
    public async Task Deleting_a_missing_document_reports_false_rather_than_throwing()
    {
        var storage = StorageFor(new FakeBlobContainer(), BusinessA);

        Assert.False(await storage.DeleteAsync(DocumentCategory.PurchaseDocument, "absent.jpg"));
    }

    #endregion

    #region Two-business isolation

    /// <summary>
    /// The whole point of the scheme: two businesses storing the same server-generated name hold
    /// two independent documents, neither of which can see the other.
    /// </summary>
    [Fact]
    public async Task Two_businesses_can_hold_the_same_stored_name_independently()
    {
        var container = new FakeBlobContainer();
        var businessA = StorageFor(container, BusinessA);
        var businessB = StorageFor(container, BusinessB);

        await businessA.SaveAsync(DocumentCategory.PurchaseDocument, "same.jpg", Content("A's document"));
        await businessB.SaveAsync(DocumentCategory.PurchaseDocument, "same.jpg", Content("B's document"));

        Assert.Equal(
            ["tenants/1/purchases/same.jpg", "tenants/2/purchases/same.jpg"],
            container.BlobNames.OrderBy(name => name, StringComparer.Ordinal));

        await using var read = await businessA.OpenReadAsync(DocumentCategory.PurchaseDocument, "same.jpg");
        Assert.Equal("A's document", await ReadAllAsync(read!));
    }

    /// <summary>
    /// Knowing business B's stored file name is the strongest thing an attacker could learn, and
    /// it is exactly what an id-guessing attack would yield. It buys nothing: the prefix comes
    /// from A's own scope, so A's read addresses a blob of A's that does not exist.
    /// </summary>
    [Fact]
    public async Task Business_A_cannot_read_business_B_document_even_knowing_its_stored_name()
    {
        var container = new FakeBlobContainer();
        await StorageFor(container, BusinessB)
            .SaveAsync(DocumentCategory.ExpenseAttachment, "invoice.pdf", Content("B's document"));
        container.ClearRequested();

        var read = await StorageFor(container, BusinessA)
            .OpenReadAsync(DocumentCategory.ExpenseAttachment, "invoice.pdf");

        Assert.Null(read);
        Assert.Equal(["tenants/1/expenses/invoice.pdf"], container.Requested);
        Assert.Equal(["tenants/2/expenses/invoice.pdf"], container.BlobNames);
    }

    /// <summary>
    /// Deletion is the destructive half of the same lookup: a refused read that still deleted
    /// the other business's document would be worse than the leak it prevented.
    /// </summary>
    [Fact]
    public async Task Business_A_cannot_delete_business_B_document_even_knowing_its_stored_name()
    {
        var container = new FakeBlobContainer();
        var businessB = StorageFor(container, BusinessB);
        await businessB.SaveAsync(DocumentCategory.ExpenseAttachment, "invoice.pdf", Content("B's document"));

        Assert.False(await StorageFor(container, BusinessA)
            .DeleteAsync(DocumentCategory.ExpenseAttachment, "invoice.pdf"));

        Assert.Equal(["tenants/2/expenses/invoice.pdf"], container.BlobNames);
        await using var stillThere = await businessB.OpenReadAsync(DocumentCategory.ExpenseAttachment, "invoice.pdf");
        Assert.Equal("B's document", await ReadAllAsync(stillThere!));
    }

    /// <summary>
    /// A traversing stored name cannot climb out of the current business's prefix into another's,
    /// which is the one way a crafted name could have crossed the boundary.
    /// </summary>
    [Fact]
    public async Task A_traversing_stored_name_cannot_reach_another_business_prefix()
    {
        var container = new FakeBlobContainer();
        await StorageFor(container, BusinessB)
            .SaveAsync(DocumentCategory.PurchaseDocument, "secret.jpg", Content("B's document"));
        container.ClearRequested();
        var businessA = StorageFor(container, BusinessA);

        Assert.Null(await businessA.OpenReadAsync(
            DocumentCategory.PurchaseDocument, "../../2/purchases/secret.jpg"));
        Assert.False(await businessA.DeleteAsync(
            DocumentCategory.PurchaseDocument, "../../2/purchases/secret.jpg"));

        Assert.Equal(["tenants/2/purchases/secret.jpg"], container.BlobNames);
        Assert.All(container.Requested, name => Assert.StartsWith("tenants/1/", name, StringComparison.Ordinal));
    }

    #endregion

    #region Business scope

    /// <summary>
    /// A scope that resolved no business reads and writes nothing. It fails closed and loudly:
    /// there is no prefix that could stand in for "no business", and silently picking one would
    /// be a cross-business write.
    /// </summary>
    [Fact]
    public async Task A_denied_business_scope_cannot_save_read_or_delete()
    {
        var container = new FakeBlobContainer();
        var storage = new AzureBlobDocumentStorage(container, new BusinessScope());

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            storage.SaveAsync(DocumentCategory.PurchaseDocument, "receipt.jpg", Content("saved")));
        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            storage.OpenReadAsync(DocumentCategory.PurchaseDocument, "receipt.jpg"));
        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            storage.DeleteAsync(DocumentCategory.PurchaseDocument, "receipt.jpg"));

        Assert.Empty(container.Requested);
        Assert.Empty(container.BlobNames);
    }

    /// <summary>
    /// The deliberate all-business opt-out has no meaning for a document, which belongs to
    /// exactly one business. Rather than choose a prefix, the adapter refuses: cross-business
    /// access for maintenance work has to be its own explicit path, not a mode of the request
    /// path every upload and download already uses.
    /// </summary>
    [Fact]
    public async Task An_unscoped_business_scope_cannot_save_read_or_delete()
    {
        var container = new FakeBlobContainer();
        var storage = new AzureBlobDocumentStorage(container, UnscopedBusinessScope.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.SaveAsync(DocumentCategory.PurchaseDocument, "receipt.jpg", Content("saved")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.OpenReadAsync(DocumentCategory.PurchaseDocument, "receipt.jpg"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.DeleteAsync(DocumentCategory.PurchaseDocument, "receipt.jpg"));

        Assert.Empty(container.Requested);
        Assert.Empty(container.BlobNames);
    }

    #endregion

    #region Helpers

    private static AzureBlobDocumentStorage StorageFor(IDocumentBlobContainer container, int businessId)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(businessId));
        return new AzureBlobDocumentStorage(container, scope);
    }

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static async Task<string> ReadAllAsync(DocumentContent document)
    {
        using var reader = new StreamReader(document.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// An in-memory container with the semantics the real one gives us: create-if-absent, a
    /// missing blob reading as nothing, and a failed create storing nothing. It records every
    /// blob name the adapter asked for, so a test can assert which blob was addressed and not
    /// merely what came back.
    /// </summary>
    private sealed class FakeBlobContainer : IDocumentBlobContainer
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
        private readonly List<string> _requested = [];

        public DateTimeOffset LastModified { get; init; } = DateTimeOffset.UnixEpoch;

        public Exception? FailCreateWith { get; init; }

        public IReadOnlyList<string> Requested => _requested;

        /// <summary>Forgets the calls made while arranging a test, so an assertion about which
        /// blob was addressed only sees the act.</summary>
        public void ClearRequested() => _requested.Clear();

        public IReadOnlyCollection<string> BlobNames => _blobs.Keys;

        public async Task<bool> CreateAsync(string blobName, Stream content, CancellationToken cancellationToken)
        {
            _requested.Add(blobName);
            if (_blobs.ContainsKey(blobName)) return false;
            if (FailCreateWith is not null) throw FailCreateWith;

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            _blobs[blobName] = buffer.ToArray();
            return true;
        }

        public Task<BlobDocument?> OpenReadAsync(string blobName, CancellationToken cancellationToken)
        {
            _requested.Add(blobName);
            return Task.FromResult(_blobs.TryGetValue(blobName, out var content)
                ? new BlobDocument(new MemoryStream(content), content.Length, LastModified)
                : null);
        }

        public Task<bool> DeleteAsync(string blobName, CancellationToken cancellationToken)
        {
            _requested.Add(blobName);
            return Task.FromResult(_blobs.Remove(blobName));
        }
    }

    #endregion
}
