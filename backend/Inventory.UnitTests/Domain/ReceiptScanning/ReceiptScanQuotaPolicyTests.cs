using Inventory.Domain.ReceiptScanning;
using Inventory.Domain.Subscriptions;
using Xunit;

namespace InventoryApi.Tests.Domain.ReceiptScanning;

/// <summary>
/// The monthly receipt-scan cap rule (issue #1164, sub-issue C of #975), by value: which month an
/// instant's business date belongs to, when one more scan fits, what is left, and the sentence a
/// business that has used them all is told.
///
/// <para>Every case here is a pure function over values the caller supplies - there is no clock, no
/// database and no configuration in the policy, which is what lets the month boundary be tested as
/// arithmetic rather than by waiting for one.</para>
/// </summary>
public class ReceiptScanQuotaPolicyTests
{
    /// <summary>
    /// The cap the issue decided on 10 November 2026, read from the catalogue rather than restated:
    /// 100 scans per business per calendar month, a safety limit rather than a selling point.
    /// </summary>
    [Fact]
    public void The_catalogue_holds_the_hundred_scan_cap()
    {
        Assert.Equal(100, PlanCatalogue.MonthlyReceiptScanLimit);
    }

    /// <summary>
    /// A month key is the business date's own year and month, and nothing else: two dates in one
    /// month share a key, and the first day of the next month does not.
    /// </summary>
    [Theory]
    [InlineData(2026, 11, 1, 202611)]
    [InlineData(2026, 11, 30, 202611)]
    [InlineData(2026, 12, 1, 202612)]
    [InlineData(2026, 12, 31, 202612)]
    [InlineData(2027, 1, 1, 202701)]
    public void A_month_key_is_the_business_dates_year_and_month(int year, int month, int day, int expected)
    {
        Assert.Equal(expected, ReceiptScanQuotaPolicy.MonthKeyFor(new DateTime(year, month, day)));
    }

    /// <summary>
    /// The time of day is irrelevant: the key comes from the business <em>date</em>, so the last
    /// minute of a month and its first minute are the same month and different from each other's
    /// neighbours. Which date a UTC instant falls on is <c>IBusinessCalendar</c>'s answer in the
    /// business's own zone, never this policy's.
    /// </summary>
    [Fact]
    public void The_time_of_day_does_not_decide_the_month()
    {
        Assert.Equal(202611, ReceiptScanQuotaPolicy.MonthKeyFor(new DateTime(2026, 11, 30, 23, 59, 59)));
        Assert.Equal(202612, ReceiptScanQuotaPolicy.MonthKeyFor(new DateTime(2026, 12, 1, 0, 0, 0)));
    }

    /// <summary>
    /// The issue's first and second criteria as arithmetic: the 100th scan of a month fits and the
    /// 101st does not. The comparison is "used so far is below the cap", so the cap is the number of
    /// scans allowed rather than the number already made.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(98, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    [InlineData(101, false)]
    public void One_more_scan_fits_only_while_fewer_than_the_cap_have_been_made(int used, bool expected)
    {
        Assert.Equal(expected, ReceiptScanQuotaPolicy.Fits(used, PlanCatalogue.MonthlyReceiptScanLimit));
    }

    /// <summary>
    /// What is left is never negative. A business that somehow holds more than the cap - a cap
    /// lowered under it, which is the only way there is - reads zero remaining rather than a
    /// negative number a page would have to explain.
    /// </summary>
    [Theory]
    [InlineData(0, 100)]
    [InlineData(99, 1)]
    [InlineData(100, 0)]
    [InlineData(140, 0)]
    public void Remaining_is_the_cap_less_what_is_used_and_never_negative(int used, int expected)
    {
        Assert.Equal(expected, ReceiptScanQuotaPolicy.Remaining(used, PlanCatalogue.MonthlyReceiptScanLimit));
    }

    /// <summary>
    /// The issue's refusal sentence, with the cap substituted rather than typed twice: the message
    /// and the limit cannot disagree, because there is one number.
    /// </summary>
    [Fact]
    public void The_refusal_names_the_cap_it_was_refused_by()
    {
        Assert.Equal(
            "You have used all 100 receipt scans for this month.",
            ReceiptScanQuotaPolicy.LimitReachedMessage(PlanCatalogue.MonthlyReceiptScanLimit));
    }

    /// <summary>
    /// A month key is published as <c>yyyy-MM</c>, so a response says which month a count is about
    /// without a client having to divide a number by a hundred.
    /// </summary>
    [Theory]
    [InlineData(202601, "2026-01")]
    [InlineData(202611, "2026-11")]
    [InlineData(202712, "2027-12")]
    public void A_month_key_is_published_as_a_year_and_month(int monthKey, string expected)
    {
        Assert.Equal(expected, ReceiptScanQuotaPolicy.MonthLabel(monthKey));
    }
}
