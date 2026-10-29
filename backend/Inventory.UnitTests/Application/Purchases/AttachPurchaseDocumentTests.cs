using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;
using InventoryApi.Tests.Application.Expenses;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

/// <summary>
/// Adding a document to a purchase that has none (issue #976), at the orchestration level: which
/// requests never reach storage, which reach the store, and - the part that matters most - that the
/// bytes are removed again whenever the write does not happen.
///
/// <para>The serialisation of "attach only if empty" is the EF adapter's write lock and is covered
/// by <c>PurchaseDocumentAttachConcurrencyTests</c> against real SQLite; what these pin is that this
/// use case asks the store rather than deciding emptiness itself, and that it cleans up after every
/// answer but <c>Attached</c>.</para>
/// </summary>
public class AttachPurchaseDocumentTests
{
    private static AttachPurchaseDocument UseCase(IPurchaseStore store, IDocumentStorage documents) =>
        new(store, documents);

    private static PurchaseFileInput FileInput(string fileName = "scan.jpg", long length = 3) =>
        new(fileName, "image/jpeg", length, () => new MemoryStream([1, 2, 3]));

    private static PurchaseFileMetadata ExistingDocument() =>
        new("already-stored.jpg", "image/jpeg", "first.jpg", 7);

    [Fact]
    public async Task An_empty_document_is_refused_without_touching_the_store_or_documents()
    {
        var store = new FakePurchaseStore();
        var purchase = store.AddPurchase();
        var documents = new FakeDocumentStorage();

        var result = await UseCase(store, documents).Handle(purchase.Id, FileInput(length: 0), CancellationToken.None);

        Assert.Equal(AttachPurchaseDocumentOutcome.InvalidDocument, result.Outcome);
        Assert.Equal(PurchaseDocumentPolicy.SizeErrorMessage, result.Message);
        Assert.Empty(documents.SavedFileNames);
        Assert.Null(store.LastAttachedDocument);
    }

    [Fact]
    public async Task An_oversized_document_is_refused_without_touching_the_store_or_documents()
    {
        var store = new FakePurchaseStore();
        var purchase = store.AddPurchase();
        var documents = new FakeDocumentStorage();

        var result = await UseCase(store, documents).Handle(
            purchase.Id, FileInput(length: PurchaseDocumentPolicy.MaxFileSizeBytes + 1), CancellationToken.None);

        Assert.Equal(AttachPurchaseDocumentOutcome.InvalidDocument, result.Outcome);
        Assert.Equal(PurchaseDocumentPolicy.SizeErrorMessage, result.Message);
        Assert.Empty(documents.SavedFileNames);
        Assert.Null(store.LastAttachedDocument);
    }

    [Fact]
    public async Task A_disallowed_extension_is_refused_without_touching_the_store_or_documents()
    {
        var store = new FakePurchaseStore();
        var purchase = store.AddPurchase();
        var documents = new FakeDocumentStorage();

        var result = await UseCase(store, documents).Handle(
            purchase.Id, FileInput("notes.txt"), CancellationToken.None);

        Assert.Equal(AttachPurchaseDocumentOutcome.InvalidDocument, result.Outcome);
        Assert.Equal(PurchaseDocumentPolicy.ExtensionErrorMessage, result.Message);
        Assert.Empty(documents.SavedFileNames);
        Assert.Null(store.LastAttachedDocument);
    }

    /// <summary>
    /// The uploaded name is recorded for display, never used as a storage name: the stored name is
    /// the server's own, and only its extension comes from the upload.
    /// </summary>
    [Fact]
    public async Task An_accepted_document_is_stored_under_a_server_generated_name_and_recorded()
    {
        var store = new FakePurchaseStore();
        var purchase = store.AddPurchase();
        var documents = new FakeDocumentStorage();

        var result = await UseCase(store, documents).Handle(
            purchase.Id, FileInput("Weekly Receipt.JPG"), CancellationToken.None);

        Assert.Equal(AttachPurchaseDocumentOutcome.Attached, result.Outcome);
        Assert.Null(result.Message);
        var storedFileName = Assert.Single(documents.SavedFileNames);
        Assert.True(documents.Contains(DocumentCategory.PurchaseDocument, storedFileName));
        Assert.EndsWith(".jpg", storedFileName, StringComparison.Ordinal);
        Assert.DoesNotContain("Weekly", storedFileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new PurchaseFileMetadata(storedFileName, "image/jpeg", "Weekly Receipt.JPG", 3),
            store.LastAttachedDocument);
        Assert.Equal(
            (storedFileName, "image/jpeg", "Weekly Receipt.JPG", (long?)3),
            (result.Purchase!.StoredFileName, result.Purchase.ContentType, result.Purchase.FileName,
                result.Purchase.FileSizeBytes));
    }

    /// <summary>
    /// The emptiness answer comes from the store, inside its own write transaction, and a refusal
    /// takes the bytes with it - otherwise a refused attach would leave a document in storage that
    /// nothing in the database names and nothing can ever reach again.
    /// </summary>
    [Fact]
    public async Task A_purchase_that_already_has_a_document_is_refused_and_the_bytes_are_removed()
    {
        var store = new FakePurchaseStore();
        var purchase = store.AddPurchase(ExistingDocument());
        var documents = new FakeDocumentStorage();

        var result = await UseCase(store, documents).Handle(purchase.Id, FileInput(), CancellationToken.None);

        Assert.Equal(AttachPurchaseDocumentOutcome.AlreadyHasDocument, result.Outcome);
        Assert.Equal(PurchaseDocumentPolicy.AlreadyHasDocumentMessage, result.Message);
        Assert.Null(result.Purchase);
        var storedFileName = Assert.Single(documents.SavedFileNames);
        Assert.Contains(storedFileName, documents.DeletedFileNames);
        Assert.False(documents.Contains(DocumentCategory.PurchaseDocument, storedFileName));
        // And the document that was already there is untouched.
        Assert.DoesNotContain("already-stored.jpg", documents.DeletedFileNames);
    }

    /// <summary>An unknown purchase is a bare not-found with no message, and leaves no bytes behind either.</summary>
    [Fact]
    public async Task An_unknown_purchase_is_not_found_and_the_bytes_are_removed()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();

        var result = await UseCase(store, documents).Handle(404, FileInput(), CancellationToken.None);

        Assert.Equal(AttachPurchaseDocumentOutcome.PurchaseNotFound, result.Outcome);
        Assert.Null(result.Message);
        var storedFileName = Assert.Single(documents.SavedFileNames);
        Assert.Contains(storedFileName, documents.DeletedFileNames);
        Assert.False(documents.Contains(DocumentCategory.PurchaseDocument, storedFileName));
    }

    /// <summary>
    /// A persistence failure propagates - the caller has to hear that nothing was recorded - and the
    /// bytes go first, the same cleanup-on-failure ordering <c>UploadPurchase</c> uses.
    /// </summary>
    [Fact]
    public async Task When_the_write_fails_the_document_is_cleaned_up_and_the_failure_propagates()
    {
        var store = new FakePurchaseStore { ThrowOnAttach = true };
        var purchase = store.AddPurchase();
        var documents = new FakeDocumentStorage();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UseCase(store, documents).Handle(purchase.Id, FileInput(), CancellationToken.None));

        var storedFileName = Assert.Single(documents.SavedFileNames);
        Assert.Contains(storedFileName, documents.DeletedFileNames);
        Assert.False(documents.Contains(DocumentCategory.PurchaseDocument, storedFileName));
    }
}
