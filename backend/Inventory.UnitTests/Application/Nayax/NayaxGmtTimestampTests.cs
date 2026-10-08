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
