using Inventory.Application.Time;
using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>
/// The one place the purchase create and edit use cases refuse a purchase dated later than today
/// (issue #1246).
///
/// It runs before any state changes: before <see cref="UploadPurchase"/> saves the uploaded
/// document, and before <see cref="UpdatePurchase"/> hands the edit to the store's transaction. So a
/// future-dated save - which the date picker could offer and a misread receipt date could fill in -
/// leaves the purchase, its lines, its stock movements and its stored document exactly as they were.
///
/// The decision itself is the Domain's (<see cref="PurchaseDatePolicy"/>) and the calendar the
/// business keeps is <see cref="IBusinessCalendar"/>'s (AGENTS.md § Reporting dates and filters);
/// this only puts the two together from both paths, so the create and the edit cannot drift apart.
/// </summary>
public static class PurchaseDateSubmission
{
    /// <summary>
    /// Throws when <paramref name="purchaseDate"/> falls on a later day than today in the current
    /// business's own time zone. The instant is converted through the calendar rather than compared
    /// to the host's clock, because for part of every day the host's date and the business's are
    /// different calendar days.
    /// </summary>
    /// <exception cref="InvalidOperationException">The purchase date is later than today.</exception>
    public static void EnsureNotAfterToday(DateTime purchaseDate, IBusinessCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        if (PurchaseDatePolicy.IsAfterToday(calendar.ToBusinessDate(purchaseDate), calendar.Today))
            throw new InvalidOperationException(PurchaseDatePolicy.AfterTodayMessage);
    }
}
