using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Purchases;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Attachments;
using InventoryApi.Tests.Application.Expenses;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

/// <summary>
/// The purchase create's idempotency key, as the use case orchestrates it (issue #1247): what a
/// retry under one key does, what a different purchase under a spent key does, and what either
/// leaves in document storage.
///
/// <para>The race itself - two copies of one submission arriving together - is a claim about a real
/// write transaction and is proved by <c>PurchaseCreateConcurrencyTests</c> against SQLite. What
/// these prove is the orchestration either side of it.</para>
/// </summary>
public class UploadPurchaseIdempotencyTests
{
    private static readonly DateTime NowUtc = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    private const string Key = "7a0d1a9e-1f8c-4a5f-8a1f-2b3c4d5e6f70";

    [Fact]
    public async Task A_retry_under_the_same_key_returns_the_first_purchase_and_saves_nothing_more()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);

        var first = await useCase.Handle(FileInput(), Fields(), Items(), null, Key, CancellationToken.None);
        var retry = await useCase.Handle(FileInput(), Fields(), Items(), null, Key, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first!.Id, retry!.Id);
        // One purchase, and its document is the one the first attempt stored: the retry's bytes are
        // never written at all, because the key is read before storage is touched.
        Assert.Single(await store.ListAsync(null, CancellationToken.None));
        Assert.Single(documents.SavedFileNames);
        Assert.Equal(first.StoredFileName, retry.StoredFileName);
        Assert.Empty(documents.DeletedFileNames);
    }

    [Fact]
    public async Task The_same_key_with_different_details_is_refused_and_saves_nothing()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);
        await useCase.Handle(FileInput(), Fields(), Items(), null, Key, CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            useCase.Handle(FileInput(), Fields(totalAmount: 31m), Items(), null, Key, CancellationToken.None));

        Assert.Equal(PurchaseIdempotencyPolicy.KeyReusedMessage, refusal.Message);
        Assert.Single(await store.ListAsync(null, CancellationToken.None));
        Assert.Single(documents.SavedFileNames);
    }

    /// <summary>
    /// Every value a purchase carries is part of "the same request", so a second submission that
    /// differs in any of them is a conflict rather than a replay.
    /// </summary>
    [Theory]
    [InlineData("title")]
    [InlineData("notes")]
    [InlineData("invoiceNumber")]
    [InlineData("purchaseDate")]
    [InlineData("supplier")]
    [InlineData("lineQuantity")]
    [InlineData("lineUnitCost")]
    [InlineData("document")]
    public async Task One_changed_value_under_a_spent_key_is_a_conflict(string changed)
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var useCase = UseCase(store, documents);
        await useCase.Handle(FileInput(), Fields(), Items(), null, Key, CancellationToken.None);

        var file = changed == "document" ? FileInput("other.jpg") : FileInput();
        var fields = changed switch
        {
            "title" => Fields() with { Title = "Thursday restock" },
            "notes" => Fields() with { Notes = "left at the door" },
            "invoiceNumber" => Fields() with { InvoiceNumber = "INV-2" },
            "purchaseDate" => Fields() with { PurchaseDate = NowUtc.AddDays(-1) },
            "supplier" => Fields() with { SupplierId = 9 },
            _ => Fields(),
        };
        List<PurchaseItemInput> items = changed switch
        {
            "lineQuantity" => [new(3, 25m, 1.15m)],
            "lineUnitCost" => [new(3, 24m, 1.16m)],
            _ => Items(),
        };

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            useCase.Handle(file, fields, items, null, Key, CancellationToken.None));
        Assert.Single(await store.ListAsync(null, CancellationToken.None));
    }

    /// <summary>
    /// The key is normalised the way everything else submitted on this form is: " key " and "key"
    /// are one key, so whitespace a client adds cannot buy a second purchase.
    /// </summary>
    [Fact]
    public async Task A_key_is_matched_trimmed()
    {
        var store = new FakePurchaseStore();
        var useCase = UseCase(store, new FakeDocumentStorage());

        var first = await useCase.Handle(null, Fields(), Items(), null, Key, CancellationToken.None);
        var retry = await useCase.Handle(null, Fields(), Items(), null, $"  {Key} ", CancellationToken.None);

        Assert.Equal(first!.Id, retry!.Id);
        Assert.Single(await store.ListAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task An_over_long_key_is_refused_before_anything_is_written()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UseCase(store, documents).Handle(
                FileInput(),
                Fields(),
                Items(),
                null,
                new string('k', PurchaseIdempotencyPolicy.MaximumKeyLength + 1),
                CancellationToken.None));

        Assert.Equal(PurchaseIdempotencyPolicy.KeyTooLongMessage, refusal.Message);
        Assert.Empty(await store.ListAsync(null, CancellationToken.None));
        Assert.Empty(documents.SavedFileNames);
    }

    /// <summary>
    /// Backwards compatible: a create that sends no key - every client before this issue - claims
    /// nothing, and two identical submissions are two purchases exactly as they always were.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_create_with_no_key_behaves_as_it_always_has(string? submitted)
    {
        var store = new FakePurchaseStore();
        var useCase = UseCase(store, new FakeDocumentStorage());

        var first = await useCase.Handle(null, Fields(), Items(), null, submitted, CancellationToken.None);
        var second = await useCase.Handle(null, Fields(), Items(), null, submitted, CancellationToken.None);

        Assert.NotEqual(first!.Id, second!.Id);
        Assert.Equal(2, (await store.ListAsync(null, CancellationToken.None)).Count);
        Assert.Null(store.LastCreateIdempotency);
    }

    /// <summary>
    /// The claim the store is handed is the key as it will be stored, with the fingerprint of the
    /// request beside it - so the comparison at the write is made on the same two values the
    /// use case decided on.
    /// </summary>
    [Fact]
    public async Task The_store_is_handed_the_key_and_the_requests_fingerprint()
    {
        var store = new FakePurchaseStore();

        await UseCase(store, new FakeDocumentStorage())
            .Handle(null, Fields(), Items(), null, $" {Key} ", CancellationToken.None);

        Assert.NotNull(store.LastCreateIdempotency);
        Assert.Equal(Key, store.LastCreateIdempotency!.Key);
        Assert.Equal(64, store.LastCreateIdempotency.Fingerprint.Length);
    }

    /// <summary>
    /// A purchase whose date the request left to the server is still replayable: the fingerprint is
    /// made from the request as submitted, so the clock moving between the first attempt and the
    /// retry cannot turn a retry into a conflict.
    /// </summary>
    [Fact]
    public async Task A_retry_of_a_request_with_no_purchase_date_still_replays()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        var clock = new FakeClock(NowUtc);
        var useCase = UseCase(store, documents, clock);

        var first = await useCase.Handle(null, Fields(withPurchaseDate: false), Items(), null, Key, CancellationToken.None);
        clock.UtcNow = NowUtc.AddMinutes(5);
        var retry = await useCase.Handle(null, Fields(withPurchaseDate: false), Items(), null, Key, CancellationToken.None);

        Assert.Equal(first!.Id, retry!.Id);
        Assert.Equal(first.PurchaseDate, retry.PurchaseDate);
        Assert.Single(await store.ListAsync(null, CancellationToken.None));
    }

    /// <summary>
    /// When the store itself replays - the claim it makes inside its own write transaction, which is
    /// what a losing concurrent request hits - this attempt's bytes are removed again. They belong to
    /// a document no purchase names, and leaving them would hold storage for a purchase that was
    /// never saved; the saved purchase keeps its own document.
    /// </summary>
    [Fact]
    public async Task A_replay_at_the_write_removes_the_document_this_attempt_had_stored()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();
        // The purchase the winning request saved. It is invisible to the key lookup, so the use
        // case's own pre-check finds nothing and the replay happens at the write - which is exactly
        // the losing request's path.
        store.ReplayNextCreateWith = store.AddPurchase();

        var replayed = await UseCase(store, documents)
            .Handle(FileInput(), Fields(), Items(), null, Key, CancellationToken.None);

        Assert.NotNull(replayed);
        var stored = Assert.Single(documents.SavedFileNames);
        Assert.Equal(stored, Assert.Single(documents.DeletedFileNames));
        Assert.False(documents.Contains(DocumentCategory.PurchaseDocument, stored));
        Assert.Single(await store.ListAsync(null, CancellationToken.None));
    }

    private static UploadPurchase UseCase(
        IPurchaseStore store, IDocumentStorage documents, FakeClock? clock = null) =>
        new(
            store,
            documents,
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            clock ?? new FakeClock(NowUtc),
            FakeAttachmentQuota.NotEnforced());

    private static ActorIdentity Actor()
    {
        ActorIdentity.TryCreate(
            "22222222-2222-2222-2222-222222222222", "44444444-4444-4444-4444-444444444444", out var actor);
        return actor!;
    }

    private static PurchaseFields Fields(decimal? totalAmount = 30m, bool withPurchaseDate = true) =>
        new(
            "Weekly restock",
            null,
            totalAmount,
            null,
            null,
            withPurchaseDate ? new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            null,
            null,
            null,
            "INV-1");

    private static List<PurchaseItemInput> Items() => [new(3, 24m, 1.15m)];

    private static PurchaseFileInput FileInput(string fileName = "receipt.jpg") =>
        new(fileName, "image/jpeg", 3, () => new MemoryStream([1, 2, 3]));
}
