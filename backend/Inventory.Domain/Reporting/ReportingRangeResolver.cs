namespace Inventory.Domain.Reporting;

public readonly record struct DateRange(DateTime From, DateTime ToDate)
{
    public DateTime EndExclusive => ToDate.Date.AddDays(1);
}

/// <summary>
/// Resolves the inclusive reporting date range from a filter's explicit dates or its Australian
/// financial-year label. A financial-year label, when present and parseable, overrides explicit dates.
/// </summary>
public static class ReportingRangeResolver
{
    public static DateRange Resolve(DateTime? from, DateTime? to, DateTime? startDate, DateTime? endDate, string? financialYear)
    {
        var resolvedFrom = (from ?? startDate)?.Date ?? new DateTime(1900, 1, 1);
        var resolvedTo = (to ?? endDate)?.Date ?? new DateTime(9999, 12, 30);
        if (!string.IsNullOrWhiteSpace(financialYear) && AustralianFyHelper.TryParse(financialYear, out var fyFrom))
        {
            resolvedFrom = fyFrom;
            resolvedTo = fyFrom.AddYears(1).AddDays(-1);
        }
        if (resolvedTo < resolvedFrom) (resolvedFrom, resolvedTo) = (resolvedTo, resolvedFrom);
        return new DateRange(resolvedFrom, resolvedTo);
    }
}
