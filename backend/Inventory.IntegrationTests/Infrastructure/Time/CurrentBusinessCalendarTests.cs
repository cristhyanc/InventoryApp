using Inventory.Application.Time;
using Inventory.Infrastructure.Time;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Time;

/// <summary>
/// The per-request business calendar's resolution rule (issue #499): it derives its dates in the
/// zone the request resolved, and when there is no usable zone it derives none at all.
///
/// The fail-closed half is the one that matters most. A business date silently decided by
/// <c>Australia/Sydney</c>, by the host's own zone or by UTC moves sales, fees, commissions and
/// profit between days without anything looking wrong, so every member of the port has to refuse
/// rather than answer. The <see cref="ZonedBusinessCalendar"/> behaviour itself is covered by
/// <see cref="ZonedBusinessCalendarTests"/>; what is under test here is only which zone - if any -
/// this type uses.
/// </summary>
public class CurrentBusinessCalendarTests
{
    private static readonly DateTime NowUtc = new(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc);

    private static CurrentBusinessCalendar CalendarFor(string? resolvedTimeZoneId)
    {
        var scope = new BusinessTimeZoneScope();
        if (resolvedTimeZoneId is not null)
        {
            scope.Resolve(resolvedTimeZoneId);
        }

        return new CurrentBusinessCalendar(new FakeClock(NowUtc), scope);
    }

    [Fact]
    public void It_derives_business_dates_in_the_zone_the_request_resolved()
    {
        var sydney = CalendarFor("Australia/Sydney");
        var newYork = CalendarFor("America/New_York");

        // One instant, two businesses, two different business dates - each its own.
        Assert.Equal(new DateTime(2026, 7, 15), sydney.Today);
        Assert.Equal(new DateTime(2026, 7, 14), newYork.Today);
        Assert.Equal(new DateTime(2026, 7, 15), sydney.ToBusinessDate(NowUtc));
        Assert.Equal(new DateTime(2026, 7, 14), newYork.ToBusinessDate(NowUtc));
        Assert.Equal(new DateTime(2026, 7, 14, 14, 0, 0, DateTimeKind.Utc),
            sydney.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));
        Assert.Equal(new DateTime(2026, 7, 15, 4, 0, 0, DateTimeKind.Utc),
            newYork.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));
    }

    [Fact]
    public void With_no_resolved_business_every_member_fails_closed()
    {
        var calendar = CalendarFor(null);

        Assert.Throws<BusinessTimeZoneUnavailableException>(() => calendar.Today);
        Assert.Throws<BusinessTimeZoneUnavailableException>(() => calendar.ToBusinessDate(NowUtc));
        Assert.Throws<BusinessTimeZoneUnavailableException>(
            () => calendar.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));
    }

    /// <summary>
    /// A stored id the host's time-zone database cannot resolve is the same situation as no zone at
    /// all, and must not quietly become Sydney, the host's zone or UTC.
    /// </summary>
    [Theory]
    [InlineData("Australia/Sydeny")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("AUS Eastern Standard Time")]
    public void With_an_unresolvable_stored_zone_every_member_fails_closed(string storedTimeZoneId)
    {
        var calendar = CalendarFor(storedTimeZoneId);

        var failure = Assert.Throws<BusinessTimeZoneUnavailableException>(() => calendar.Today);
        Assert.Equal(storedTimeZoneId, failure.TimeZoneId);
        Assert.Throws<BusinessTimeZoneUnavailableException>(() => calendar.ToBusinessDate(NowUtc));
        Assert.Throws<BusinessTimeZoneUnavailableException>(
            () => calendar.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));
    }

    /// <summary>
    /// The resolution is memoised per request, so several use cases in one request cannot end up in
    /// different calendars and the platform lookup happens once. The zone can only be published
    /// before the first derivation; a scope that publishes one afterwards is refused by
    /// <see cref="BusinessTimeZoneScope"/> itself.
    /// </summary>
    [Fact]
    public void A_resolved_zone_cannot_be_replaced_later_in_the_request()
    {
        var scope = new BusinessTimeZoneScope();
        scope.Resolve("Australia/Sydney");

        Assert.Throws<InvalidOperationException>(() => scope.Resolve("America/New_York"));
        Assert.Equal("Australia/Sydney", scope.TimeZoneId);
    }
}
