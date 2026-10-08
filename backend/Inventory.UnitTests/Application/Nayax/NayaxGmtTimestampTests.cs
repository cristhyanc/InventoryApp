using System.Globalization;
using Inventory.Application.Nayax;
using Xunit;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// Issue #471: the one parser both Nayax ingestion paths use for a field Nayax documents as GMT -
/// the live Lynx <c>AuthorizationDateTimeGMT</c> JSON field (through
/// <see cref="NayaxGmtTimestampJsonConverter"/>) and the uploaded export's column of the same name.
/// Its rule is that the field contract establishes UTC, so a value with no designator is UTC, one
/// with a designator or an explicit offset keeps its physical instant, and an unusable value has no
/// instant at all.
/// </summary>
public class NayaxGmtTimestampTests
{
    /// <summary>2026-10-07T23:42:44.263Z: the instant the operator's reported sale was authorized at.</summary>
    private static readonly DateTime OperatorInstantUtc = new(2026, 10, 7, 23, 42, 44, 263, DateTimeKind.Utc);

    [Theory]
    // Offset-free: UTC because the field is GMT, whatever time zone the host runs in.
    [InlineData("2026-10-07T23:42:44.263")]
    [InlineData("2026-10-07T23:42:44.263Z")]
    [InlineData("2026-10-07T23:42:44.263+00:00")]
    // The same physical instant rendered in Sydney AEDT and AEST, and west of UTC.
    [InlineData("2026-10-08T10:42:44.263+11:00")]
    [InlineData("2026-10-08T09:42:44.263+10:00")]
    [InlineData("2026-10-07T18:42:44.263-05:00")]
    public void Every_rendering_of_one_instant_parses_to_that_instant(string value)
    {
        Assert.Equal(OperatorInstantUtc, NayaxGmtTimestamp.ParseInstantUtc(value));
        Assert.Equal(DateTimeKind.Utc, NayaxGmtTimestamp.ParseInstantUtc(value)!.Value.Kind);
        Assert.Equal(TimeSpan.Zero, NayaxGmtTimestamp.Parse(value)!.Value.Offset);
    }

    /// <summary>
    /// The documented renderings of a second-and-fraction time of day: no fractional part at all,
    /// one digit, two, the three the live samples print, and the seven a round-tripped .NET value
    /// carries. The Nayax portal's own live sample response prints <c>.817</c>, <c>.46</c> and
    /// <c>.5</c> on neighbouring items, so a fixed fractional width would reject real payloads.
    /// </summary>
    [Theory]
    [InlineData("2026-10-07T23:42:44", 0)]
    [InlineData("2026-10-07T23:42:44.5", 500)]
    [InlineData("2026-10-07T23:42:44.26", 260)]
    [InlineData("2026-10-07T23:42:44.263", 263)]
    [InlineData("2026-10-07T23:42:44.2630000", 263)]
    [InlineData("2026-10-07T23:42:44.263Z", 263)]
    [InlineData("2026-10-07T23:42:44Z", 0)]
    // The export's unambiguous ISO text form, which this parser also reads, writes a space separator.
    [InlineData("2026-10-07 23:42:44.263", 263)]
    [InlineData("2026-10-07 23:42:44", 0)]
    [InlineData("2026-10-08 10:42:44.263+11:00", 263)]
    // An hours-and-minutes offset is unambiguous with or without its colon.
    [InlineData("2026-10-08T10:42:44.263+1100", 263)]
    // Surrounding whitespace is not information.
    [InlineData("  2026-10-07T23:42:44.263Z  ", 263)]
    public void Every_supported_fractional_and_separator_form_is_read(string value, int milliseconds)
    {
        var expected = new DateTime(2026, 10, 7, 23, 42, 44, milliseconds, DateTimeKind.Utc);

        Assert.Equal(expected, NayaxGmtTimestamp.ParseInstantUtc(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-instant")]
    [InlineData("2026-13-45T99:99:99")]
    [InlineData("1760000000")]
    public void An_absent_blank_or_unreadable_value_has_no_instant(string? value)
    {
        Assert.Null(NayaxGmtTimestamp.Parse(value));
        Assert.Null(NayaxGmtTimestamp.ParseInstantUtc(value));
    }

    /// <summary>
    /// Fail closed on a malformed-but-parseable value. The field is documented
    /// <c>string&lt;date-time&gt;</c>, so a value that a permissive
    /// <c>DateTimeOffset.TryParse</c> would happily turn into *some* instant, but
    /// which is not a date-time the contract describes, must have no instant at all:
    ///
    /// <list type="bullet">
    ///   <item>A date-only value states no time of day. Reading <c>2026-10-07</c> as
    ///   2026-10-07T00:00:00Z invents midnight, which in Sydney is 11:00 on 7 October under AEDT - a
    ///   whole business day's worth of error on a sale that may have happened at any hour.</item>
    ///   <item>A slash-separated date is ambiguous: <c>07/10/2026</c> is 7 October to an Australian
    ///   operator and 10 July to an American one, and nothing in the payload says which. Invariant
    ///   culture would silently pick 10 July.</item>
    ///   <item>Month-name, RFC 1123, basic-ISO, time-only and offset-shape-mismatched renderings are
    ///   not the contract's format either, however readable a human finds them.</item>
    /// </list>
    ///
    /// Each of these is skipped at ingestion: the sale is not imported, nothing falls back to the
    /// machine-local wall clock, and the rolling last-sales window offers the transaction again.
    /// </summary>
    [Theory]
    // Date only: no time of day was stated at all.
    [InlineData("2026-10-07")]
    [InlineData("2026-10-07Z")]
    [InlineData("2026-10")]
    [InlineData("2026")]
    // Ambiguous slash dates, with and without a time of day.
    [InlineData("07/10/2026")]
    [InlineData("07/10/2026 23:42:44")]
    [InlineData("10/07/2026 11:42:44 PM")]
    [InlineData("2026/10/07T23:42:44")]
    // Date/time formats the GMT field's contract does not use.
    [InlineData("Wed, 07 Oct 2026 23:42:44 GMT")]
    [InlineData("October 7, 2026 11:42:44 PM")]
    [InlineData("07-Oct-2026 23:42:44")]
    [InlineData("20261007T234244Z")]
    [InlineData("20261007")]
    // A time of day with no date: the instant's day would come from today's clock.
    [InlineData("23:42:44")]
    // An hours-only offset states no minutes: half-hour and three-quarter-hour zones exist, so the
    // shape is refused rather than assumed to mean :00.
    [InlineData("2026-10-08T10:42:44.263+11")]
    public void A_malformed_but_parseable_value_fails_closed(string value)
    {
        Assert.Null(NayaxGmtTimestamp.Parse(value));
        Assert.Null(NayaxGmtTimestamp.ParseInstantUtc(value));
    }

    /// <summary>
    /// What a permissive parse actually did with these two values, asserted here so the defect this
    /// parser refuses is recorded rather than described: a date-only value became midnight UTC - in
    /// Sydney, 11:00 on the previous business day under AEDT - and an ambiguous slash date became a
    /// different month entirely. Both would have been persisted as a sale's authoritative instant.
    /// This test also fails if the framework's own permissive reading ever changes.
    /// </summary>
    [Fact]
    public void The_permissive_parse_this_replaced_invented_an_instant_for_a_non_contract_value()
    {
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        Assert.True(DateTimeOffset.TryParse(
            "2026-10-07", CultureInfo.InvariantCulture, styles, out var dateOnly));
        Assert.Equal(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), dateOnly.UtcDateTime);
        Assert.Null(NayaxGmtTimestamp.Parse("2026-10-07"));

        Assert.True(DateTimeOffset.TryParse(
            "07/10/2026", CultureInfo.InvariantCulture, styles, out var slashDate));
        Assert.Equal(new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc), slashDate.UtcDateTime);
        Assert.Null(NayaxGmtTimestamp.Parse("07/10/2026"));
    }

    /// <summary>
    /// Normalization happens exactly once: re-rendering a parsed instant and parsing it again cannot
    /// move it, which is what makes a re-read transaction or a re-uploaded export row safe.
    /// </summary>
    [Fact]
    public void Re_parsing_a_value_this_parser_produced_cannot_shift_it()
    {
        var once = NayaxGmtTimestamp.Parse("2026-10-07T23:42:44.263")!.Value;
        var twice = NayaxGmtTimestamp.Parse(once.ToString("O"))!.Value;
        var thrice = NayaxGmtTimestamp.Parse(twice.UtcDateTime.ToString("O"))!.Value;

        Assert.Equal(OperatorInstantUtc, once.UtcDateTime);
        Assert.Equal(OperatorInstantUtc, twice.UtcDateTime);
        Assert.Equal(OperatorInstantUtc, thrice.UtcDateTime);
    }
}
