using System;
using Inventory.Domain.Reporting;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting;

public class ReportingRangeResolverTests
{
    [Fact]
    public void Resolve_defaults_to_the_full_supported_range_when_nothing_is_provided()
    {
        var range = ReportingRangeResolver.Resolve(null, null, null, null, null);

        Assert.Equal(new DateTime(1900, 1, 1), range.From);
        Assert.Equal(new DateTime(9999, 12, 30), range.ToDate);
    }

    [Fact]
    public void Resolve_prefers_explicit_From_To_over_legacy_StartDate_EndDate_aliases()
    {
        var range = ReportingRangeResolver.Resolve(
            new DateTime(2026, 2, 1), new DateTime(2026, 2, 28),
            new DateTime(2020, 1, 1), new DateTime(2020, 1, 2), null);

        Assert.Equal(new DateTime(2026, 2, 1), range.From);
        Assert.Equal(new DateTime(2026, 2, 28), range.ToDate);
    }

    [Fact]
    public void Resolve_falls_back_to_legacy_StartDate_EndDate_when_From_To_are_absent()
    {
        var range = ReportingRangeResolver.Resolve(null, null, new DateTime(2026, 3, 1), new DateTime(2026, 3, 31), null);

        Assert.Equal(new DateTime(2026, 3, 1), range.From);
        Assert.Equal(new DateTime(2026, 3, 31), range.ToDate);
    }

    [Fact]
    public void Resolve_swaps_from_and_to_when_to_precedes_from()
    {
        var range = ReportingRangeResolver.Resolve(new DateTime(2026, 3, 31), new DateTime(2026, 3, 1), null, null, null);

        Assert.Equal(new DateTime(2026, 3, 1), range.From);
        Assert.Equal(new DateTime(2026, 3, 31), range.ToDate);
    }

    [Fact]
    public void Resolve_overrides_explicit_dates_with_a_parseable_financial_year()
    {
        var range = ReportingRangeResolver.Resolve(new DateTime(2020, 1, 1), new DateTime(2020, 1, 2), null, null, "FY2025-26");

        Assert.Equal(new DateTime(2025, 7, 1), range.From);
        Assert.Equal(new DateTime(2026, 6, 30), range.ToDate);
    }

    [Fact]
    public void Resolve_ignores_an_unparseable_financial_year_and_keeps_explicit_dates()
    {
        var range = ReportingRangeResolver.Resolve(new DateTime(2026, 2, 1), new DateTime(2026, 2, 2), null, null, "not-a-year");

        Assert.Equal(new DateTime(2026, 2, 1), range.From);
        Assert.Equal(new DateTime(2026, 2, 2), range.ToDate);
    }

    [Fact]
    public void EndExclusive_is_the_day_after_the_inclusive_end_date()
    {
        var range = ReportingRangeResolver.Resolve(new DateTime(2026, 2, 1), new DateTime(2026, 2, 2), null, null, null);

        Assert.Equal(new DateTime(2026, 2, 3), range.EndExclusive);
    }
}
