using System.Globalization;

namespace Inventory.Domain.ReceiptScanning;

/// <summary>
/// The one monthly receipt-scan cap rule (issue #1164, sub-issue C of #975, the cap decided
/// 10 November 2026).
///
/// <para><strong>Why there is a cap at all.</strong> Every scan spends real money at an outside
/// reader - about US$0.04 on the model the 10 November receipt test measured - so a business with no
/// ceiling is an open-ended bill. A hundred scans per business per calendar month holds that at about
/// US$3.80 per business per month, which is a <em>safety limit</em> rather than a selling point: it is
/// far above what a vending business records in a month, and nothing in the product is priced on it.
/// The number itself lives in <c>Inventory.Domain.Subscriptions.PlanCatalogue</c> beside the storage
/// allowance, compiled rather than configured, and is never restated here.</para>
///
/// <para><strong>A month is the business's own calendar month.</strong> The key comes from a business
/// <em>date</em> - which <c>Inventory.Application.Time.IBusinessCalendar</c> derives in the business's
/// own configured zone, <c>Australia/Sydney</c> for the business this application runs for - so a scan
/// made at 11pm on the 30th and one made an hour later belong to different months exactly when the
/// business's own calendar says so, and no server-local or UTC month boundary is involved
/// (AGENTS.md § Reporting dates and filters). The policy itself holds no clock and no zone: every
/// instant it is asked about arrives as a date the caller resolved.</para>
///
/// <para>It is deterministic and total: no clock, no database, no configuration.</para>
/// </summary>
public static class ReceiptScanQuotaPolicy
{
    /// <summary>
    /// The multiple a month key packs a year into, so <c>2026-11</c> is <c>202611</c>. An integer
    /// rather than text because it is a stored key under a unique index and an ordering, and
    /// <c>yyyy-MM</c> text would order correctly only by accident of the format.
    /// </summary>
    private const int YearMultiplier = 100;

    /// <summary>
    /// Which calendar month <paramref name="businessDate"/> belongs to, as <c>year * 100 + month</c>.
    /// The time of day is ignored: the caller has already decided which business date the instant
    /// fell on.
    /// </summary>
    public static int MonthKeyFor(DateTime businessDate) =>
        (businessDate.Year * YearMultiplier) + businessDate.Month;

    /// <summary>
    /// Whether one more scan fits, given how many have been counted this month and the cap. The
    /// comparison is strict, so the cap is the number of scans a month allows rather than the number
    /// already made: with 99 counted the next scan runs, and with 100 counted it does not.
    /// </summary>
    /// <param name="used">How many scans have been counted in the month, from the stored counter.</param>
    /// <param name="limit">The cap, from the catalogue.</param>
    public static bool Fits(int used, int limit) => used < limit;

    /// <summary>
    /// How many scans are left in the month. Never negative: a business holding more than the cap -
    /// which only a cap lowered under it could produce - has none left rather than a negative number
    /// a page would have to explain away.
    /// </summary>
    public static int Remaining(int used, int limit) => limit - used > 0 ? limit - used : 0;

    /// <summary>
    /// What a business that has used its whole month's allowance is told. The cap is substituted
    /// rather than typed, so the sentence and the number enforced cannot drift apart.
    /// </summary>
    public static string LimitReachedMessage(int limit) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "You have used all {0} receipt scans for this month.",
            limit);

    /// <summary>
    /// A month key as <c>yyyy-MM</c>, which is how a response says which month a count is about.
    /// </summary>
    public static string MonthLabel(int monthKey) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0:D4}-{1:D2}",
            monthKey / YearMultiplier,
            monthKey % YearMultiplier);
}
