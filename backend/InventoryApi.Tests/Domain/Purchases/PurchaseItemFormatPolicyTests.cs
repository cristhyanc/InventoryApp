using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

public class PurchaseItemFormatPolicyTests
{
    [Fact]
    public void Empty_items_are_valid()
    {
        Assert.False(PurchaseItemFormatPolicy.HasInvalidItem(Array.Empty<PurchaseItemCandidate>()));
    }

    [Fact]
    public void A_well_formed_item_is_valid()
    {
        Assert.False(PurchaseItemFormatPolicy.HasInvalidItem([new PurchaseItemCandidate(1, 2m, 1.5m)]));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(-1, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1.5, 1)]
    [InlineData(1, 1, -1)]
    public void An_invalid_item_fails(long productId, double quantity, double unitCost)
    {
        Assert.True(PurchaseItemFormatPolicy.HasInvalidItem(
            [new PurchaseItemCandidate(productId, (decimal)quantity, (decimal)unitCost)]));
    }

    [Fact]
    public void One_invalid_item_among_valid_ones_fails_the_whole_set()
    {
        Assert.True(PurchaseItemFormatPolicy.HasInvalidItem(
        [
            new PurchaseItemCandidate(1, 2m, 1m),
            new PurchaseItemCandidate(2, 0m, 1m),
        ]));
    }

    [Fact]
    public void A_zero_unit_cost_is_allowed()
    {
        Assert.False(PurchaseItemFormatPolicy.HasInvalidItem([new PurchaseItemCandidate(1, 1m, 0m)]));
    }
}
