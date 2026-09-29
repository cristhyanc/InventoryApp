using System.ComponentModel;
using System.Globalization;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Audit for issue #218 (phase 3 of #213): the Sync Restock <c>fromDate</c> query parameter binds
/// through ASP.NET Core's <c>SimpleTypeModelBinder</c>, which resolves a value via
/// <see cref="TypeDescriptor.GetConverter(Type)"/> for the parameter's declared type - the exact
/// conversion exercised directly here, without a full HTTP/hosting pipeline. The frontend always
/// sends a <c>Z</c>-suffixed UTC ISO instant (issue #218's <c>startOfDayUtc</c>), so this proves the
/// binding preserves that exact instant regardless of the server process's local time zone
/// (<see cref="TimeZoneInfo.Local"/>), rather than reinterpreting it - which is why
/// <c>MachinesController.SyncRestock</c> declares <c>fromDate</c> as <see cref="DateTimeOffset"/>,
/// not a plain <see cref="DateTime"/>.
/// </summary>
public class SyncRestockFromDateQueryBindingTests
{
    private static DateTime BindDateTimeAsQueryModelBinderWould(string queryValue)
    {
        var converter = TypeDescriptor.GetConverter(typeof(DateTime));
        return (DateTime)converter.ConvertFrom(null, CultureInfo.InvariantCulture, queryValue)!;
    }

    private static DateTimeOffset BindDateTimeOffsetAsQueryModelBinderWould(string queryValue)
    {
        var converter = TypeDescriptor.GetConverter(typeof(DateTimeOffset));
        return (DateTimeOffset)converter.ConvertFrom(null, CultureInfo.InvariantCulture, queryValue)!;
    }

    /// <summary>
    /// The historical bug this issue fixes: a plain <c>DateTime</c> query parameter's default
    /// .NET conversion reinterprets a <c>Z</c>-suffixed UTC instant against
    /// <see cref="TimeZoneInfo.Local"/>, shifting its ticks whenever the server process's local
    /// time zone is not UTC - which the <see cref="DateTimeKind"/>-agnostic
    /// <c>EventDateTimeGmt &gt;= fromDate</c> comparison would then silently compare incorrectly.
    /// This is exactly the class of server-local date shift <c>docs/architecture.md</c> prohibits.
    /// </summary>
    [Fact]
    public void A_plain_DateTime_query_conversion_is_not_safe_for_a_utc_instant()
    {
        var utcInstant = new DateTime(2026, 8, 31, 14, 0, 0, DateTimeKind.Utc);

        var bound = BindDateTimeAsQueryModelBinderWould("2026-08-31T14:00:00.000Z");

        // Kind is Local rather than Utc: .NET's default DateTime conversion converts the instant to
        // the server process's local wall-clock time, shifting its ticks by TimeZoneInfo.Local's
        // offset - zero only when the server process happens to run in UTC. That shifted value is
        // exactly what the DateTimeKind-agnostic EventDateTimeGmt >= fromDate comparison would then
        // compare, silently, against every other server time zone. DateTimeOffset avoids this below.
        Assert.Equal(DateTimeKind.Local, bound.Kind);
        Assert.Equal(utcInstant, bound.ToUniversalTime());
    }

    [Fact]
    public void A_DateTimeOffset_query_conversion_preserves_the_exact_instant_and_offset()
    {
        var expectedUtc = new DateTime(2026, 8, 31, 14, 0, 0, DateTimeKind.Utc);

        var bound = BindDateTimeOffsetAsQueryModelBinderWould("2026-08-31T14:00:00.000Z");

        Assert.Equal(expectedUtc, bound.UtcDateTime);
        Assert.Equal(DateTimeKind.Utc, bound.UtcDateTime.Kind);
        Assert.Equal(TimeSpan.Zero, bound.Offset);
    }

    [Fact]
    public void A_DateTimeOffset_query_conversion_during_aedt_preserves_the_exact_instant()
    {
        // 2026-01-15 Canberra midnight (AEDT, UTC+11) as computed by the frontend's startOfDayUtc.
        var expectedUtc = new DateTime(2026, 1, 14, 13, 0, 0, DateTimeKind.Utc);

        var bound = BindDateTimeOffsetAsQueryModelBinderWould("2026-01-14T13:00:00.000Z");

        Assert.Equal(expectedUtc, bound.UtcDateTime);
    }
}
