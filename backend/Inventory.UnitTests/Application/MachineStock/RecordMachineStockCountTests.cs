using Inventory.Application.MachineStock;
using Inventory.Domain.Exceptions;
using Inventory.Domain.MachineStock;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStock;

/// <summary>
/// Recording an operator's machine-stock count (issue #56): the instant is the server's, an invalid
/// count persists nothing, and an unknown product is a not-found rather than a stored row.
/// </summary>
public class RecordMachineStockCountTests
{
    private const long MachineId = 42;
    private const long Product = 500;
    private static readonly DateTime Now = new(2026, 3, 10, 2, 0, 0, DateTimeKind.Utc);

    private static RecordMachineStockCount UseCase(Mock<IMachineStockLedgerStore> store) =>
        new(store.Object, new FakeClock(Now));

    [Fact]
    public async Task It_records_the_count_at_the_servers_instant()
    {
        var store = new Mock<IMachineStockLedgerStore>();
        store.Setup(x => x.RecordCountAsync(
                MachineId, Product, 14, Now, "Front row empty", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockCountRecord(7, MachineId, Product, "Coke Zero", 14, Now, "Front row empty"));

        var result = await UseCase(store).Handle(
            MachineId, new MachineStockCountRequestDto(Product, 14, "Front row empty"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(7, result!.CountId);
        Assert.Equal(MachineId, result.MachineId);
        Assert.Equal(Product, result.ProductId);
        Assert.Equal("Coke Zero", result.ProductName);
        Assert.Equal(14, result.CountedQuantity);
        Assert.Equal(Now, result.CountedAtUtc);
        Assert.Equal("Front row empty", result.Notes);
    }

    [Fact]
    public async Task An_unknown_product_records_nothing()
    {
        var store = new Mock<IMachineStockLedgerStore>();
        store.Setup(x => x.RecordCountAsync(
                MachineId, Product, 14, Now, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MachineStockCountRecord?)null);

        var result = await UseCase(store).Handle(
            MachineId, new MachineStockCountRequestDto(Product, 14, null), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_negative_count_is_refused_before_anything_is_written()
    {
        var store = new Mock<IMachineStockLedgerStore>();

        await Assert.ThrowsAsync<DomainValidationException>(() => UseCase(store).Handle(
            MachineId, new MachineStockCountRequestDto(Product, -1, null), CancellationToken.None));

        store.Verify(
            x => x.RecordCountAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task An_over_long_note_is_refused_before_anything_is_written()
    {
        var store = new Mock<IMachineStockLedgerStore>();

        await Assert.ThrowsAsync<DomainValidationException>(() => UseCase(store).Handle(
            MachineId,
            new MachineStockCountRequestDto(Product, 1, new string('n', MachineStockCountPolicy.MaxNotesLength + 1)),
            CancellationToken.None));

        store.Verify(
            x => x.RecordCountAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
