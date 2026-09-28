using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Domain.Nayax;

public class NayaxStockAdjustmentEventParserTests
{
    [Fact]
    public void Parses_a_positive_quantity_with_the_employee_prefix()
    {
        var ok = NayaxStockAdjustmentEventParser.TryParse(
            "Eunhye Chung 'Adjusted Stock, Product MDB: 13 | 25g Nobby's Beef Jerky Hot | 2",
            out var parsed, out var failureReason);

        Assert.True(ok);
        Assert.Null(failureReason);
        Assert.Equal(13, parsed.Mdb);
        Assert.Equal("25g Nobby's Beef Jerky Hot", parsed.ProductName);
        Assert.Equal(2, parsed.SignedQuantity);
    }

    [Fact]
    public void Parses_a_negative_quantity()
    {
        var ok = NayaxStockAdjustmentEventParser.TryParse(
            "Jane Doe 'Adjusted Stock, Product MDB: 31 | Nu Pure Spring Water 600mL | -5",
            out var parsed, out _);

        Assert.True(ok);
        Assert.Equal(31, parsed.Mdb);
        Assert.Equal("Nu Pure Spring Water 600mL", parsed.ProductName);
        Assert.Equal(-5, parsed.SignedQuantity);
    }

    [Fact]
    public void Parses_without_any_employee_prefix_at_all()
    {
        var ok = NayaxStockAdjustmentEventParser.TryParse(
            "Product MDB: 7 | Coke 375mL | 10", out var parsed, out _);

        Assert.True(ok);
        Assert.Equal(7, parsed.Mdb);
        Assert.Equal("Coke 375mL", parsed.ProductName);
        Assert.Equal(10, parsed.SignedQuantity);
    }

    [Theory]
    [InlineData("Someone 'Adjusted Stock, Product MDB: 32 | Nu Pure Spring Water 600mL | 1")]
    [InlineData("A very different prefix entirely: Product MDB: 32 | Nu Pure Spring Water 600mL | 1")]
    public void Ignores_the_free_text_prefix_content(string eventData)
    {
        var ok = NayaxStockAdjustmentEventParser.TryParse(eventData, out var parsed, out _);

        Assert.True(ok);
        Assert.Equal(32, parsed.Mdb);
        Assert.Equal(1, parsed.SignedQuantity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Someone did something unrelated")]
    [InlineData("Product MDB: | Missing mdb | 2")]
    [InlineData("Product MDB: 13 | 25g Nobby's Beef Jerky Hot | not-a-number")]
    public void Malformed_event_data_fails_to_parse_with_a_reason(string? eventData)
    {
        var ok = NayaxStockAdjustmentEventParser.TryParse(eventData, out _, out var failureReason);

        Assert.False(ok);
        Assert.NotNull(failureReason);
    }
}
