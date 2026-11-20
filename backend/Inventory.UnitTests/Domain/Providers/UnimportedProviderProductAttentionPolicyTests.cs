using Inventory.Domain.Providers;
using Xunit;

namespace InventoryApi.Tests.Domain.Providers;

/// <summary>
/// The window rule behind the Dashboard's unimported-provider-product alert (issue #1352).
///
/// <para>It is one subtraction, and it is pinned anyway: the alert's whole promise is that it counts
/// exactly thirty business calendar dates including today, so a sale on the first of them is counted
/// and one the date before is not. An off-by-one here would silently widen or narrow what an operator
/// is told to go and import.</para>
/// </summary>
public class UnimportedProviderProductAttentionPolicyTests
{
    [Fact]
    public void Thirty_dates_are_counted_today_included() =>
        Assert.Equal(30, UnimportedProviderProductAttentionPolicy.WindowDays);

    [Fact]
    public void The_window_starts_twenty_nine_dates_before_today() =>
        Assert.Equal(
            new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Unspecified),
            UnimportedProviderProductAttentionPolicy.FirstBusinessDate(new DateTime(2026, 11, 18)));

    /// <summary>
    /// The first date and today are thirty dates apart inclusive, whatever month boundary or leap
    /// day falls between them: the rule is a date subtraction, not a count of hours.
    /// </summary>
    [Theory]
    [InlineData(2026, 11, 18)]
    [InlineData(2027, 1, 5)]
    [InlineData(2028, 3, 1)]
    [InlineData(2026, 4, 5)]
    public void The_first_date_and_today_span_exactly_the_window(int year, int month, int day)
    {
        var today = new DateTime(year, month, day);

        var first = UnimportedProviderProductAttentionPolicy.FirstBusinessDate(today);

        Assert.Equal(UnimportedProviderProductAttentionPolicy.WindowDays, (today - first).Days + 1);
    }

    /// <summary>A time of day on the given instant never reaches the answer.</summary>
    [Fact]
    public void A_time_of_day_on_today_is_ignored() =>
        Assert.Equal(
            UnimportedProviderProductAttentionPolicy.FirstBusinessDate(new DateTime(2026, 11, 18)),
            UnimportedProviderProductAttentionPolicy.FirstBusinessDate(
                new DateTime(2026, 11, 18, 23, 59, 59, DateTimeKind.Utc)));

    /// <summary>
    /// Three examples, because the alert names a few products and sends the operator to Import
    /// products for the rest.
    /// </summary>
    [Fact]
    public void At_most_three_products_are_named() =>
        Assert.Equal(3, UnimportedProviderProductAttentionPolicy.MaxExamples);
}
