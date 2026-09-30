using Inventory.Application.InventoryCounting;
using Inventory.Domain.Exceptions;
using Inventory.Domain.InventoryCounting;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.InventoryCounting;

/// <summary>
/// The Take Inventory apply use case (issue #245): re-reads the authoritative current quantity at
/// the mutation boundary, refuses a stale count instead of silently overwriting a concurrent change,
/// and applies exactly the existing positive Restock/Correction movement for a non-zero difference -
/// never <c>MachineRefill</c>. These tests use a mocked <see cref="IInventoryCountAdjustmentStore"/>
/// only; the EF-backed adapter has its own relational tests.
/// </summary>
public class ApplyInventoryCountTests
{
    private static Mock<IInventoryCountAdjustmentStore> StoreWith(long productId, string name, int quantityInStock)
    {
        var store = new Mock<IInventoryCountAdjustmentStore>();
        store.Setup(x => x.GetCurrentStockAsync(productId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InventoryCountProduct(productId, name, quantityInStock));
        return store;
    }

    [Fact]
    public async Task Handle_ReturnsNull_WhenTheProductDoesNotExist()
    {
        var store = new Mock<IInventoryCountAdjustmentStore>();
        store.Setup(x => x.GetCurrentStockAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InventoryCountProduct?)null);

        var result = await new ApplyInventoryCount(store.Object)
            .Handle(1, new InventoryCountApplyRequestDto(5, 5), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Handle_ReturnsConfirmedWithoutApplying_WhenCountedEqualsCurrent()
    {
        var store = StoreWith(1, "Coke", 46);

        var result = await new ApplyInventoryCount(store.Object)
            .Handle(1, new InventoryCountApplyRequestDto(46, 46), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(InventoryCountApplyOutcome.Confirmed, result!.Outcome);
        Assert.Equal(0, result.QuantityChange);
        Assert.Equal(46, result.CurrentStock);
        Assert.Null(result.StockAdjustmentId);
        store.Verify(
            x => x.ApplyAsync(
                It.IsAny<long>(), It.IsAny<InventoryCountMovementKind>(), It.IsAny<int>(), It.IsAny<decimal?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AppliesAnIncrease_WithTheAvailableRestockCostSuggestion_WhenCountedExceedsCurrent()
    {
        var store = StoreWith(1, "Coke", 17);
        store.Setup(x => x.GetRestockUnitCostAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(2.5m);
        store.Setup(x => x.ApplyAsync(1, InventoryCountMovementKind.Increase, 5, 2.5m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InventoryCountAdjustmentApplication(900, 22));

        var result = await new ApplyInventoryCount(store.Object)
            .Handle(1, new InventoryCountApplyRequestDto(22, 17), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(InventoryCountApplyOutcome.Applied, result!.Outcome);
        Assert.Equal(5, result.QuantityChange);
        Assert.Equal(22, result.CurrentStock);
        Assert.Equal(900, result.StockAdjustmentId);
    }

    [Fact]
    public async Task Handle_RejectsAnIncrease_WhenNoRestockCostSuggestionIsAvailable()
    {
        var store = StoreWith(1, "Coke", 17);
        store.Setup(x => x.GetRestockUnitCostAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync((decimal?)null);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            new ApplyInventoryCount(store.Object).Handle(1, new InventoryCountApplyRequestDto(22, 17), CancellationToken.None));

        store.Verify(
            x => x.ApplyAsync(
                It.IsAny<long>(), It.IsAny<InventoryCountMovementKind>(), It.IsAny<int>(), It.IsAny<decimal?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AppliesADecrease_AsACorrection_WhenCountedIsBelowCurrent()
    {
        var store = StoreWith(1, "Coke", 32);
        store.Setup(x => x.ApplyAsync(1, InventoryCountMovementKind.Decrease, -4, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InventoryCountAdjustmentApplication(901, 28));

        var result = await new ApplyInventoryCount(store.Object)
            .Handle(1, new InventoryCountApplyRequestDto(28, 32), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(InventoryCountApplyOutcome.Applied, result!.Outcome);
        Assert.Equal(-4, result.QuantityChange);
        Assert.Equal(28, result.CurrentStock);
        Assert.Equal(901, result.StockAdjustmentId);
        store.Verify(x => x.GetRestockUnitCostAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RefusesAStaleCount_WhenTheAuthoritativeCurrentStockHasChanged()
    {
        var store = StoreWith(1, "Coke", 19);

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() =>
            new ApplyInventoryCount(store.Object).Handle(1, new InventoryCountApplyRequestDto(22, 17), CancellationToken.None));

        Assert.Contains("19", exception.Message);
        store.Verify(
            x => x.ApplyAsync(
                It.IsAny<long>(), It.IsAny<InventoryCountMovementKind>(), It.IsAny<int>(), It.IsAny<decimal?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_RejectsNegativeCountedStock()
    {
        var store = StoreWith(1, "Coke", 5);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            new ApplyInventoryCount(store.Object).Handle(1, new InventoryCountApplyRequestDto(-1, 5), CancellationToken.None));
    }
}
