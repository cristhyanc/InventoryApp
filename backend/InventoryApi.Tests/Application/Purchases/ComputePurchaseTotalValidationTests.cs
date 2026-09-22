using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

public class ComputePurchaseTotalValidationTests
{
    [Fact]
    public void Handle_delegates_to_the_domain_policy()
    {
        var useCase = new ComputePurchaseTotalValidation();

        var result = useCase.Handle(
            totalAmount: 30m,
            deliveryCost: null,
            packageCost: null,
            items: new[] { new PurchaseTotalValidationItem(20m, 1m) });

        Assert.True(result.HasMismatch);
        Assert.Equal(20m, result.ItemSubtotal);
        Assert.Equal(20m, result.CalculatedTotal);
        Assert.Equal(10m, result.Difference);
    }
}
