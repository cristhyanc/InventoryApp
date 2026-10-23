#nullable enable

using System.Globalization;
using System.Net;
using Inventory.Infrastructure.Nayax;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

// Issue #844: the complete Retry-After parsing table, decided against a fixed instant rather than
// the host's own clock, so an HTTP-date row is exact instead of approximately right. The handler
// tests above exercise the same rules through a real retry sequence; this file is the per-row
// statement of what each header value means, including the values an upstream may legitimately
// send that this application deliberately refuses to interpret.
public class NayaxRetryAfterPolicyTests
{
    private static readonly DateTime NowUtc = new(2026, 10, 23, 9, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("9", 9)]
    [InlineData("10", 10)]
    public void A_whole_number_of_seconds_up_to_the_ceiling_is_waited_out_then_retried_once(
        string header, int expectedSeconds)
    {
        var decision = Decide(header);

        Assert.True(decision.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), decision.HonouredDelay);
    }

    [Theory]
    [InlineData("11")]
    [InlineData("30")]
    [InlineData("3600")]
    [InlineData("999999999999999999999")] // digits only, but far beyond a 64-bit second count
    public void A_wait_longer_than_the_ceiling_is_not_retried_at_all(string header)
    {
        var decision = Decide(header);

        Assert.False(decision.ShouldRetry);
        Assert.Null(decision.HonouredDelay);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void An_HTTP_date_within_the_ceiling_is_waited_out_until_that_instant(int secondsAhead)
    {
        var decision = Decide(HttpDate(NowUtc.AddSeconds(secondsAhead)));

        Assert.True(decision.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(secondsAhead), decision.HonouredDelay);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(600)]
    public void An_HTTP_date_beyond_the_ceiling_is_not_retried_at_all(int secondsAhead)
    {
        var decision = Decide(HttpDate(NowUtc.AddSeconds(secondsAhead)));

        Assert.False(decision.ShouldRetry);
        Assert.Null(decision.HonouredDelay);
    }

    // "Retry now" and "retry at an instant that has already passed" are the one pair of rows that
    // could be read as "send the next request immediately". They are deliberately not: a rate
    // limited upstream gets the ordinary backoff, which is what a null honoured delay means.
    [Fact]
    public void Zero_seconds_is_retried_once_after_the_base_backoff()
    {
        var decision = Decide("0");

        Assert.True(decision.ShouldRetry);
        Assert.Null(decision.HonouredDelay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public void An_HTTP_date_now_or_in_the_past_is_retried_once_after_the_base_backoff(int secondsAhead)
    {
        var decision = Decide(HttpDate(NowUtc.AddSeconds(secondsAhead)));

        Assert.True(decision.ShouldRetry);
        Assert.Null(decision.HonouredDelay);
    }

    [Fact]
    public void A_missing_header_is_retried_once_after_the_base_backoff()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var decision = NayaxRetryAfterPolicy.Decide(response.Headers, NowUtc);

        Assert.True(decision.ShouldRetry);
        Assert.Null(decision.HonouredDelay);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-5")]            // negative
    [InlineData("2.5")]           // decimal
    [InlineData("soon")]          // non-numeric text
    [InlineData("1 hour")]
    [InlineData("Mon, 32 Foo 2026 09:30:40 GMT")] // unparseable date
    [InlineData("2026-10-23T09:30:40Z")]          // ISO 8601 is not an HTTP-date
    public void A_malformed_header_is_treated_exactly_like_a_missing_one(string header)
    {
        var decision = Decide(header);

        Assert.True(decision.ShouldRetry);
        Assert.Null(decision.HonouredDelay);
    }

    [Fact]
    public void Several_values_are_treated_exactly_like_a_missing_header()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "2");
        response.Headers.TryAddWithoutValidation("Retry-After", "30");

        var decision = NayaxRetryAfterPolicy.Decide(response.Headers, NowUtc);

        Assert.True(decision.ShouldRetry);
        Assert.Null(decision.HonouredDelay);
    }

    // Surrounding whitespace is optional whitespace in the header's own grammar, not a malformed
    // value, so a padded delay still means the delay it states.
    [Fact]
    public void Surrounding_whitespace_does_not_make_a_delay_malformed()
    {
        var decision = Decide(" 3 ");

        Assert.True(decision.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(3), decision.HonouredDelay);
    }

    [Fact]
    public void The_honoured_ceiling_is_the_decided_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), NayaxRetryAfterPolicy.MaxHonouredDelay);
    }

    private static NayaxRetryAfterDecision Decide(string header)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", header);

        return NayaxRetryAfterPolicy.Decide(response.Headers, NowUtc);
    }

    private static string HttpDate(DateTime instantUtc) =>
        instantUtc.ToString("R", CultureInfo.InvariantCulture);
}
