using Inventory.Domain.Products;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

/// <summary>
/// The free-text limits of issue #1286 for this record: each field accepts a value exactly at its
/// limit, refuses one character more by naming the field and the limit, never refuses a blank
/// value, and on an update refuses only a new over-long value - never the stored one sent back.
/// </summary>
public class ProductFieldLengthPolicyTests
{
    /// <summary>Each field's limit and the sentence one character more is refused with.</summary>
    private static readonly (int Max, string Message)[] Fields =
    [
        (ProductFieldLengthPolicy.MaxNameLength, "Product name must be 200 characters or fewer."),
        (ProductFieldLengthPolicy.MaxSkuLength, "SKU must be 100 characters or fewer."),
        (ProductFieldLengthPolicy.MaxUnitLength, "Unit label must be 100 characters or fewer."),
        (ProductFieldLengthPolicy.MaxDescriptionLength, "Description must be 2,000 characters or fewer."),
    ];

    public static TheoryData<int> FieldIndexes()
    {
        var data = new TheoryData<int>();
        for (var index = 0; index < Fields.Length; index++) data.Add(index);
        return data;
    }

    [Fact]
    public void The_limits_are_the_ones_the_issue_table_states() =>
        Assert.Equal(
            [200, 100, 100, 2000],
            Fields.Select(field => field.Max).ToArray());

    [Theory]
    [MemberData(nameof(FieldIndexes))]
    public void A_value_exactly_at_the_limit_is_accepted(int field) =>
        Assert.Null(Create(field, new string('x', Fields[field].Max)));

    [Theory]
    [MemberData(nameof(FieldIndexes))]
    public void A_value_one_over_the_limit_is_refused_naming_the_field_and_limit(int field) =>
        Assert.Equal(Fields[field].Message, Create(field, new string('x', Fields[field].Max + 1)));

    [Theory]
    [MemberData(nameof(FieldIndexes))]
    public void Trailing_whitespace_does_not_make_a_value_too_long(int field) =>
        Assert.Null(Create(field, new string('x', Fields[field].Max) + "   "));

    [Theory]
    [MemberData(nameof(FieldIndexes))]
    public void A_blank_value_is_never_too_long(int field)
    {
        Assert.Null(Create(field, null));
        Assert.Null(Create(field, "   "));
    }

    [Theory]
    [MemberData(nameof(FieldIndexes))]
    public void An_update_resubmitting_an_over_long_stored_value_is_accepted(int field)
    {
        var legacy = new string('x', Fields[field].Max + 50);

        Assert.Null(Update(field, legacy, legacy));
    }

    [Theory]
    [MemberData(nameof(FieldIndexes))]
    public void An_update_sending_a_new_over_long_value_is_refused(int field)
    {
        var legacy = new string('x', Fields[field].Max + 50);

        Assert.Equal(Fields[field].Message, Update(field, "ok", new string('y', Fields[field].Max + 1)));
        Assert.Equal(Fields[field].Message, Update(field, legacy, legacy[1..]));
    }

    [Theory]
    [MemberData(nameof(FieldIndexes))]
    public void An_update_at_the_limit_is_accepted(int field) =>
        Assert.Null(Update(field, "ok", new string('y', Fields[field].Max)));

    /// <summary>The create rule with <paramref name="value"/> in one field and a short value in the others.</summary>
    private static string? Create(int field, string? value)
    {
        var values = Values(field, value);
        return ProductFieldLengthPolicy.ValidateCreate(values[0], values[1], values[2], values[3]);
    }

    /// <summary>The update rule with one field stored and submitted as given, the others unchanged.</summary>
    private static string? Update(int field, string? stored, string? submitted)
    {
        var storedValues = Values(field, stored);
        var submittedValues = Values(field, submitted);
        return ProductFieldLengthPolicy.ValidateUpdate(
            storedValues[0], storedValues[1], storedValues[2], storedValues[3],
            submittedValues[0], submittedValues[1], submittedValues[2], submittedValues[3]);
    }

    private static string?[] Values(int field, string? value) =>
        Enumerable.Range(0, Fields.Length).Select(index => index == field ? value : "ok").ToArray();
}
