using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Domain.Nayax;

/// <summary>
/// The Event 510 "Machine Product Changed" parser (issue #1325), starting from the one real alert
/// the repository owner captured: it anchors on the text markers rather than the operator prefix,
/// and anything it cannot read unambiguously fails with a reason instead of a guess.
/// </summary>
public class NayaxProductReplacementEventParserTests
{
    [Fact]
    public void The_captured_alert_deserialises_as_event_510_and_its_event_data_parses()
    {
        var alert = JsonSerializer.Deserialize<NayaxMachineAlert>(NayaxProductChangedAlertFixture.Json);

        Assert.NotNull(alert);
        Assert.Equal(NayaxMachineAlertEventCodes.MachineProductChanged, alert.EventCode);
        Assert.Equal(510, alert.EventCode);
        Assert.Equal("Machine Product Changed", alert.EventDescription);
        Assert.Equal(900000000001, alert.EventLogId);
        Assert.Equal(NayaxProductChangedAlertFixture.EventData, alert.EventData);

        var ok = NayaxProductReplacementEventParser.TryParse(alert.EventData, out var parsed, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal(58, parsed.Mdb);
        Assert.Equal("V Energy Drink Sugarfree Original 250ML", parsed.OutgoingProductName);
        Assert.Equal("Redbull Red Edition", parsed.IncomingProductName);
    }

    [Theory]
    [InlineData("Jane Doe 'has replaced product Coke 375mL with product Sprite 375mL | Product MDB: 7")]
    [InlineData("Jane Doe has replaced product Coke 375mL with product Sprite 375mL | Product MDB: 7")]
    [InlineData("has replaced product Coke 375mL with product Sprite 375mL | Product MDB: 7")]
    [InlineData("  [operator] 'has replaced product   Coke 375mL   with product Sprite 375mL|Product MDB:7  ")]
    public void Parses_whatever_the_operator_prefix_and_spacing(string eventData)
    {
        var ok = NayaxProductReplacementEventParser.TryParse(eventData, out var parsed, out var reason);

        Assert.True(ok, reason);
        Assert.Equal(7, parsed.Mdb);
        Assert.Equal("Coke 375mL", parsed.OutgoingProductName);
        Assert.Equal("Sprite 375mL", parsed.IncomingProductName);
    }

    [Fact]
    public void A_product_name_with_a_pipe_or_parentheses_is_kept_whole()
    {
        var ok = NayaxProductReplacementEventParser.TryParse(
            "[operator] 'has replaced product Chips (Salt | Vinegar) with product Mars Bar (2.50) | Product MDB: 12",
            out var parsed,
            out _);

        Assert.True(ok);
        Assert.Equal(12, parsed.Mdb);
        Assert.Equal("Chips (Salt | Vinegar)", parsed.OutgoingProductName);
        Assert.Equal("Mars Bar (2.50)", parsed.IncomingProductName);
    }

    [Theory]
    [InlineData(null, "empty")]
    [InlineData("   ", "empty")]
    [InlineData("Eunhye 'Adjusted Stock, Product MDB: 13 | Chips | 2", "supported")]
    [InlineData("[operator] 'has replaced product Coke with product Sprite", "supported")]
    [InlineData("[operator] 'has replaced product Coke with product Sprite | Product MDB: x", "supported")]
    [InlineData("[operator] 'has replaced product Coke with product Sprite | Product MDB: 7 | extra", "supported")]
    [InlineData("[operator] 'replaced product Coke with product Sprite | Product MDB: 7", "supported")]
    [InlineData("[operator] 'has replaced product Coke with product Sprite | Product MDB: 99999999999", "integer")]
    public void Malformed_text_fails_with_a_reason_and_no_values(string? eventData, string reasonFragment)
    {
        var ok = NayaxProductReplacementEventParser.TryParse(eventData, out var parsed, out var reason);

        Assert.False(ok);
        Assert.Equal(default, parsed);
        Assert.Contains(reasonFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_name_containing_the_with_product_marker_is_ambiguous_and_is_not_guessed()
    {
        var ok = NayaxProductReplacementEventParser.TryParse(
            "[operator] 'has replaced product Box with product inside with product Sprite | Product MDB: 7",
            out var parsed,
            out var reason);

        Assert.False(ok);
        Assert.Equal(default, parsed);
        Assert.Contains("more than once", reason, StringComparison.Ordinal);
    }
}
