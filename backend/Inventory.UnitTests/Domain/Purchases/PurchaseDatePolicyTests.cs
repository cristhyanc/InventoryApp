using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

/// <summary>
/// The rule that a purchase is never dated later than the business has got to (issue #1246). The
/// policy compares two calendar dates the caller has already derived in the business's own time
/// zone; which zone that is, and how an instant becomes a date in it, is
/// <c>Inventory.Application.Time.IBusinessCalendar</c>'s work.
/// </summary>
public class PurchaseDatePolicyTests
{
    private static readonly DateTime Today = new(2026, 11, 14);

    [Fact]
    public void Tomorrow_is_after_today() =>
        Assert.True(PurchaseDatePolicy.IsAfterToday(Today.AddDays(1), Today));

    [Fact]
    public void Today_is_not_after_today() =>
        Assert.False(PurchaseDatePolicy.IsAfterToday(Today, Today));

    [Theory]
    [InlineData(-1)]
    [InlineData(-30)]
    [InlineData(-400)]
    public void An_earlier_date_is_not_after_today(int deltaDays) =>
        Assert.False(PurchaseDatePolicy.IsAfterToday(Today.AddDays(deltaDays), Today));

    /// <summary>
    /// The comparison is between calendar days, so a time of day on either side never turns today
    /// into tomorrow: a purchase dated later today is today's purchase, and the last moment of
    /// today is still not after today.
    /// </summary>
    [Fact]
    public void A_time_of_day_on_either_side_does_not_make_a_date_later()
    {
        Assert.False(PurchaseDatePolicy.IsAfterToday(Today.AddHours(23).AddMinutes(59), Today));
        Assert.False(PurchaseDatePolicy.IsAfterToday(Today, Today.AddHours(9)));
        Assert.True(PurchaseDatePolicy.IsAfterToday(Today.AddDays(1), Today.AddHours(23).AddMinutes(59)));
    }

    /// <summary>The kind is irrelevant: both sides are already business calendar dates.</summary>
    [Fact]
    public void The_datetime_kind_does_not_change_the_answer()
    {
        Assert.True(PurchaseDatePolicy.IsAfterToday(
            DateTime.SpecifyKind(Today.AddDays(1), DateTimeKind.Utc), Today));
        Assert.False(PurchaseDatePolicy.IsAfterToday(
            DateTime.SpecifyKind(Today, DateTimeKind.Utc),
            DateTime.SpecifyKind(Today, DateTimeKind.Local)));
    }
}
