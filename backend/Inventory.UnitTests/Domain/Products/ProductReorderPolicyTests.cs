using Inventory.Domain.Products;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

/// <summary>
/// The reorder formulas moved off the <c>Product</c> persistence entity into this policy (issue #240)
/// with their rules unchanged. These cases pin the boundaries directly at the Domain layer; the
/// end-to-end reorder behaviour over EF stays covered by <c>ProductServiceTests</c>'s low-stock cases.
/// </summary>
public class ProductReorderPolicyTests
{
    [Theory]
    [InlineData(32, 0, 3, 29)]
    [InlineData(32, 20, 3, 49)]
    [InlineData(0, 0, 0, 0)]
    public void ProjectedStockForReorder_AddsOutstandingOrdersAndSubtractsMachineNeed(
        int quantityInStock, decimal onOrderQuantity, int machineReplenishmentNeed, decimal expected)
    {
        Assert.Equal(
            expected,
            ProductReorderPolicy.ProjectedStockForReorder(quantityInStock, onOrderQuantity, machineReplenishmentNeed));
    }

    [Theory]
    [InlineData(32, 0, 3, 57, 114, 85)]
    [InlineData(32, 20, 3, 57, 114, 65)]
    [InlineData(70, 0, 0, 57, 114, 0)]   // projected stock above the threshold needs nothing
    [InlineData(60, 0, 3, 57, 114, 57)]
    [InlineData(32, 85, 3, 57, 114, 0)]  // an outstanding order already covers the need
    public void NeedToOrder_AccountsForStockOutstandingOrdersAndMachineNeed(
        int quantityInStock,
        decimal onOrderQuantity,
        int machineReplenishmentNeed,
        int lowStockThreshold,
        int restockTo,
        decimal expected)
    {
        Assert.Equal(
            expected,
            ProductReorderPolicy.NeedToOrder(
                quantityInStock, onOrderQuantity, machineReplenishmentNeed, lowStockThreshold, restockTo));
    }

    /// <summary>
    /// Exactly at the low-stock threshold still triggers a reorder; one unit above it does not.
    /// </summary>
    [Fact]
    public void NeedToOrder_TreatsTheThresholdAsInclusive()
    {
        Assert.Equal(
            7m,
            ProductReorderPolicy.NeedToOrder(
                quantityInStock: 2, onOrderQuantity: 9m, machineReplenishmentNeed: 1, lowStockThreshold: 10, restockTo: 17));
        Assert.Equal(
            0m,
            ProductReorderPolicy.NeedToOrder(
                quantityInStock: 2, onOrderQuantity: 10m, machineReplenishmentNeed: 1, lowStockThreshold: 10, restockTo: 17));
    }

    /// <summary>A restock target already met must never produce a negative order quantity.</summary>
    [Fact]
    public void NeedToOrder_IsNeverNegative()
    {
        Assert.Equal(
            0m,
            ProductReorderPolicy.NeedToOrder(
                quantityInStock: 100, onOrderQuantity: 0m, machineReplenishmentNeed: 0, lowStockThreshold: 200, restockTo: 50));
    }

    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(5, 4, false)]
    [InlineData(0, 0, true)]
    public void IsLowStock_ComparesStorageStockInclusively(int quantityInStock, int lowStockThreshold, bool expected)
    {
        Assert.Equal(expected, ProductReorderPolicy.IsLowStock(quantityInStock, lowStockThreshold));
    }

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(true, 0, false)]
    [InlineData(false, 99, false)] // an inactive product never alerts
    public void IsReorderAlert_RequiresAnActiveProductWithSomethingToOrder(
        bool isActive, decimal needToOrder, bool expected)
    {
        Assert.Equal(expected, ProductReorderPolicy.IsReorderAlert(isActive, needToOrder));
    }
}
