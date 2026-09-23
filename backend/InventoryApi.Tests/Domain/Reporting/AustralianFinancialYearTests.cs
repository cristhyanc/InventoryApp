using Inventory.Domain.Reporting;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting;

public class AustralianFinancialYearTests
{
    [Theory]
    [InlineData(2025, 6, 30, "FY2024-25")]
    [InlineData(2025, 7, 1, "FY2025-26")]
    [InlineData(2026, 1, 1, "FY2025-26")]
    public void Label_uses_the_1_July_to_30_June_financial_year_boundary(int year, int month, int day, string expected)
    {
        var date = new DateTime(year, month, day);

        Assert.Equal(expected, AustralianFyHelper.Label(date));
        Assert.Equal(expected, AustralianFinancialYear.Label(date));
    }

    [Fact]
    public void Start_and_End_bracket_the_financial_year_inclusively()
    {
        var date = new DateTime(2026, 1, 1);

        Assert.Equal(new DateTime(2025, 7, 1), AustralianFinancialYear.Start(date));
        Assert.Equal(new DateTime(2026, 6, 30), AustralianFinancialYear.End(date));
    }

    [Theory]
    [InlineData("FY2025-26")]
    [InlineData("2025-26")]
    [InlineData("2025/26")]
    public void TryParse_accepts_a_four_digit_start_year_label(string value)
    {
        var parsed = AustralianFyHelper.TryParse(value, out var start);

        Assert.True(parsed);
        Assert.Equal(new DateTime(2025, 7, 1), start);
    }

    [Fact]
    public void TryParse_treats_a_bare_four_digit_year_as_the_financial_years_end_year()
    {
        var parsed = AustralianFyHelper.TryParse("2025", out var start);

        Assert.True(parsed);
        Assert.Equal(new DateTime(2024, 7, 1), start);
    }

    [Fact]
    public void TryParse_rejects_a_two_digit_start_year()
    {
        var parsed = AustralianFyHelper.TryParse("25-26", out _);

        Assert.False(parsed);
    }

    [Fact]
    public void TryParse_rejects_non_numeric_input()
    {
        var parsed = AustralianFyHelper.TryParse("not-a-year", out _);

        Assert.False(parsed);
    }
}
