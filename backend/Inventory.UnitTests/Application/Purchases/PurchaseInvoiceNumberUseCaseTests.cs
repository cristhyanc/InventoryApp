using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;
using InventoryApi.Tests.Application.Attachments;
using InventoryApi.Tests.Application.Expenses;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

/// <summary>
/// The purchase create and edit use cases' handling of the supplier's own receipt or invoice number
/// (issue #1162): what reaches the store, and what is refused before anything is written.
///
/// <para>Both use cases apply <see cref="PurchaseInvoiceNumberSubmission"/>, so the rules are
/// asserted on both paths rather than only on the one the form happens to use most.</para>
/// </summary>
public class PurchaseInvoiceNumberUseCaseTests
{
    private static readonly FakeClock Clock = new(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

    private static UploadPurchase CreateUseCase(IPurchaseStore store, IDocumentStorage documents) =>
        new(store, documents, FakeAuthenticatedActorAccessor.Identified(Operator()), Clock, FakeAttachmentQuota.NotEnforced());

    private static ActorIdentity Operator()
    {
        ActorIdentity.TryCreate(
            "22222222-2222-2222-2222-222222222222", "44444444-4444-4444-4444-444444444444", out var actor);
        return actor!;
    }

    private static PurchaseFields Fields(string? invoiceNumber) =>
        new("Weekly restock", null, null, null, null, null, null, InvoiceNumber: invoiceNumber);

    private static PurchaseFileInput FileInput() =>
        new("receipt.jpg", "image/jpeg", 3, () => new MemoryStream([1, 2, 3]));

    [Fact]
    public async Task A_created_purchase_records_the_invoice_number_trimmed()
    {
        var store = new FakePurchaseStore();

        var record = await CreateUseCase(store, new FakeDocumentStorage())
            .Handle(FileInput(), Fields(" INV-123 "), [], CancellationToken.None);

        Assert.Equal("INV-123", record!.InvoiceNumber);
        Assert.Equal("INV-123", store.LastCreated!.InvoiceNumber);
    }

    /// <summary>
    /// A purchase with nothing typed in the field records no invoice number at all, rather than an
    /// empty string a reader would have to tell apart from a missing one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_created_purchase_records_a_blank_invoice_number_as_none(string? submitted)
    {
        var store = new FakePurchaseStore();

        var record = await CreateUseCase(store, new FakeDocumentStorage())
            .Handle(FileInput(), Fields(submitted), [], CancellationToken.None);

        Assert.Null(record!.InvoiceNumber);
        Assert.Null(store.LastCreated!.InvoiceNumber);
    }

    [Fact]
    public async Task A_created_purchase_records_an_invoice_number_of_the_maximum_length()
    {
        var store = new FakePurchaseStore();
        var longest = new string('9', PurchaseInvoiceNumberPolicy.MaxLength);

        var record = await CreateUseCase(store, new FakeDocumentStorage())
            .Handle(FileInput(), Fields(longest), [], CancellationToken.None);

        Assert.Equal(longest, record!.InvoiceNumber);
    }

    /// <summary>
    /// An over-long invoice number is refused by name, and it is refused <em>before</em> the document
    /// is stored and before the purchase is written - so a refused request leaves no purchase, no
    /// line, no stock movement and no bytes in storage.
    /// </summary>
    [Fact]
    public async Task An_over_long_invoice_number_is_refused_on_create_and_stores_nothing()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var tooLong = new string('9', PurchaseInvoiceNumberPolicy.MaxLength + 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateUseCase(store, documents)
            .Handle(FileInput(), Fields(tooLong), [new PurchaseItemInput(1, 1, 1)], CancellationToken.None));

        Assert.Equal(PurchaseInvoiceNumberPolicy.TooLongMessage, error.Message);
        Assert.Null(store.LastCreated);
        Assert.Empty(documents.SavedFileNames);
    }

    /// <summary>
    /// The edit path refuses it before the store is reached at all, which is what keeps the edit's
    /// transaction from opening: the fake's <c>UpdateAsync</c> throws
    /// <see cref="NotSupportedException"/>, so an <see cref="InvalidOperationException"/> carrying the
    /// policy's own message is the proof that nothing was handed on.
    /// </summary>
    [Fact]
    public async Task An_over_long_invoice_number_is_refused_on_edit_before_the_store_is_reached()
    {
        var store = new FakePurchaseStore();
        var tooLong = new string('9', PurchaseInvoiceNumberPolicy.MaxLength + 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new UpdatePurchase(store)
            .Handle(7, Fields(tooLong), null, CancellationToken.None));

        Assert.Equal(PurchaseInvoiceNumberPolicy.TooLongMessage, error.Message);
    }

    /// <summary>
    /// What the edit hands the store is the trimmed number, and nothing for a cleared field - the
    /// same normalisation the create applies, from the one place both call.
    /// </summary>
    [Theory]
    [InlineData(" INV-123 ", "INV-123")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void An_edited_invoice_number_reaches_the_store_normalised(string? submitted, string? stored) =>
        Assert.Equal(stored, PurchaseInvoiceNumberSubmission.Normalized(Fields(submitted)).InvoiceNumber);

    /// <summary>Nothing else about the submitted fields is touched on the way through.</summary>
    [Fact]
    public void Normalising_the_invoice_number_changes_no_other_field()
    {
        var fields = new PurchaseFields(
            "Weekly restock", " a note ", 30m, 5m, 2m, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), 4,
            InvoiceNumber: " INV-123 ");

        var normalised = PurchaseInvoiceNumberSubmission.Normalized(fields);

        Assert.Equal(fields with { InvoiceNumber = "INV-123" }, normalised);
    }
}
