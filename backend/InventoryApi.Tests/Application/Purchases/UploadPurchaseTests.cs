using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;
using InventoryApi.Tests.Application.Expenses;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

public class UploadPurchaseTests
{
    private static PurchaseFields Fields(int? supplierId = null, DateTime? purchaseDate = null) =>
        new("Weekly restock", null, null, null, null, purchaseDate, supplierId);

    private static PurchaseFileInput FileInput(string fileName = "receipt.jpg", long length = 3) =>
        new(fileName, "image/jpeg", length, () => new MemoryStream([1, 2, 3]));

    [Fact]
    public async Task An_empty_file_is_rejected_without_touching_the_store_or_documents()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UploadPurchase(store, documents);

        var result = await useCase.Handle(FileInput(length: 0), Fields(), [], CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task An_oversized_file_is_rejected_without_touching_the_store_or_documents()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UploadPurchase(store, documents);

        var result = await useCase.Handle(FileInput(length: 10 * 1024 * 1024 + 1), Fields(), [], CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task A_disallowed_extension_is_rejected_without_touching_the_store_or_documents()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UploadPurchase(store, documents);

        var result = await useCase.Handle(FileInput("receipt.exe"), Fields(), [], CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task An_unknown_supplier_is_rejected_before_any_item_validation_or_file_save()
    {
        var store = new FakePurchaseStore { SupplierExists = false };
        var documents = new FakeDocumentStorage();
        var useCase = new UploadPurchase(store, documents);

        var result = await useCase.Handle(
            FileInput(), Fields(supplierId: 99), [new PurchaseItemInput(1, -1, 1)], CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task A_malformed_item_throws_before_any_file_save()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UploadPurchase(store, documents);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.Handle(FileInput(), Fields(), [new PurchaseItemInput(1, 0, 1)], CancellationToken.None));

        Assert.Equal(PurchaseItemFormatPolicy.InvalidItemsMessage, exception.Message);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task An_unknown_product_throws_before_any_file_save()
    {
        var store = new FakePurchaseStore { AllProductsExist = false };
        var documents = new FakeDocumentStorage();
        var useCase = new UploadPurchase(store, documents);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.Handle(FileInput(), Fields(), [new PurchaseItemInput(404, 1, 1)], CancellationToken.None));

        Assert.Equal("One or more purchase products do not exist.", exception.Message);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task A_purchase_date_at_or_before_a_cost_transition_cutoff_throws_before_any_file_save()
    {
        var cutoff = new DateTime(2026, 1, 1);
        var store = new FakePurchaseStore { ConflictingBaseline = (1, cutoff) };
        var documents = new FakeDocumentStorage();
        var useCase = new UploadPurchase(store, documents);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.Handle(FileInput(), Fields(purchaseDate: cutoff), [new PurchaseItemInput(1, 1, 1)], CancellationToken.None));

        Assert.Equal(PurchaseCostTransitionPolicy.DateBeforeCutoffMessage(cutoff, 1), exception.Message);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task A_valid_request_saves_the_document_and_persists_the_purchase()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UploadPurchase(store, documents);

        var record = await useCase.Handle(FileInput(), Fields(), [new PurchaseItemInput(1, 2m, 1.5m)], CancellationToken.None);

        Assert.NotNull(record);
        Assert.Single(documents.SavedFileNames);
        Assert.True(documents.Contains(DocumentCategory.PurchaseDocument, record!.StoredFileName));
        Assert.Equal(store.LastCreated, record);
    }

    [Fact]
    public async Task A_blank_title_falls_back_to_the_uploaded_file_name()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UploadPurchase(store, documents);

        var record = await useCase.Handle(
            FileInput("scan.jpg"), new PurchaseFields("   ", null, null, null, null, null, null), [], CancellationToken.None);

        Assert.Equal("scan.jpg", record!.Title);
    }

    [Fact]
    public async Task When_persistence_fails_after_saving_the_document_is_cleaned_up()
    {
        var store = new FakePurchaseStore { ThrowOnCreate = true };
        var documents = new FakeDocumentStorage();
        var useCase = new UploadPurchase(store, documents);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.Handle(FileInput(), Fields(), [new PurchaseItemInput(1, 1, 1)], CancellationToken.None));

        Assert.Single(documents.SavedFileNames);
        var savedFileName = documents.SavedFileNames[0];
        Assert.Contains(savedFileName, documents.DeletedFileNames);
        Assert.False(documents.Contains(DocumentCategory.PurchaseDocument, savedFileName));
    }
}
