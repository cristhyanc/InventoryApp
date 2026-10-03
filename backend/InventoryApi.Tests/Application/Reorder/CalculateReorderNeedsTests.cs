using System.Net;
using Inventory.Application;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Infrastructure.Nayax;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Reorder;

/// <summary>
/// Issue #47: <see cref="CalculateReorderNeeds"/> replaces the sequential per-machine loop that used
/// to live in <c>InventoryApi.Services.ProductService.LowStock</c> with bounded-parallelism fan-out
/// through <see cref="INayaxLynxClient"/>, combined with outstanding supplier-order quantity from
/// <see cref="IOutstandingSupplierOrderQuantityStore"/>. These tests use deterministic test doubles
/// only; none depend on live Nayax timing.
/// </summary>
public class CalculateReorderNeedsTests
{
    private static Mock<IOutstandingSupplierOrderQuantityStore> EmptyOutstandingStore()
    {
        var store = new Mock<IOutstandingSupplierOrderQuantityStore>();
        store.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());
        return store;
    }

    private static Mock<INayaxLynxClient> NayaxWithMachines(params long[] machineIds)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(machineIds.Select(id => new NayaxMachine { MachineID = id }).ToList());
        return nayax;
    }

    /// <summary>
    /// Guards the DI wiring itself: <see cref="CalculateReorderNeeds"/> has a second public
    /// constructor (for the concurrency-bound test above) whose extra <c>int</c> parameter is never
    /// registered as a service. If the DI container ever treated that as ambiguous rather than falling
    /// back to the two-argument constructor, every consumer resolved through
    /// <c>AddApplicationServices</c> - including <c>ListLowStockProducts</c>, which
    /// <c>ProductsController</c> depends on - would fail at startup.
    /// </summary>
    [Fact]
    public void AddApplicationServices_ResolvesCalculateReorderNeeds_DespiteItsSecondTestOnlyConstructor()
    {
        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddScoped(_ => new Mock<INayaxLynxClient>().Object);
        services.AddScoped(_ => EmptyOutstandingStore().Object);

        using var provider = services.BuildServiceProvider();

        var useCase = provider.GetRequiredService<CalculateReorderNeeds>();

        Assert.NotNull(useCase);
    }

    [Fact]
    public async Task Handle_SumsMachineReplenishmentNeed_AcrossMultipleMachinesForTheSameProduct()
    {
        var nayax = NayaxWithMachines(1, 2);
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, MissingStockByMDB = 3 } });
        nayax.Setup(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, MissingStockByMDB = 5 } });

        var result = await new CalculateReorderNeeds(nayax.Object, EmptyOutstandingStore().Object).Handle(CancellationToken.None);

        Assert.Equal(8, result.MachineReplenishmentNeedByProductId[100]);
    }

    [Fact]
    public async Task Handle_SumsMachineReplenishmentNeed_ForDuplicateProductMappingsWithinTheSameMachine()
    {
        var nayax = NayaxWithMachines(1);
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 100, MissingStockByMDB = 2 },
                new() { NayaxProductID = 100, MissingStockByMDB = 4 },
            });

        var result = await new CalculateReorderNeeds(nayax.Object, EmptyOutstandingStore().Object).Handle(CancellationToken.None);

        Assert.Equal(6, result.MachineReplenishmentNeedByProductId[100]);
    }

    [Fact]
    public async Task Handle_IgnoresMachineProductsWithNoLocalProductMapping()
    {
        var nayax = NayaxWithMachines(1);
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = null, MissingStockByMDB = 9 },
                new() { NayaxProductID = 100, MissingStockByMDB = 2 },
            });

        var result = await new CalculateReorderNeeds(nayax.Object, EmptyOutstandingStore().Object).Handle(CancellationToken.None);

        Assert.Single(result.MachineReplenishmentNeedByProductId);
        Assert.Equal(2, result.MachineReplenishmentNeedByProductId[100]);
    }

    [Fact]
    public async Task Handle_ReturnsOutstandingOrderQuantitiesFromTheStorePort()
    {
        var nayax = NayaxWithMachines();
        var store = new Mock<IOutstandingSupplierOrderQuantityStore>();
        store.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal> { [7] = 12m });

        var result = await new CalculateReorderNeeds(nayax.Object, store.Object).Handle(CancellationToken.None);

        Assert.Equal(12m, result.OnOrderQuantityByProductId[7]);
    }

    [Fact]
    public async Task Handle_PropagatesATypedNayaxUpstreamFailure_WithoutReturningAPartialResult()
    {
        var nayax = NayaxWithMachines(1, 2);
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>());
        nayax.Setup(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NayaxUpstreamException(
                "GetMachineProducts", HttpMethod.Get, "machines/2/machineProducts", HttpStatusCode.BadGateway));
        var store = new Mock<IOutstandingSupplierOrderQuantityStore>(MockBehavior.Strict);

        await Assert.ThrowsAsync<NayaxUpstreamException>(
            () => new CalculateReorderNeeds(nayax.Object, store.Object).Handle(CancellationToken.None));

        store.Verify(
            x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_PropagatesCallerCancellation()
    {
        var nayax = new Mock<INayaxLynxClient>();
        using var cts = new CancellationTokenSource();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new List<NayaxMachine>());
            });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new CalculateReorderNeeds(nayax.Object, EmptyOutstandingStore().Object).Handle(cts.Token));
    }

    /// <summary>
    /// Every call blocks on a shared gate that only releases once exactly
    /// <c>expectedMaxConcurrency</c> calls are in flight simultaneously. Because
    /// <c>Parallel.ForEachAsync</c>'s <c>MaxDegreeOfParallelism</c> is a hard scheduling bound enforced
    /// by the runtime (not a race that depends on timing), this deterministically proves both that the
    /// bound is never exceeded (a fifth machine's call cannot start until one of the first four
    /// completes, so it can never contribute to a fifth simultaneous arrival) and that real parallelism
    /// up to the bound actually happens (the gate could not otherwise open at all).
    /// </summary>
    [Fact]
    public async Task Handle_BoundsPerMachineConcurrency_ToTheConfiguredLimit()
    {
        const int expectedMaxConcurrency = 2;
        var tracker = new ConcurrencyTrackingNayaxClient(expectedMaxConcurrency);
        var machineIds = Enumerable.Range(1, 6).Select(id => (long)id).ToArray();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(machineIds.Select(id => new NayaxMachine { MachineID = id }).ToList());
        nayax.Setup(x => x.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns<long, CancellationToken>((_, ct) => tracker.GetMachineProductsAsync(ct));

        var useCase = new CalculateReorderNeeds(nayax.Object, EmptyOutstandingStore().Object, expectedMaxConcurrency);
        await useCase.Handle(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(expectedMaxConcurrency, tracker.MaxObservedConcurrency);
        Assert.Equal(machineIds.Length, tracker.TotalCalls);
    }

    /// <summary>
    /// A test-only gate: releases every waiter as soon as the configured concurrency has arrived
    /// concurrently, proving that many were in flight together without ever needing to exceed that
    /// number.
    /// </summary>
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
