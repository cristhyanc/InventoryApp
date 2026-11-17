using Inventory.Application.Purchases;
using Inventory.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

/// <summary>
/// The "this looks like a purchase you already recorded" warning (issue #1247): when it asks the
/// question at all, what day it asks about, and that it changes nothing either way.
/// </summary>
public class FindPossibleDuplicatePurchaseTests
{
    private static readonly DateTime Entered = new(2026, 4, 1, 22, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_matching_purchase_is_reported()
    {
        var store = new FakePurchaseStore
        {
            PossibleDuplicate = new PurchaseDuplicateMatch(7, "Weekly restock", Entered.AddHours(-3)),
        };

        var match = await UseCase(store).Handle(4, " INV-1 ", Entered, CancellationToken.None);

        Assert.Equal(7, match!.PurchaseId);
        // Matched on the value the purchase would be stored with, so " INV-1 " recognises "INV-1".
        Assert.Equal("INV-1", store.LastDuplicateLookup!.InvoiceNumber);
        Assert.Equal(4, store.LastDuplicateLookup.SupplierId);
    }

    /// <summary>
    /// The day is the business's own, asked as a half-open UTC range rather than by comparing two
    /// instants: a purchase entered at 9am and the same receipt entered again at 11pm have to be
    /// the same day's question.
    /// </summary>
    [Fact]
    public async Task The_lookup_covers_the_whole_business_day_of_the_entered_date()
    {
        var store = new FakePurchaseStore();
        // A calendar whose day starts ten hours before UTC midnight, the way a +10 business's does.
        var calendar = new ShiftedBusinessCalendar(TimeSpan.FromHours(10));

        await UseCase(store, calendar).Handle(4, "INV-1", Entered, CancellationToken.None);

        var lookup = store.LastDuplicateLookup!;
        Assert.Equal(calendar.StartOfBusinessDayUtc(calendar.ToBusinessDate(Entered)), lookup.FromUtc);
        Assert.Equal(lookup.FromUtc.AddDays(1), lookup.ToUtc);
        Assert.True(lookup.FromUtc <= Entered && Entered < lookup.ToUtc);
    }

    /// <summary>
    /// Nothing to recognise a repeat by is no question at all: the store is never asked, so a
    /// purchase with no supplier or no invoice number never produces a warning - which is what
    /// keeps the warning worth reading on the purchases that do carry both.
    /// </summary>
    [Theory]
    [InlineData(null, "INV-1")]
    [InlineData(4, null)]
    [InlineData(4, "")]
    [InlineData(4, "   ")]
    public async Task Without_a_supplier_and_an_invoice_number_nothing_is_asked(int? supplierId, string? invoiceNumber)
    {
        var store = new FakePurchaseStore
        {
            PossibleDuplicate = new PurchaseDuplicateMatch(7, "Weekly restock", Entered),
        };

        var match = await UseCase(store).Handle(supplierId, invoiceNumber, Entered, CancellationToken.None);

        Assert.Null(match);
        Assert.Null(store.LastDuplicateLookup);
    }

    [Fact]
    public async Task Without_a_date_nothing_is_asked()
    {
        var store = new FakePurchaseStore
        {
            PossibleDuplicate = new PurchaseDuplicateMatch(7, "Weekly restock", Entered),
        };

        var match = await UseCase(store).Handle(4, "INV-1", null, CancellationToken.None);

        Assert.Null(match);
        Assert.Null(store.LastDuplicateLookup);
    }

    private static FindPossibleDuplicatePurchase UseCase(
        FakePurchaseStore store, ShiftedBusinessCalendar? calendar = null) =>
        new(store, calendar ?? new ShiftedBusinessCalendar(TimeSpan.Zero));

    /// <summary>
    /// A calendar whose day starts <paramref name="offset"/> before UTC midnight, which is what a
    /// business east of UTC has. The real one is <c>ZonedBusinessCalendar</c>, covered by its own
    /// tests; what matters here is that this use case asks for a day rather than for an instant.
    /// </summary>
    private sealed class ShiftedBusinessCalendar(TimeSpan offset) : IBusinessCalendar
    {
        public DateTime Today => ToBusinessDate(DateTime.UtcNow);

        public DateTime ToBusinessDate(DateTime utcInstant) => (utcInstant + offset).Date;

        public DateTime StartOfBusinessDayUtc(DateTime businessDate) =>
            DateTime.SpecifyKind(businessDate.Date - offset, DateTimeKind.Utc);
    }
}
