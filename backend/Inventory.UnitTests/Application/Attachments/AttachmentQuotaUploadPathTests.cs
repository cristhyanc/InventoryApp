using Inventory.Application.Attachments;
using Inventory.Application.Documents;
using Inventory.Application.Expenses;
using Inventory.Application.Purchases;
using Inventory.Domain.Attachments;
using Inventory.Domain.Expenses;
using Inventory.Domain.Subscriptions;
using InventoryApi.Tests.Application.Expenses;
using InventoryApi.Tests.Application.Machines;
using InventoryApi.Tests.Application.Purchases;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Attachments;

/// <summary>
/// Every upload path against its business's attachment storage allowance (issue #968), at the
/// orchestration level: purchase create, "add a document later", and the operating-expense
/// multipart create and update.
///
/// <para>What these pin is the composition the quota rests on, and in this order: nothing is
/// written to storage before the room is reserved; a refusal is about the <em>document</em> and
/// never about the record; and whenever the row does not end up referring to the file, both the
/// bytes and the reservation are released. The arithmetic itself is
/// <c>AttachmentStorageQuotaPolicyTests</c>', and the serialisation is the EF adapter's, against
/// real SQLite.</para>
/// </summary>
public class AttachmentQuotaUploadPathTests
{
    private static readonly DateTime NowUtc = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Free's allowance, which is what the guard reads for a business on Free.</summary>
    private static readonly long FreeLimit =
        PlanCatalogue.For(SubscriptionPlan.Free).AttachmentStorageLimitBytes;

    [Fact]
    public async Task A_purchase_document_is_refused_when_the_business_is_full_and_nothing_is_written()
    {
        var quota = new FakeAttachmentQuotaStore { StoredBytes = FreeLimit };
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();

        var refusal = await Assert.ThrowsAsync<AttachmentStorageQuotaExceededException>(() =>
            Upload(store, documents, quota).Handle(
                File("receipt.jpg", 10), Fields(), [], CancellationToken.None));

        Assert.Equal(AttachmentStorageQuotaPolicy.StorageFullMessage, refusal.Message);
        Assert.Empty(documents.SavedFileNames);
        Assert.Null(store.LastCreated);
    }

    /// <summary>
    /// The refusal is the document's alone. A purchase with no document at all is recorded exactly
    /// as it is under the allowance - which is the whole reason issue #976 made the document
    /// optional, and what stops a full business from being unable to record what it bought.
    /// </summary>
    [Fact]
    public async Task A_purchase_with_no_document_is_recorded_even_when_the_business_is_full()
    {
        var quota = new FakeAttachmentQuotaStore { StoredBytes = FreeLimit };
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();

        var purchase = await Upload(store, documents, quota).Handle(
            file: null, Fields(), [], CancellationToken.None);

        Assert.NotNull(purchase);
        Assert.Empty(quota.Requests);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task A_purchase_document_that_fits_is_reserved_and_the_reservation_reaches_the_write()
    {
        var quota = new FakeAttachmentQuotaStore();
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();

        await Upload(store, documents, quota).Handle(
            File("receipt.jpg", 2_048), Fields(), [], CancellationToken.None);

        var request = Assert.Single(quota.Requests);
        Assert.Equal(DocumentCategory.PurchaseDocument, request.Category);
        Assert.Null(request.RecordId);
        Assert.Equal(2_048, request.UploadBytes);
        Assert.Equal(FreeLimit, request.LimitBytes);
        Assert.Equal(2_048, store.LastCreateReservation!.ChargeableBytes);
        Assert.Empty(quota.Released);
    }

    /// <summary>
    /// The crash-recovery answer at the commit: the reservation had expired and the re-validation
    /// inside the write refused the upload. The purchase is not created, the bytes this request
    /// wrote are removed, and the reservation is released rather than left holding storage.
    /// </summary>
    [Fact]
    public async Task A_purchase_whose_expired_reservation_no_longer_fits_releases_its_bytes_and_its_room()
    {
        var quota = new FakeAttachmentQuotaStore();
        var store = new FakePurchaseStore { QuotaExceededOnCreate = true };
        var documents = new FakeDocumentStorage();

        await Assert.ThrowsAsync<AttachmentStorageQuotaExceededException>(() =>
            Upload(store, documents, quota).Handle(
                File("receipt.jpg", 2_048), Fields(), [], CancellationToken.None));

        var stored = Assert.Single(documents.SavedFileNames);
        Assert.Contains(stored, documents.DeletedFileNames);
        Assert.Single(quota.Released);
        Assert.Empty(quota.Outstanding);
    }

    /// <summary>A store failure releases the room too, for the same reason: nothing refers to the file.</summary>
    [Fact]
    public async Task A_failed_purchase_write_releases_the_reservation()
    {
        var quota = new FakeAttachmentQuotaStore();
        var store = new FakePurchaseStore { ThrowOnCreate = true };
        var documents = new FakeDocumentStorage();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Upload(store, documents, quota).Handle(
                File("receipt.jpg", 2_048), Fields(), [], CancellationToken.None));

        Assert.Single(quota.Released);
        Assert.Empty(quota.Outstanding);
    }

    [Fact]
    public async Task Attaching_a_document_later_is_refused_when_the_business_is_full()
    {
        var quota = new FakeAttachmentQuotaStore { StoredBytes = FreeLimit };
        var store = new FakePurchaseStore();
        var purchase = store.AddPurchase();
        var documents = new FakeDocumentStorage();

        var result = await new AttachPurchaseDocument(
                store, documents, FakeAttachmentQuota.Enforcing(quota))
            .Handle(purchase.Id, File("receipt.jpg", 10), CancellationToken.None);

        Assert.Equal(AttachPurchaseDocumentOutcome.StorageFull, result.Outcome);
        Assert.Equal(AttachmentStorageQuotaPolicy.StorageFullMessage, result.Message);
        Assert.Empty(documents.SavedFileNames);
        Assert.Null(store.LastAttachedDocument);
    }

    /// <summary>
    /// An attach is computed against the purchase it names, so the reservation records that
    /// purchase - and a refused attach gives the room back.
    /// </summary>
    [Fact]
    public async Task A_refused_attach_releases_the_room_it_had_reserved()
    {
        var quota = new FakeAttachmentQuotaStore();
        var store = new FakePurchaseStore();
        var purchase = store.AddPurchase(
            new PurchaseFileMetadata("already-stored.jpg", "image/jpeg", "first.jpg", 7));
        var documents = new FakeDocumentStorage();

        var result = await new AttachPurchaseDocument(
                store, documents, FakeAttachmentQuota.Enforcing(quota))
            .Handle(purchase.Id, File("receipt.jpg", 2_048), CancellationToken.None);

        Assert.Equal(AttachPurchaseDocumentOutcome.AlreadyHasDocument, result.Outcome);
        Assert.Equal(purchase.Id, Assert.Single(quota.Requests).RecordId);
        Assert.Single(quota.Released);
        Assert.Empty(quota.Outstanding);
        Assert.Single(documents.DeletedFileNames);
    }

    [Fact]
    public async Task An_expense_attachment_is_refused_when_the_business_is_full_and_nothing_is_written()
    {
        var quota = new FakeAttachmentQuotaStore { StoredBytes = FreeLimit };
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();

        var result = await new CreateOperatingExpense(
                store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.Enforcing(quota))
            .Handle(ExpenseFields(), Attachment("invoice.pdf", 10), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AttachmentStorageQuotaPolicy.StorageFullMessage, result.ValidationError);
        Assert.Empty(documents.SavedFileNames);
    }

    /// <summary>
    /// And an expense with no attachment is recorded whatever the storage says, which is the same
    /// rule the purchase has: a full business keeps bookkeeping.
    /// </summary>
    [Fact]
    public async Task An_expense_with_no_attachment_is_recorded_even_when_the_business_is_full()
    {
        var quota = new FakeAttachmentQuotaStore { StoredBytes = FreeLimit };
        var store = new FakeOperatingExpenseStore();

        var result = await new CreateOperatingExpense(
                store,
                new FakeDocumentStorage(),
                new FakeLocalMachineStore(),
                FakeAttachmentQuota.Enforcing(quota))
            .Handle(ExpenseFields(), attachment: null, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Empty(quota.Requests);
    }

    /// <summary>
    /// A replacement is reserved against the expense it replaces, so the figure the guard asks for
    /// is the net increase and the version the attachment was read at travels with it.
    /// </summary>
    [Fact]
    public async Task A_replacement_reserves_the_net_increase_against_the_attachments_version()
    {
        var quota = new FakeAttachmentQuotaStore
        {
            StoredBytes = 4_000,
            CurrentAttachmentBytes = 4_000,
            RecordVersion = 3,
        };
        var store = new FakeOperatingExpenseStore();
        var created = await new CreateOperatingExpense(
                store, new FakeDocumentStorage(), new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced())
            .Handle(ExpenseFields(), Attachment("old.pdf", 4_000), CancellationToken.None);

        var result = await new UpdateOperatingExpense(
                store, new FakeDocumentStorage(), new FakeLocalMachineStore(), FakeAttachmentQuota.Enforcing(quota))
            .Handle(created.Record!.Id, ExpenseFields(), Attachment("new.pdf", 4_500), CancellationToken.None);

        Assert.True(result.IsValid);
        var request = Assert.Single(quota.Requests);
        Assert.Equal(created.Record!.Id, request.RecordId);
        Assert.Equal(4_500, request.UploadBytes);
        Assert.Equal(500, store.LastReservation!.ChargeableBytes);
        Assert.Equal(3, store.LastReservation.RecordVersion);
    }

    /// <summary>
    /// The loser of two concurrent replacements: a conflict rather than a validation failure -
    /// nothing about the request was wrong - with the file removed and the room given back.
    /// </summary>
    [Fact]
    public async Task A_replacement_that_lost_the_race_is_a_conflict_and_leaves_nothing_behind()
    {
        var quota = new FakeAttachmentQuotaStore();
        var store = new FakeOperatingExpenseStore();
        var created = await new CreateOperatingExpense(
                store, new FakeDocumentStorage(), new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced())
            .Handle(ExpenseFields(), Attachment("old.pdf", 4_000), CancellationToken.None);
        store.ReplacementConflictOnUpdate = true;
        var documents = new FakeDocumentStorage();

        var result = await new UpdateOperatingExpense(
                store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.Enforcing(quota))
            .Handle(created.Record!.Id, ExpenseFields(), Attachment("new.pdf", 4_000), CancellationToken.None);

        Assert.True(result.IsConflict);
        Assert.Equal(AttachmentStorageQuotaPolicy.ReplacementConflictMessage, result.ConflictError);
        Assert.Single(documents.DeletedFileNames);
        Assert.Single(quota.Released);
        Assert.Empty(quota.Outstanding);
    }

    /// <summary>
    /// The expired-reservation refusal on the replacement path: refused with the storage message,
    /// the expense untouched, the file removed and the room released.
    /// </summary>
    [Fact]
    public async Task A_replacement_whose_expired_reservation_no_longer_fits_is_refused_and_cleaned_up()
    {
        var quota = new FakeAttachmentQuotaStore();
        var store = new FakeOperatingExpenseStore();
        var created = await new CreateOperatingExpense(
                store, new FakeDocumentStorage(), new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced())
            .Handle(ExpenseFields(), Attachment("old.pdf", 4_000), CancellationToken.None);
        store.QuotaExceededOnWrite = true;
        var documents = new FakeDocumentStorage();

        var result = await new UpdateOperatingExpense(
                store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.Enforcing(quota))
            .Handle(created.Record!.Id, ExpenseFields(), Attachment("new.pdf", 8_000), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.False(result.IsConflict);
        Assert.Equal(AttachmentStorageQuotaPolicy.StorageFullMessage, result.ValidationError);
        Assert.Single(documents.DeletedFileNames);
        Assert.Single(quota.Released);
    }

    /// <summary>
    /// The shipped state: with no plan enforced nothing is reserved at all, and every upload path
    /// behaves exactly as it did before quotas existed.
    /// </summary>
    [Fact]
    public async Task While_nothing_is_enforced_no_upload_path_reserves_anything()
    {
        var quota = new FakeAttachmentQuotaStore { StoredBytes = long.MaxValue / 2 };
        var purchases = new FakePurchaseStore();
        var expenses = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();

        var purchase = await TestUpload(purchases, documents, FakeAttachmentQuota.NotEnforced(quota))
            .Handle(File("receipt.jpg", 2_048), Fields(), [], CancellationToken.None);
        var expense = await new CreateOperatingExpense(
                expenses, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced(quota))
            .Handle(ExpenseFields(), Attachment("invoice.pdf", 2_048), CancellationToken.None);

        Assert.NotNull(purchase);
        Assert.True(expense.IsValid);
        Assert.Empty(quota.Requests);
        Assert.Null(purchases.LastCreateReservation);
        Assert.Null(expenses.LastReservation);
    }

    private static UploadPurchase Upload(
        FakePurchaseStore store, FakeDocumentStorage documents, FakeAttachmentQuotaStore quota) =>
        TestUpload(store, documents, FakeAttachmentQuota.Enforcing(quota));

    private static UploadPurchase TestUpload(
        FakePurchaseStore store, FakeDocumentStorage documents, AttachmentQuotaGuard quota) =>
        new(
            store,
            documents,
            FakeAuthenticatedActorAccessor.Unidentifiable(),
            new FakeClock(NowUtc),
            quota);

    private static PurchaseFileInput File(string fileName, long length) =>
        new(fileName, "image/jpeg", length, () => new MemoryStream([1, 2, 3]));

    private static PurchaseFields Fields() =>
        new("Weekly restock", null, null, null, null, NowUtc, null);

    private static OperatingExpenseFields ExpenseFields() => new(
        NowUtc, ExpenseCategory.Insurance, "Cover", 10m, 1m, 11m, null, null, null, null, null, null);

    private static ExpenseAttachmentInput Attachment(string fileName, long length) =>
        new(fileName, length, () => new MemoryStream([1, 2, 3]));
}
