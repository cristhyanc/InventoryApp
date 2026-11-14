namespace Inventory.Domain.Purchases;

/// <summary>
/// Guards the inventory invariant that a purchase is never dated later than the business has got to
/// (issue #1246).
///
/// <para>A purchase's date is the instant its restock movement is replayed at, so a date the
/// business has not reached yet adds stock the replay orders <em>after</em> everything that happens
/// between now and then: the quantity shows on the shelf immediately, but today's refill or
/// write-off of it is refused as physically negative, and no ordinary screen can repair that other
/// than by correcting the date. A slip on the date picker, or a misread receipt date, must therefore
/// be refused at the save rather than recorded.</para>
///
/// <para>Both sides of the comparison are calendar dates in the business's own time zone, which is
/// <c>Inventory.Application.Time.IBusinessCalendar</c>'s work (AGENTS.md § Reporting dates and
/// filters): the host's own date is a different day from the business's for part of every day. This
/// policy only compares the two days it is handed, so it stays deterministic and zone-free.</para>
/// </summary>
public static class PurchaseDatePolicy
{
    /// <summary>The refusal the operator sees, on a create and on an edit alike.</summary>
    public const string AfterTodayMessage =
        "Purchase date cannot be later than today. Correct the date and save again.";

    /// <summary>
    /// Whether <paramref name="purchaseBusinessDate"/> falls on a later business day than
    /// <paramref name="businessToday"/>. A time of day on either side is ignored: today's purchase
    /// is today's whatever hour it names.
    /// </summary>
    public static bool IsAfterToday(DateTime purchaseBusinessDate, DateTime businessToday) =>
        purchaseBusinessDate.Date > businessToday.Date;
}
