using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Application.SupplierOrders;
using Inventory.Application.Tenancy;
using Inventory.Domain.Purchases;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Expenses;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

public class UploadPurchaseTests
{
    /// <summary>
    /// A fixed UTC instant on a date the Australia/Sydney calendar is already a day ahead of, so a
    /// defaulted purchase date proves the use case stores the clock's UTC instant (issue #310) rather
    /// than a business-calendar date.
    /// </summary>
    private static readonly DateTime NowUtc = new(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc);

    private static readonly FakeClock Clock = new(NowUtc);

    /// <summary>The identified operator these tests record as, and whose attribution a completion decision carries.</summary>
    private static readonly ActorIdentity Operator = Actor("44444444-4444-4444-4444-444444444444");

    private static UploadPurchase UseCase(
        IPurchaseStore store, IDocumentStorage documents, IAuthenticatedActorAccessor? actors = null) =>
        new(store, documents, actors ?? FakeAuthenticatedActorAccessor.Identified(Operator), Clock);

    private static ActorIdentity Actor(string objectId)
    {
        ActorIdentity.TryCreate("22222222-2222-2222-2222-222222222222", objectId, out var actor);
        return actor!;
    }

    private static PurchaseFields Fields(int? supplierId = null, DateTime? purchaseDate = null) =>
        new("Weekly restock", null, null, null, null, purchaseDate, supplierId);

    private static PurchaseFileInput FileInput(string fileName = "receipt.jpg", long length = 3) =>
        new(fileName, "image/jpeg", length, () => new MemoryStream([1, 2, 3]));

    [Fact]
    public async Task An_empty_file_is_rejected_without_touching_the_store_or_documents()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

        var result = await useCase.Handle(FileInput(length: 0), Fields(), [], CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task An_oversized_file_is_rejected_without_touching_the_store_or_documents()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

        var result = await useCase.Handle(FileInput(length: 10 * 1024 * 1024 + 1), Fields(), [], CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task A_disallowed_extension_is_rejected_without_touching_the_store_or_documents()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

        var result = await useCase.Handle(FileInput("receipt.exe"), Fields(), [], CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task An_unknown_supplier_is_rejected_before_any_item_validation_or_file_save()
    {
        var store = new FakePurchaseStore { SupplierExists = false };
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

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
        var useCase = UseCase(store, documents);

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
        var useCase = UseCase(store, documents);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.Handle(FileInput(), Fields(), [new PurchaseItemInput(404, 1, 1)], CancellationToken.None));

        Assert.Equal("One or more purchase products do not exist.", exception.Message);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task A_purchase_date_at_or_before_a_cost_transition_cutoff_throws_before_any_file_save()
    {
        var cutoff = new DateTime(2026, 1, 1);
        var store = new FakePurchaseStore { ConflictingBaseline = (1, "Coke", cutoff) };
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.Handle(FileInput(), Fields(purchaseDate: cutoff), [new PurchaseItemInput(1, 1, 1)], CancellationToken.None));

        // Issue #833: the refusal names the product the operator has to fix, not only its ID.
        Assert.Equal(PurchaseCostTransitionPolicy.DateBeforeCutoffMessage(cutoff, 1, "Coke"), exception.Message);
        Assert.Equal(
            $"Purchase date must be after the inventory-cost transition cutoff {cutoff:O} for product Coke (1).",
            exception.Message);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task A_cutoff_refusal_for_a_product_with_no_loaded_name_keeps_the_bare_id_wording()
    {
        var cutoff = new DateTime(2026, 1, 1);
        var store = new FakePurchaseStore { ConflictingBaseline = (1, null, cutoff) };
        var useCase = UseCase(store, new FakeDocumentStorage());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.Handle(FileInput(), Fields(purchaseDate: cutoff), [new PurchaseItemInput(1, 1, 1)], CancellationToken.None));

        Assert.Equal(
            $"Purchase date must be after the inventory-cost transition cutoff {cutoff:O} for product 1.",
            exception.Message);
    }

    [Fact]
    public async Task A_valid_request_saves_the_document_and_persists_the_purchase()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

        var record = await useCase.Handle(FileInput(), Fields(), [new PurchaseItemInput(1, 2m, 1.5m)], CancellationToken.None);

        Assert.NotNull(record);
        Assert.Single(documents.SavedFileNames);
        Assert.True(documents.Contains(DocumentCategory.PurchaseDocument, record!.StoredFileName));
        Assert.Equal(store.LastCreated, record);
    }

    /// <summary>
    /// Issue #310: a missing purchase date defaults to the clock port's UTC instant, the same value
    /// the host clock used to supply directly. The stored value for a given instant is unchanged - it
    /// is deliberately not reduced to the Sydney business date, because a purchase date omitted by
    /// the client is recorded as the instant the purchase was uploaded.
    /// </summary>
    [Fact]
    public async Task A_missing_purchase_date_defaults_to_the_clocks_utc_instant()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

        var record = await useCase.Handle(
            FileInput(), Fields(purchaseDate: null), [new PurchaseItemInput(1, 1, 1)], CancellationToken.None);

        Assert.Equal(NowUtc, record!.PurchaseDate);
        Assert.Equal(NowUtc, store.LastConflictCheckedPurchaseDate);
    }

    [Fact]
    public async Task A_supplied_purchase_date_is_preserved_and_the_clock_is_not_consulted()
    {
        var suppliedDate = new DateTime(2026, 2, 1);
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

        var record = await useCase.Handle(
            FileInput(), Fields(purchaseDate: suppliedDate), [new PurchaseItemInput(1, 1, 1)], CancellationToken.None);

        Assert.Equal(suppliedDate, record!.PurchaseDate);
        Assert.Equal(suppliedDate, store.LastConflictCheckedPurchaseDate);
    }

    [Fact]
    public async Task A_blank_title_falls_back_to_the_uploaded_file_name()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

        var record = await useCase.Handle(
            FileInput("scan.jpg"), new PurchaseFields("   ", null, null, null, null, null, null), [], CancellationToken.None);

        Assert.Equal("scan.jpg", record!.Title);
    }

    [Fact]
    public async Task When_persistence_fails_after_saving_the_document_is_cleaned_up()
    {
        var store = new FakePurchaseStore { ThrowOnCreate = true };
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.Handle(FileInput(), Fields(), [new PurchaseItemInput(1, 1, 1)], CancellationToken.None));

        Assert.Single(documents.SavedFileNames);
        var savedFileName = documents.SavedFileNames[0];
        Assert.Contains(savedFileName, documents.DeletedFileNames);
        Assert.False(documents.Contains(DocumentCategory.PurchaseDocument, savedFileName));
    }

    /// <summary>
    /// A purchase with no completion choice hands the store none (issue #974), so the ordinary save
    /// path is untouched and nothing can close an order that nobody asked about.
    /// </summary>
    [Fact]
    public async Task A_purchase_with_no_completion_choice_hands_the_store_none()
    {
        var store = new FakePurchaseStore();
        var useCase = UseCase(store, new FakeDocumentStorage());

        await useCase.Handle(FileInput(), Fields(supplierId: 4), [new PurchaseItemInput(1, 1, 1)], CancellationToken.None);

        Assert.Null(store.LastCompletionDecision);
    }

    /// <summary>
    /// The request carries the order id and the fingerprint; the actor and the instant are the
    /// server's. That split is what keeps a client from asserting who decided or when.
    /// </summary>
    [Fact]
    public async Task A_completion_choice_is_attributed_to_the_caller_and_the_clock()
    {
        var store = new FakePurchaseStore();
        var useCase = UseCase(store, new FakeDocumentStorage());

        await useCase.Handle(
            FileInput(),
            Fields(supplierId: 4),
            [new PurchaseItemInput(1, 1, 1)],
            new SupplierOrderCompletionRequest(9, "fingerprint-from-the-preview"),
            CancellationToken.None);

        var decision = store.LastCompletionDecision;
        Assert.NotNull(decision);
        Assert.Equal((9, "fingerprint-from-the-preview"), (decision!.SupplierOrderId, decision.DecisionFingerprint));
        Assert.Equal(
            (Operator.DirectoryTenantId, Operator.ObjectId, NowUtc),
            (decision.DecidedByDirectoryTenantId, decision.DecidedByObjectId, decision.DecidedAtUtc));
    }

    /// <summary>
    /// A completion that cannot name who made it is not auditable, so it is refused before the
    /// document is stored and before the write transaction opens - the same rule
    /// <c>ApplyInventoryCostRepair</c> applies to a costing repair.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unidentifiable_caller_cannot_complete_an_order_and_stores_nothing(bool notAuthenticated)
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(
            store,
            documents,
            notAuthenticated
                ? FakeAuthenticatedActorAccessor.NotAuthenticated()
                : FakeAuthenticatedActorAccessor.Unidentifiable());

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() => useCase.Handle(
            FileInput(),
            Fields(supplierId: 4),
            [new PurchaseItemInput(1, 1, 1)],
            new SupplierOrderCompletionRequest(9, "fingerprint-from-the-preview"),
            CancellationToken.None));

        Assert.Empty(documents.SavedFileNames);
        Assert.Null(store.LastCreated);
        Assert.Null(store.LastCompletionDecision);
    }
}
