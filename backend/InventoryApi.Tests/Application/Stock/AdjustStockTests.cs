using Inventory.Application.Stock;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Application.Stock;

public class AdjustStockTests
{
    private static ManualStockAdjustmentInput Input(
        int quantityChange = -2, StockAdjustmentReason reason = StockAdjustmentReason.MachineRefill, decimal? unitCost = null) =>
        new(quantityChange, reason, "note", 5, null, unitCost);

    [Fact]
    public async Task An_unknown_product_returns_null_without_validating_or_applying()
    {
        var store = new FakeStockAdjustmentStore { ProductExists = false };
        var useCase = new AdjustStock(store);

        var result = await useCase.Handle(1, Input(quantityChange: 0, reason: StockAdjustmentReason.Correction), CancellationToken.None);

        Assert.Null(result);
        Assert.False(store.ApplyCalled);
    }

    [Fact]
    public async Task An_invalid_request_throws_before_applying_any_movement()
    {
        var store = new FakeStockAdjustmentStore();
        var useCase = new AdjustStock(store);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => useCase.Handle(1, Input(quantityChange: 0, reason: StockAdjustmentReason.Correction), CancellationToken.None));

        Assert.Equal("Correction quantity must remove stock.", exception.Message);
        Assert.False(store.ApplyCalled);
    }

    [Fact]
    public async Task A_valid_request_is_applied_through_the_store()
    {
        var store = new FakeStockAdjustmentStore();
        var useCase = new AdjustStock(store);
        var input = Input(quantityChange: -4, reason: StockAdjustmentReason.MachineRefill);

        var result = await useCase.Handle(1, input, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(store.ApplyCalled);
        Assert.Same(input, store.LastAppliedInput);
    }
}
