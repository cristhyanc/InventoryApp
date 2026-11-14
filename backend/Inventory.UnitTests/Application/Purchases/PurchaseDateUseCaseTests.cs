using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Application.Time;
using Inventory.Domain.Purchases;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Attachments;
using InventoryApi.Tests.Application.Expenses;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

/// <summary>
/// The purchase create and edit use cases' refusal of a date the business has not reached yet
/// (issue #1246).
///
/// <para>Both use cases apply <see cref="PurchaseDateSubmission"/> over
/// <see cref="PurchaseDatePolicy"/>, so the rule is asserted on both paths: a create that would
/// store the future date, and an edit - including one that submits no date at all and would
/// therefore leave an existing future-dated purchase standing.</para>
///
/// <para>The business calendar here is a fake whose business date is the UTC date; the real
/// Australia/Sydney boundary, where the two differ, is
/// <c>InventoryApi.Tests.Services.PurchaseFutureDateTests</c>.</para>
/// </summary>
public class PurchaseDateUseCaseTests
{
    private static readonly DateTime NowUtc = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
    private static readonly FakeClock Clock = new(NowUtc);
    private static readonly DateTime Today = new(2026, 11, 14);

    private static UploadPurchase CreateUseCase(
        IPurchaseStore store, IDocumentStorage documents, IBusinessCalendar? calendar = null) =>
        new(
            store,
            documents,
            FakeAuthenticatedActorAccessor.Identified(Operator()),
            Clock,
            calendar ?? new FakeBusinessCalendar(Today),
            FakeAttachmentQuota.NotEnforced());

    private static UpdatePurchase EditUseCase(IPurchaseStore store, IBusinessCalendar? calendar = null) =>
        new(store, calendar ?? new FakeBusinessCalendar(Today));

    private static ActorIdentity Operator()
    {
        ActorIdentity.TryCreate(
            "22222222-2222-2222-2222-222222222222", "44444444-4444-4444-4444-444444444444", out var actor);
        return actor!;
    }

    private static PurchaseFields Fields(DateTime? purchaseDate) =>
        new("Weekly restock", null, null, null, null, purchaseDate, null);

    private static PurchaseFileInput FileInput() =>
        new("receipt.jpg", "image/jpeg", 3, () => new MemoryStream([1, 2, 3]));

    /// <summary>
    /// The case the issue is about: a date a week out is refused by name, and refused
    /// <em>before</em> the document is stored and before the purchase is written - so nothing at all
    /// is left behind for the operator to clean up.
    /// </summary>
    [Fact]
    public async Task A_purchase_dated_after_today_is_refused_on_create_and_stores_nothing()
    {
        var store = new FakePurchaseStore();
        var documents = new FakeDocumentStorage();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateUseCase(store, documents)
            .Handle(FileInput(), Fields(Today.AddDays(7)), [new PurchaseItemInput(1, 12m, 1m)], CancellationToken.None));

        Assert.Equal(PurchaseDatePolicy.AfterTodayMessage, error.Message);
        Assert.Null(store.LastCreated);
        Assert.Empty(documents.SavedFileNames);
    }

    /// <summary>Tomorrow is the first refused day, and the refusal does not depend on the gap's size.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(365)]
    public async Task Any_date_later_than_today_is_refused_on_create(int deltaDays)
    {
        var store = new FakePurchaseStore();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateUseCase(store, new FakeDocumentStorage())
            .Handle(FileInput(), Fields(Today.AddDays(deltaDays)), [], CancellationToken.None));

        Assert.Equal(PurchaseDatePolicy.AfterTodayMessage, error.Message);
        Assert.Null(store.LastCreated);
    }

    /// <summary>
    /// Today and every earlier date are saved exactly as before, including a time of day later than
    /// the clock's own: the rule is about the business day, not about the instant.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-400)]
    public async Task Today_and_earlier_dates_are_created(int deltaDays)
    {
        var store = new FakePurchaseStore();
        var submitted = Today.AddDays(deltaDays).AddHours(22);

        var record = await CreateUseCase(store, new FakeDocumentStorage())
            .Handle(FileInput(), Fields(submitted), [], CancellationToken.None);

        Assert.NotNull(record);
        Assert.Equal(submitted, record!.PurchaseDate);
    }

    /// <summary>
    /// A request that submits no date at all defaults to the clock's instant (issue #310), which is
    /// today in every calendar the clock can be read in, so the default is never refused.
    /// </summary>
    [Fact]
    public async Task A_purchase_with_no_submitted_date_is_created_at_the_clocks_instant()
    {
        var store = new FakePurchaseStore();

        var record = await CreateUseCase(store, new FakeDocumentStorage())
            .Handle(FileInput(), Fields(null), [], CancellationToken.None);

        Assert.Equal(NowUtc, record!.PurchaseDate);
    }

    /// <summary>
    /// The edit path refuses it before the store is reached at all, which is what keeps the edit's
    /// transaction from opening: the fake's <c>UpdateAsync</c> throws
    /// <see cref="NotSupportedException"/>, so an <see cref="InvalidOperationException"/> carrying the
    /// policy's own message is the proof that nothing was handed on.
    /// </summary>
    [Fact]
    public async Task A_submitted_date_after_today_is_refused_on_edit_before_the_store_is_reached()
    {
        var store = new FakePurchaseStore();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => EditUseCase(store)
            .Handle(7, Fields(Today.AddDays(1)), null, CancellationToken.None));

        Assert.Equal(PurchaseDatePolicy.AfterTodayMessage, error.Message);
    }

    /// <summary>
    /// The decision @cristhyanc made on 14 November 2026: <em>every</em> save needs a date of today
    /// or earlier. An edit of an existing future-dated purchase that submits no date would leave
    /// that date standing, so it is refused too - correcting the date is the only way to save it.
    /// </summary>
    [Fact]
    public async Task An_edit_that_submits_no_date_is_refused_while_the_stored_date_is_after_today()
    {
        var store = new FakePurchaseStore();
        var stored = store.AddPurchase(purchaseDate: Today.AddDays(7));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => EditUseCase(store)
            .Handle(stored.Id, Fields(null), null, CancellationToken.None));

        Assert.Equal(PurchaseDatePolicy.AfterTodayMessage, error.Message);
    }

    /// <summary>
    /// The same edit with the date corrected passes the rule and reaches the store, which the fake
    /// reports by throwing <see cref="NotSupportedException"/> - the real save is
    /// <c>InventoryApi.Tests.Services.PurchaseFutureDateTests</c>.
    /// </summary>
    [Fact]
    public async Task An_edit_of_a_future_dated_purchase_reaches_the_store_once_its_date_is_corrected()
    {
        var store = new FakePurchaseStore();
        var stored = store.AddPurchase(purchaseDate: Today.AddDays(7));

        await Assert.ThrowsAsync<NotSupportedException>(() => EditUseCase(store)
            .Handle(stored.Id, Fields(Today), null, CancellationToken.None));
    }

    /// <summary>
    /// An ordinary edit of an ordinary purchase is unchanged: no date submitted, a stored date of
    /// today or earlier, and the edit goes straight to the store.
    /// </summary>
    [Fact]
    public async Task An_edit_that_submits_no_date_reaches_the_store_when_the_stored_date_is_not_after_today()
    {
        var store = new FakePurchaseStore();
        var stored = store.AddPurchase(purchaseDate: Today.AddDays(-3));

        await Assert.ThrowsAsync<NotSupportedException>(() => EditUseCase(store)
            .Handle(stored.Id, Fields(null), null, CancellationToken.None));
    }

    /// <summary>
    /// A purchase that does not exist has no stored date to judge, so the edit still reaches the
    /// store and the store's own "not found" is what the caller is told - a <c>404</c>, not a date
    /// refusal.
    /// </summary>
    [Fact]
    public async Task An_edit_of_a_purchase_that_does_not_exist_still_reaches_the_store()
    {
        var store = new FakePurchaseStore();

        await Assert.ThrowsAsync<NotSupportedException>(() => EditUseCase(store)
            .Handle(404, Fields(null), null, CancellationToken.None));
    }
}
