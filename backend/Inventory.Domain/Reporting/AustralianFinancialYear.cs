namespace Inventory.Domain.Reporting;

public static class AustralianFyHelper
{
    public static DateTime Start(DateTime date) => new(date.Month >= 7 ? date.Year : date.Year - 1, 7, 1);
    public static string Label(DateTime date) { var start = Start(date); return $"FY{start.Year}-{(start.Year + 1) % 100:00}"; }
    public static bool TryParse(string value, out DateTime start)
    {
        start = default;
        var text = value.Trim().ToUpperInvariant().Replace("FY", string.Empty).Replace("/", "-").Trim();
        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (!int.TryParse(parts[0], out var year) || year < 1900) return false;
        var startYear = parts.Length > 1 && year < 100 ? year + 2000 : (parts.Length > 1 ? year : year - 1);
        if (parts.Length == 1 && year >= 1900) startYear = year - 1;
        start = new DateTime(startYear, 7, 1);
        return true;
    }

}

public static class AustralianFinancialYear
{
    public static DateTime Start(DateTime date) => AustralianFyHelper.Start(date);
    public static DateTime End(DateTime date) => Start(date).AddYears(1).AddDays(-1);
    public static string Label(DateTime date) => AustralianFyHelper.Label(date);
    public static bool TryParse(string value, out DateTime start) => AustralianFyHelper.TryParse(value, out start);
}
