using Inventory.Application.Machines;
using Inventory.Application.Stock;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;
using InventoryApi.Tests.Application.Machines;
using Xunit;

namespace InventoryApi.Tests.Application.Stock;

public class AdjustStockTests
{
    /// <summary>The local machine every refill here names (issue #741).</summary>
    private const long LocalMachineId = 5;

    private static FakeLocalMachineStore Machines() =>
        new([new LocalMachineRecord(LocalMachineId, "Machine A", IsActive: true)]);

    private static ManualStockAdjustmentInput Input(
        int quantityChange = -2, StockAdjustmentReason reason = StockAdjustmentReason.MachineRefill, decimal? unitCost = null, long? machineId = LocalMachineId) =>
        new(quantityChange, reason, "note", machineId, null, unitCost);

    [Fact]
    public async Task An_unknown_product_returns_null_without_validating_or_applying()
    {
        var store = new FakeStockAdjustmentStore { ProductExists = false };
        var useCase = new AdjustStock(store, Machines());

        var result = await useCase.Handle(1, Input(quantityChange: 0, reason: StockAdjustmentReason.Correction), CancellationToken.None);

        Assert.Null(result);
        Assert.False(store.ApplyCalled);
    }

    [Fact]
    public async Task An_invalid_request_throws_before_applying_any_movement()
    {
        var store = new FakeStockAdjustmentStore();
        var useCase = new AdjustStock(store, Machines());

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => useCase.Handle(1, Input(quantityChange: 0, reason: StockAdjustmentReason.Correction), CancellationToken.None));

        Assert.Equal("Correction quantity must remove stock.", exception.Message);
        Assert.False(store.ApplyCalled);
    }

    [Fact]
    public async Task A_valid_request_is_applied_through_the_store()
    {
        var store = new FakeStockAdjustmentStore();
        var useCase = new AdjustStock(store, Machines());
        var input = Input(quantityChange: -4, reason: StockAdjustmentReason.MachineRefill);

        var result = await useCase.Handle(1, input, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(store.ApplyCalled);
        Assert.Same(input, store.LastAppliedInput);
    }

    /// <summary>
    /// A machine id that is not a local machine of the caller's business - another business's
    /// machine, or a provider machine number an older client still sends - is refused with a clear
    /// reason before anything is written (issue #741).
    /// </summary>
    [Fact]
    public async Task A_machine_that_is_not_a_local_machine_of_the_business_is_refused_before_applying()
    {
        var store = new FakeStockAdjustmentStore();
        var useCase = new AdjustStock(store, Machines());

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => useCase.Handle(1, Input(machineId: 900), CancellationToken.None));

        Assert.Equal(ManualStockAdjustmentPolicy.UnknownMachineMessage, exception.Message);
        Assert.False(store.ApplyCalled);
    }

    [Fact]
    public async Task An_adjustment_that_names_no_machine_needs_no_machine_lookup()
    {
        var store = new FakeStockAdjustmentStore();
        var useCase = new AdjustStock(store, new FakeLocalMachineStore());
        var input = Input(quantityChange: -1, reason: StockAdjustmentReason.Correction, machineId: null);

        var result = await useCase.Handle(1, input, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Same(input, store.LastAppliedInput);
    }
}
