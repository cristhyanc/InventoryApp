using Inventory.Infrastructure.Time;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Time;

/// <summary>
/// What counts as a usable business time zone (issue #499).
///
/// This is the single decision the write path and the read path share: an id this resolver accepts
/// is an id a business can be stored with, and an id a business is stored with is one a calendar
/// can be built from. The cases below are the ones that would otherwise be decided by accident -
/// a blank value, an unknown id, and a Windows id that would resolve on one host's time-zone
/// database and not on another's.
/// </summary>
public class BusinessTimeZonesTests
{
    [Theory]
    [InlineData("Australia/Sydney")]
    [InlineData("America/New_York")]
    [InlineData("Europe/London")]
    [InlineData("Pacific/Auckland")]
    [InlineData("Etc/UTC")]
    [InlineData("UTC")]
    public void An_IANA_id_the_platform_knows_resolves(string ianaId)
    {
        Assert.True(BusinessTimeZones.TryResolve(ianaId, out var timeZone));
        Assert.NotNull(timeZone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Australia/Sydeny")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("+10:00")]
    public void A_blank_or_unknown_id_does_not_resolve_and_nothing_is_substituted(string? id)
    {
        Assert.False(BusinessTimeZones.TryResolve(id, out _));
        Assert.Throws<TimeZoneNotFoundException>(() => BusinessTimeZones.Resolve(id));
    }

    /// <summary>
    /// The stored contract is an IANA id so that one database behaves identically on a Linux and a
    /// Windows host. A Windows id is therefore refused on both, rather than being accepted on the
    /// host that happens to understand it and failing closed on the other.
    /// </summary>
    [Theory]
    [InlineData("AUS Eastern Standard Time")]
    [InlineData("Eastern Standard Time")]
    public void A_Windows_time_zone_id_is_refused_even_where_the_platform_could_resolve_it(string windowsId)
    {
        Assert.False(BusinessTimeZones.TryResolve(windowsId, out _));
    }

    /// <summary>
    /// The cross-platform fallback the Sydney-only calendar introduced, kept for every zone: a
    /// host that cannot resolve IANA ids resolves the mapped Windows id instead, so the same
    /// stored id works on both.
    /// </summary>
    [Theory]
    [InlineData("Australia/Sydney")]
    [InlineData("America/New_York")]
    public void Every_supported_zone_has_a_Windows_mapping_for_the_fallback(string ianaId)
    {
        Assert.True(TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId, out var windowsId));
        Assert.False(string.IsNullOrWhiteSpace(windowsId));
    }
}
