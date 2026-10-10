using Inventory.Application.Nayax;
using Inventory.Application.PickList;
using Inventory.Application.Reorder;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.PickList;

/// <summary>
/// The read-only Pick List projection use case (issue #221): reuses the PAR/<c>MissingStockByMDB</c>
/// arithmetic already authoritative for a machine's current stock and restock target
/// (<c>InventoryApi.Services.MachineService.GetMachineProducts</c>) and the bounded-parallelism
/// pattern already established for fanning <c>GetMachineProductsAsync</c> out across machines
/// (<see cref="CalculateReorderNeeds"/>, issue #47). These tests use deterministic test doubles only.
/// </summary>
public class GetPickListTests
{
    private static Mock<IPickListStorageStockStore> StorageWith(params (long ProductId, string Name, int QuantityInStock)[] products)
    {
        var dictionary = products.ToDictionary(
            p => p.ProductId, p => new PickListStorageProduct(p.ProductId, p.Name, p.QuantityInStock));
        var store = new Mock<IPickListStorageStockStore>();
        store.Setup(x => x.GetStorageProductsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<long, PickListStorageProduct>)dictionary);
        return store;
    }

    [Fact]
    public async Task Handle_ReturnsEmptyResult_ForNoSelectedMachines_WithoutCallingNayaxOrStorage()
    {
        var nayax = new Mock<INayaxLynxClient>(MockBehavior.Strict);
        var storage = new Mock<IPickListStorageStockStore>(MockBehavior.Strict);

        var result = await new GetPickList(nayax.Object, storage.Object).Handle(Array.Empty<long>(), CancellationToken.None);

        Assert.Empty(result.Products);
    }

    [Fact]
    public async Task Handle_ComputesCurrentTargetAndPickQuantity_FromTheSameParAndMissingStockArithmeticAsMachineService()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 4 },
            });
        var storage = StorageWith((100, "Coke Zero", 50));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        var product = Assert.Single(result.Products);
        Assert.Equal(100, product.ProductId);
        Assert.Equal("Coke Zero", product.ProductName);
        var cell = Assert.Single(product.MachineQuantities);
        Assert.Equal(1, cell.MachineId);
        Assert.Equal(6, cell.CurrentQuantity);
        Assert.Equal(10, cell.TargetQuantity);
        Assert.Equal(4, cell.QuantityToPick);
        Assert.Equal(4, product.TotalQuantityToPick);
    }

    [Fact]
    public async Task Handle_ExposesMdbCode_PerMachineProductEntry()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 100, MDBCode = 12, PAR = 10, MissingStockByMDB = 4 },
            });
        var storage = StorageWith((100, "Coke Zero", 50));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        var product = Assert.Single(result.Products);
        Assert.Equal(12, product.MdbCode);
        var cell = Assert.Single(product.MachineQuantities);
        Assert.Equal(12, cell.MdbCode);
    }

    [Fact]
    public async Task Handle_PreservesDifferentMdbCodesPerMachine_ForTheSameProductOnDifferentMachines()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, MDBCode = 12, PAR = 10, MissingStockByMDB = 3 } });
        nayax.Setup(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, MDBCode = 45, PAR = 8, MissingStockByMDB = 5 } });
        var storage = StorageWith((100, "Coke Zero", 100));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1, 2], CancellationToken.None);

        var product = Assert.Single(result.Products);
        // Two genuinely distinct MDB slots on two different machines for the same product: neither
        // the per-machine codes nor the combined pick quantity collapse into one another.
        Assert.Equal(8, product.TotalQuantityToPick);
        var machine1Cell = product.MachineQuantities.Single(mq => mq.MachineId == 1);
        var machine2Cell = product.MachineQuantities.Single(mq => mq.MachineId == 2);
        Assert.Equal(12, machine1Cell.MdbCode);
        Assert.Equal(45, machine2Cell.MdbCode);
        Assert.Equal(3, machine1Cell.QuantityToPick);
        Assert.Equal(5, machine2Cell.QuantityToPick);
    }

    [Fact]
    public async Task Handle_PreservesRepeatedMdbCode_AcrossDifferentMachines()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, MDBCode = 12, PAR = 10, MissingStockByMDB = 3 } });
        nayax.Setup(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, MDBCode = 12, PAR = 10, MissingStockByMDB = 5 } });
        var storage = StorageWith((100, "Coke Zero", 100));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1, 2], CancellationToken.None);

        var product = Assert.Single(result.Products);
        Assert.Equal(12, product.MdbCode);
        Assert.All(product.MachineQuantities, mq => Assert.Equal(12, mq.MdbCode));
        Assert.Equal(8, product.TotalQuantityToPick);
    }

    [Fact]
    public async Task Handle_OrdersProductsAscendingByMdbCode_NumericallyNotLexically()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 100, MDBCode = 10, PAR = 10, MissingStockByMDB = 1 },
                new() { NayaxProductID = 200, MDBCode = 2, PAR = 10, MissingStockByMDB = 1 },
            });
        var storage = StorageWith((100, "Zucchini Chips", 10), (200, "Apple Juice", 10));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        // Numeric 2 sorts before 10, even though "10" sorts before "2" lexically; a sort purely on
        // ProductName would also put Apple Juice (200) first, so this proves the MDB code drives
        // ordering rather than the previous ProductName-only default.
        Assert.Equal([200, 100], result.Products.Select(p => p.ProductId));
    }

    [Fact]
    public async Task Handle_OrdersMissingMdbCodesDeterministically_BeforeAnyCode()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 100, MDBCode = 5, PAR = 10, MissingStockByMDB = 1 },
                new() { NayaxProductID = 200, MDBCode = null, PAR = 10, MissingStockByMDB = 1 },
            });
        var storage = StorageWith((100, "Coke", 10), (200, "Chips", 10));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        Assert.Equal([200, 100], result.Products.Select(p => p.ProductId));
        Assert.Null(result.Products.First().MdbCode);
    }

    [Fact]
    public async Task Handle_BreaksMdbCodeTies_ByProductNameOrdinal()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 100, MDBCode = 7, PAR = 10, MissingStockByMDB = 1 },
                new() { NayaxProductID = 200, MDBCode = 7, PAR = 10, MissingStockByMDB = 1 },
            });
        var storage = StorageWith((100, "Zebra Snack", 10), (200, "Apple Juice", 10));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        Assert.Equal([200, 100], result.Products.Select(p => p.ProductId));
    }

    [Fact]
    public async Task Handle_SumsQuantityToPick_AcrossSelectedMachinesForTheSameProduct()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 3 } });
        nayax.Setup(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, PAR = 8, MissingStockByMDB = 5 } });
        var storage = StorageWith((100, "Coke Zero", 100));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1, 2], CancellationToken.None);

        var product = Assert.Single(result.Products);
        Assert.Equal(8, product.TotalQuantityToPick);
        Assert.Equal(2, product.MachineQuantities.Count);
    }

    [Fact]
    public async Task Handle_SumsQuantityToPick_ForDuplicateProductMappingsWithinTheSameMachine()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 100, PAR = 5, MissingStockByMDB = 2 },
                new() { NayaxProductID = 100, PAR = 5, MissingStockByMDB = 1 },
            });
        var storage = StorageWith((100, "Coke Zero", 50));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        var product = Assert.Single(result.Products);
        var cell = Assert.Single(product.MachineQuantities);
        Assert.Equal(3, cell.QuantityToPick);
        Assert.Equal(10, cell.TargetQuantity);
    }

    [Fact]
    public async Task Handle_ReturnsZeroPickQuantity_WhenAMachineIsAlreadyAtOrAboveItsTarget()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 0 } });
        var storage = StorageWith((100, "Coke Zero", 50));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        var product = Assert.Single(result.Products);
        Assert.Equal(0, product.TotalQuantityToPick);
        Assert.Equal(0, product.StorageShortageQuantity);
        Assert.Equal(0, Assert.Single(product.MachineQuantities).QuantityToPick);
    }

    [Fact]
    public async Task Handle_ProjectsStorageShortage_WhenCombinedPickQuantityExceedsPhysicalStorage()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 8 } });
        nayax.Setup(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 7 } });
        var storage = StorageWith((100, "Coke Zero", 10));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1, 2], CancellationToken.None);

        var product = Assert.Single(result.Products);
        Assert.Equal(15, product.TotalQuantityToPick);
        Assert.Equal(10, product.StorageQuantityInStock);
        Assert.Equal(5, product.StorageShortageQuantity);
    }

    [Fact]
    public async Task Handle_ReportsNoShortage_WhenCombinedPickQuantityDoesNotExceedPhysicalStorage()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 3 } });
        var storage = StorageWith((100, "Coke Zero", 10));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        var product = Assert.Single(result.Products);
        Assert.Equal(0, product.StorageShortageQuantity);
    }

    [Fact]
    public async Task Handle_ExcludesAMachineProduct_WithNoMatchingLocalCatalogueRow()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 3 },
                new() { NayaxProductID = 999, PAR = 10, MissingStockByMDB = 3 },
            });
        var storage = StorageWith((100, "Coke Zero", 10));

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        var product = Assert.Single(result.Products);
        Assert.Equal(100, product.ProductId);
    }

    [Fact]
    public async Task Handle_IgnoresMachineProductsWithNoNayaxProductId()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = null, PAR = 10, MissingStockByMDB = 3 },
            });
        var storage = new Mock<IPickListStorageStockStore>(MockBehavior.Strict);

        var result = await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        Assert.Empty(result.Products);
    }

    [Fact]
    public async Task Handle_DoesNotAlterInventory_ItOnlyReadsThroughThePorts()
    {
        var nayax = new Mock<INayaxLynxClient>(MockBehavior.Strict);
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 3 } });
        var storage = StorageWith((100, "Coke Zero", 10));

        await new GetPickList(nayax.Object, storage.Object).Handle([1], CancellationToken.None);

        nayax.Verify(x => x.CreateMachineProductsAsync(
            It.IsAny<long>(), It.IsAny<List<NayaxMachineProduct>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_PropagatesCallerCancellation()
    {
        var nayax = new Mock<INayaxLynxClient>();
        using var cts = new CancellationTokenSource();
        nayax.Setup(x => x.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns<long, CancellationToken>((_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new List<NayaxMachineProduct>());
            });
        var storage = new Mock<IPickListStorageStockStore>(MockBehavior.Strict);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GetPickList(nayax.Object, storage.Object).Handle([1], cts.Token));
    }

    /// <summary>
    /// Mirrors <c>CalculateReorderNeedsTests.Handle_BoundsPerMachineConcurrency_ToTheConfiguredLimit</c>
    /// (issue #47): proves the same bounded-fan-out guarantee holds for the Pick List's own
    /// per-machine <c>GetMachineProductsAsync</c> calls, deterministically via a shared gate rather
    /// than timing.
    /// </summary>
    [Fact]
    public async Task Handle_BoundsPerMachineConcurrency_ToTheConfiguredLimit()
    {
        const int expectedMaxConcurrency = 2;
        var tracker = new ConcurrencyTrackingNayaxClient(expectedMaxConcurrency);
        var machineIds = Enumerable.Range(1, 6).Select(id => (long)id).ToArray();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns<long, CancellationToken>((_, ct) => tracker.GetMachineProductsAsync(ct));
        var storage = new Mock<IPickListStorageStockStore>();
        storage.Setup(x => x.GetStorageProductsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<long, PickListStorageProduct>)new Dictionary<long, PickListStorageProduct>());

        var useCase = new GetPickList(nayax.Object, storage.Object, expectedMaxConcurrency);
        await useCase.Handle(machineIds, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(expectedMaxConcurrency, tracker.MaxObservedConcurrency);
        Assert.Equal(machineIds.Length, tracker.TotalCalls);
    }

    private sealed class ConcurrencyTrackingNayaxClient
    {
        private readonly int _expectedConcurrency;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _current;
        private int _maxObserved;
        private int _totalCalls;

        public ConcurrencyTrackingNayaxClient(int expectedConcurrency) => _expectedConcurrency = expectedConcurrency;

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);
        public int TotalCalls => Volatile.Read(ref _totalCalls);

        public async Task<List<NayaxMachineProduct>> GetMachineProductsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _totalCalls);
            var current = Interlocked.Increment(ref _current);
            InterlockedMax(ref _maxObserved, current);
            if (current == _expectedConcurrency)
                _gate.TrySetResult();

            await _gate.Task.WaitAsync(ct);
            Interlocked.Decrement(ref _current);
            return new List<NayaxMachineProduct>();
        }

        private static void InterlockedMax(ref int location, int value)
        {
            int initial;
            do
            {
                initial = location;
                if (value <= initial) return;
            } while (Interlocked.CompareExchange(ref location, value, initial) != initial);
        }
    }
}
