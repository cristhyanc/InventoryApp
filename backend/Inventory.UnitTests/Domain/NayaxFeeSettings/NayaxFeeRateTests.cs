using Inventory.Domain.NayaxFeeSettings;
using Xunit;

namespace InventoryApi.Tests.Domain.NayaxFeeSettings;

public class NayaxFeeRateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(0.1234)]
    public void TryCreate_valid_boundary_values_succeeds(decimal value)
    {
        var created = NayaxFeeRate.TryCreate(value, out var rate);

        Assert.True(created);
        Assert.Equal(value, rate.Value);
    }

    [Fact]
    public void TryCreate_negative_value_fails()
    {
        var created = NayaxFeeRate.TryCreate(-0.01m, out _);

        Assert.False(created);
    }

    [Fact]
    public void TryCreate_more_than_four_decimal_places_fails()
    {
        var created = NayaxFeeRate.TryCreate(0.12345m, out _);

        Assert.False(created);
    }
}
