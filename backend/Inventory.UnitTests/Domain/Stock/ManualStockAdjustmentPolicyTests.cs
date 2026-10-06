using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Domain.Stock;

/// <summary>
/// Mirrors the former <c>InventoryApi.Services.StockService.Adjust</c> inline validation (issue
/// #282): a Correction must remove stock, and a positive Restock must carry a non-negative unit
/// cost. Every other reason/quantity combination is left untouched by this policy.
/// </summary>
public class ManualStockAdjustmentPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Correction_with_a_non_negative_quantity_change_is_rejected(int quantityChange)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => ManualStockAdjustmentPolicy.Validate(StockAdjustmentReason.Correction, quantityChange, null));

        Assert.Equal("Correction quantity must remove stock.", exception.Message);
    }

    [Fact]
    public void Correction_with_a_negative_quantity_change_is_accepted()
    {
        ManualStockAdjustmentPolicy.Validate(StockAdjustmentReason.Correction, -4, null);
    }

    [Fact]
    public void Positive_restock_without_a_unit_cost_is_rejected()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => ManualStockAdjustmentPolicy.Validate(StockAdjustmentReason.Restock, 3, null));

        Assert.Equal("Unit cost is required for a positive Restock adjustment.", exception.Message);
    }

    [Fact]
    public void Positive_restock_with_a_negative_unit_cost_is_rejected()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => ManualStockAdjustmentPolicy.Validate(StockAdjustmentReason.Restock, 3, -1m));

        Assert.Equal("Unit cost cannot be negative for a positive Restock adjustment.", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.25)]
    public void Positive_restock_with_a_valid_unit_cost_is_accepted(decimal unitCost)
    {
        ManualStockAdjustmentPolicy.Validate(StockAdjustmentReason.Restock, 4, unitCost);
    }

    [Fact]
    public void A_negative_restock_never_requires_a_unit_cost()
    {
        ManualStockAdjustmentPolicy.Validate(StockAdjustmentReason.Restock, -4, null);
    }

    [Theory]
    [InlineData(StockAdjustmentReason.MachineRefill)]
    [InlineData(StockAdjustmentReason.Sale)]
    [InlineData(StockAdjustmentReason.Damaged)]
    [InlineData(StockAdjustmentReason.Expired)]
    public void Reasons_other_than_restock_and_correction_are_never_validated(StockAdjustmentReason reason)
    {
        ManualStockAdjustmentPolicy.Validate(reason, 0, null);
        ManualStockAdjustmentPolicy.Validate(reason, -5, null);
        ManualStockAdjustmentPolicy.Validate(reason, 5, null);
    }
}
