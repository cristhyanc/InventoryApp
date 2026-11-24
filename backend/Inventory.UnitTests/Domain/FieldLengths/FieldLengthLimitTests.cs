using Inventory.Domain.FieldLengths;
using Xunit;

namespace InventoryApi.Tests.Domain.FieldLengths;

/// <summary>
/// The shared measurement behind every record's length policy (issue #1286): trimmed length, the
/// boundary, the refusal sentence, and when an update leaves a stored value alone.
/// </summary>
public class FieldLengthLimitTests
{
    [Fact]
    public void A_value_exactly_at_the_limit_is_accepted() =>
        Assert.Null(FieldLengthLimit.Refusal("Name", new string('x', 10), 10));

    [Fact]
    public void A_value_one_over_the_limit_is_refused_by_field_and_limit() =>
        Assert.Equal(
            "Name must be 10 characters or fewer.",
            FieldLengthLimit.Refusal("Name", new string('x', 11), 10));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_never_too_long(string? submitted) =>
        Assert.Null(FieldLengthLimit.Refusal("Name", submitted, 0));

    [Fact]
    public void Surrounding_whitespace_does_not_count() =>
        Assert.Null(FieldLengthLimit.Refusal("Name", $"  {new string('x', 10)}\t\r\n", 10));

    [Fact]
    public void The_limit_is_written_with_a_thousands_separator() =>
        Assert.Equal("Notes must be 2,000 characters or fewer.", FieldLengthLimit.TooLongMessage("Notes", 2_000));

    [Fact]
    public void An_update_that_resubmits_an_over_long_stored_value_is_accepted()
    {
        var stored = new string('x', 50);

        Assert.Null(FieldLengthLimit.ChangeRefusal("Name", stored, stored, 10));
        Assert.Null(FieldLengthLimit.ChangeRefusal("Name", stored, $" {stored} ", 10));
    }

    [Fact]
    public void An_update_that_sends_a_new_over_long_value_is_refused()
    {
        var stored = new string('x', 50);

        Assert.NotNull(FieldLengthLimit.ChangeRefusal("Name", stored, new string('x', 49), 10));
        Assert.NotNull(FieldLengthLimit.ChangeRefusal("Name", "short", new string('y', 11), 10));
    }

    [Fact]
    public void An_update_that_shortens_an_over_long_stored_value_is_accepted() =>
        Assert.Null(FieldLengthLimit.ChangeRefusal("Name", new string('x', 50), "short", 10));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", null)]
    [InlineData(" a ", "a")]
    public void Blank_and_none_and_trimmed_text_are_unchanged(string? stored, string? submitted) =>
        Assert.True(FieldLengthLimit.IsUnchanged(stored, submitted));
}
