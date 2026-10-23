using System.Collections.Concurrent;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Application.Sites;
using Inventory.Application.Time;
using Inventory.Domain.Sites;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// Issue #845: both Sites use cases fan their per-machine <c>GetMachineProductsAsync</c> requests out
/// under the same bound <see cref="CalculateReorderNeeds.MaxConcurrentMachineRequests"/> already holds
/// the reorder and pick-list fan-outs to, instead of starting one request per machine at once. Every
/// case runs a fleet larger than that bound, so an unbounded implementation is visible here rather
/// than passing by accident on a fleet no bigger than the limit.
///
/// The bound is the only thing that changes, so the figures each use case reports, the pairing of a
/// machine with the products read for it, and the way a failing machine request or a caller
/// cancellation propagates are pinned here too. The <see cref="ISiteFactsStore"/> ordering rule both
/// use cases document - every database read awaited one at a time, and only once the Nayax fan-out has
/// completed - stays asserted by <see cref="GetSiteSummariesTests"/> and
/// <see cref="GetSiteProductsTests"/>.
/// </summary>
public class SiteNayaxFanOutBoundTests
{
    private const long AlphaSiteId = 43;
    private const long BravoSiteId = 42;

    /// <summary>The bound under test, taken from the one place it is declared.</summary>
    private const int Bound = CalculateReorderNeeds.MaxConcurrentMachineRequests;

    /// <summary>How long a case waits for requests it expects the implementation to start.</summary>
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a case then waits to see whether one more request starts. The bounded implementation
    /// cannot start one at all while the earlier requests are still in flight, so this grace only has
    /// to be long enough to catch an implementation that starts every machine at once - which issues
    /// all of them in one synchronous run, before the first of them has completed.
    /// </summary>
    private static readonly TimeSpan NoFurtherRequestGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 14:30 UTC on Wednesday 11 March 2026, the instant <see cref="GetSiteSummariesTests"/> pins its
    /// business-day periods to; these cases are not about which day that is, only that the revenue
    /// figures are unchanged by the bound.
    /// </summary>
    private static readonly FixedSydneyTime Time =
        new(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

    private static readonly SiteProductActivityFact[] ActiveProducts =
    [
        new(ProductId: 1, IsActive: true),
        new(ProductId: 2, IsActive: true),
    ];

    private static readonly Dictionary<long, SiteProductCostBasis> CostBasis = new()
    {
        [1] = new SiteProductCostBasis("Chips", AverageUnitCost: 2m, HasCostBasis: true),
    };

    private static readonly Dictionary<decimal, decimal> CommissionByPrice = new() { [5m] = 0.5m };

    private static readonly IBusinessCalendar Calendar = new FakeBusinessCalendar(new DateTime(2026, 3, 12));

    /// <summary>
    /// Six machines across two sites. Every request stays in flight until this case releases it, so
    /// the number that started is exactly the number the implementation allows at once: four, with the
    /// remaining two waiting for one of them to finish. An unbounded fan-out starts all six before any
    /// of them completes and fails on the request count.
    /// </summary>
    [Fact]
    public async Task GetSiteSummaries_KeepsAtMostTheBoundedNumberOfMachineRequestsInFlight()
    {
        var nayax = new BlockingFanOutNayaxClient(SummariesFleetMachines(), SummariesProducts());
        var handle = UseCase(nayax, new RecordingSiteFactsStore(ActiveProducts, Sales()))
            .Handle(CancellationToken.None);

        await nayax.StartedAtLeastAsync(Bound).WaitAsync(StartWait);
        await Task.Delay(NoFurtherRequestGrace);
        Assert.Equal(Bound, nayax.MachineProductCallCount);

        nayax.ReleaseAll();
        var summaries = await handle.WaitAsync(StartWait);

        Assert.Equal(2, summaries.Count);
        Assert.Equal(Bound, nayax.MaxConcurrentMachineProductCalls);
        Assert.Equal(6, nayax.MachineProductCallCount);
    }

    /// <summary>
    /// Every figure the site dashboard reports over that fleet, with the fan-out really running
    /// several requests at once (the gated client holds each one until <see cref="Bound"/> of them have
    /// started): the sites in site-name order, each with only its own machines' stock, alerts and
    /// revenue. A bound that dropped or re-attributed a machine's mappings would change the stock
    /// percentage and the alert counts.
    /// </summary>
    [Fact]
    public async Task GetSiteSummaries_ReportsTheSameFiguresUnderTheBoundedFanOut()
    {
        var nayax = SummariesFleet(expectedConcurrentMachineProductCalls: Bound);

        var summaries = await UseCase(nayax, new RecordingSiteFactsStore(ActiveProducts, Sales()))
            .Handle(CancellationToken.None);

        Assert.Equal(new[] { "Alpha", "Bravo" }, summaries.Select(summary => summary.SiteName));

        var alpha = summaries[0];
        Assert.Equal(AlphaSiteId, alpha.SiteId);
        Assert.Equal(3, alpha.MachineCount);
        // Three machines, each stocking product 2 at PAR 5 with all five units missing.
        Assert.Equal(0m, alpha.TotalStockPercentage);
        Assert.Equal(0, alpha.LowProductCount);
        Assert.Equal(1, alpha.EmptyProductCount);
        Assert.Equal(9m, alpha.TodayRevenue);
        Assert.Equal(9m, alpha.CurrentWeekRevenue);
        Assert.Equal(0m, alpha.PreviousComparableWeekRevenue);

        var bravo = summaries[1];
        Assert.Equal(BravoSiteId, bravo.SiteId);
        Assert.Equal(3, bravo.MachineCount);
        // Three machines, each stocking product 1 at PAR 10 with two units missing: 24 of 30.
        Assert.Equal(80m, bravo.TotalStockPercentage);
        Assert.Equal(0, bravo.LowProductCount);
        Assert.Equal(0, bravo.EmptyProductCount);
        Assert.Equal(5m, bravo.TodayRevenue);
        Assert.Equal(5m, bravo.CurrentWeekRevenue);
        Assert.Equal(0m, bravo.PreviousComparableWeekRevenue);
    }

    /// <summary>
    /// A machine request that fails inside the bounded fan-out still propagates as itself - not as the
    /// aggregate of a parallel loop cancelling its siblings, and not as a complete result with a site
    /// missing.
    /// </summary>
    [Fact]
    public async Task GetSiteSummaries_PropagatesAFailingMachineRequestUnderTheBoundedFanOut()
    {
        var nayax = SummariesFleet(failingMachineId: 11);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => UseCase(nayax, new RecordingSiteFactsStore(ActiveProducts)).Handle(CancellationToken.None));

        Assert.Equal("Nayax machine 11 is unavailable", failure.Message);
    }

    [Fact]
    public async Task GetSiteSummaries_PropagatesCallerCancellationUnderTheBoundedFanOut()
    {
        using var cancellation = new CancellationTokenSource();
        var nayax = CancellingFleet(SummariesFleetMachines(), cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => UseCase(nayax, new RecordingSiteFactsStore(ActiveProducts)).Handle(cancellation.Token));
    }

    /// <summary>
    /// The site product preview's own fan-out, over a site with six machines: the same bound, proved
    /// the same way.
    /// </summary>
    [Fact]
    public async Task GetSiteProducts_KeepsAtMostTheBoundedNumberOfMachineRequestsInFlight()
    {
        var nayax = new BlockingFanOutNayaxClient(ProductsFleetMachines(), ProductsFleetProducts());
        var handle = ProductsUseCase(nayax, ProductFacts()).Handle(BravoSiteId, CancellationToken.None);

        await nayax.StartedAtLeastAsync(Bound).WaitAsync(StartWait);
        await Task.Delay(NoFurtherRequestGrace);
        Assert.Equal(Bound, nayax.MachineProductCallCount);

        nayax.ReleaseAll();
        var products = await handle.WaitAsync(StartWait);

        Assert.Single(products);
        Assert.Equal(Bound, nayax.MaxConcurrentMachineProductCalls);
        Assert.Equal(6, nayax.MachineProductCallCount);
    }

    /// <summary>
    /// The row the preview reports over that fleet, including each machine's own MDB code: the
    /// mappings a request returned stay paired with the machine the request was made for, in machine
    /// order, whatever order the bounded requests completed in.
    /// </summary>
    [Fact]
    public async Task GetSiteProducts_ReportsTheSameRowUnderTheBoundedFanOut()
    {
        var nayax = ProductsFleet(expectedConcurrentMachineProductCalls: Bound);

        var product = Assert.Single(
            await ProductsUseCase(nayax, ProductFacts()).Handle(BravoSiteId, CancellationToken.None));

        Assert.Equal("Chips", product.Name);
        Assert.Equal(2m, product.AverageUnitCost);
        Assert.Equal(5m, product.SitePrice);
        // 5.00 retail - 0.50 commission - 2.00 cost - 0.22 fee including GST.
        Assert.Equal(2.28m, product.EstimatedCardProfit);
        // Six machines, each stocking the product at PAR 5 with one unit missing.
        Assert.Equal(24, product.QuantityInStock);
        Assert.Equal(30, product.MaxStock);
        Assert.Equal(1, product.MdbCode);
        Assert.Equal(
            new long[] { 10, 11, 12, 13, 14, 15 },
            product.MachineMdbCodes.Select(code => code.MachineId));
        Assert.Equal(
            new int?[] { 6, 5, 4, 3, 2, 1 },
            product.MachineMdbCodes.Select(code => code.MdbCode));
    }

    [Fact]
    public async Task GetSiteProducts_PropagatesAFailingMachineRequestUnderTheBoundedFanOut()
    {
        var nayax = ProductsFleet(failingMachineId: 11);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProductsUseCase(nayax, ProductFacts()).Handle(BravoSiteId, CancellationToken.None));

        Assert.Equal("Nayax machine 11 is unavailable", failure.Message);
    }

    [Fact]
    public async Task GetSiteProducts_PropagatesCallerCancellationUnderTheBoundedFanOut()
    {
        using var cancellation = new CancellationTokenSource();
        var nayax = CancellingFleet(ProductsFleetMachines(), cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProductsUseCase(nayax, ProductFacts()).Handle(BravoSiteId, cancellation.Token));
    }

    private static GetSiteSummaries UseCase(INayaxLynxClient nayax, ISiteFactsStore facts) =>
        new(
            nayax,
            facts,
            FakeNayaxProductLinkStore.EveryIdLinkedToItself(),
            new SiteNameResolver(),
            Time.Clock,
            Time.Calendar,
            TestCurrentCapabilities.Owner);

    private static GetSiteProducts ProductsUseCase(INayaxLynxClient nayax, ISiteFactsStore facts) =>
        new(nayax, facts, FakeNayaxProductLinkStore.EveryIdLinkedToItself(), Calendar, TestCurrentCapabilities.Owner);

    private static RecordingSiteFactsStore ProductFacts() =>
        new(costBasis: CostBasis, commissionByPrice: CommissionByPrice, feeExGst: 0.20m);

    private static NayaxMachine[] SummariesFleetMachines() =>
    [
        new() { MachineID = 10, CustomerID = BravoSiteId, MachineName = "Bravo Left" },
        new() { MachineID = 11, CustomerID = BravoSiteId, MachineName = "Bravo Middle" },
        new() { MachineID = 12, CustomerID = BravoSiteId, MachineName = "Bravo Right" },
        new() { MachineID = 20, CustomerID = AlphaSiteId, MachineName = "Alpha One" },
        new() { MachineID = 21, CustomerID = AlphaSiteId, MachineName = "Alpha Two" },
        new() { MachineID = 22, CustomerID = AlphaSiteId, MachineName = "Alpha Three" },
    ];

    private static Dictionary<long, List<NayaxMachineProduct>> SummariesProducts() =>
        new()
        {
            [10] = [Mapping(10, nayaxProductId: 1, par: 10, missingStockByMdb: 2)],
            [11] = [Mapping(11, nayaxProductId: 1, par: 10, missingStockByMdb: 2)],
            [12] = [Mapping(12, nayaxProductId: 1, par: 10, missingStockByMdb: 2)],
            [20] = [Mapping(20, nayaxProductId: 2, par: 5, missingStockByMdb: 5)],
            [21] = [Mapping(21, nayaxProductId: 2, par: 5, missingStockByMdb: 5)],
            [22] = [Mapping(22, nayaxProductId: 2, par: 5, missingStockByMdb: 5)],
        };

    private static RecordingNayaxLynxClient SummariesFleet(
        int expectedConcurrentMachineProductCalls = 0, long? failingMachineId = null) =>
        new(
            SummariesFleetMachines(),
            SummariesProducts(),
            expectedConcurrentMachineProductCalls,
            failingMachineId);

    /// <summary>Six machines at one site, each carrying the same product with its own MDB code.</summary>
    private static NayaxMachine[] ProductsFleetMachines() =>
        [.. MachineIds().Select(machineId => new NayaxMachine
        {
            MachineID = machineId,
            CustomerID = BravoSiteId,
        })];

    private static Dictionary<long, List<NayaxMachineProduct>> ProductsFleetProducts() =>
        MachineIds().ToDictionary(
            machineId => machineId,
            machineId => new List<NayaxMachineProduct>
            {
                new()
                {
                    MachineID = machineId,
                    NayaxProductID = 1,
                    RetailPrice = 5m,
                    PAR = 5,
                    MissingStockByMDB = 1,
                    // Descending with the machine id, so the lowest code is not the first one read.
                    MDBCode = (int)(16 - machineId),
                },
            });

    private static RecordingNayaxLynxClient ProductsFleet(
        int expectedConcurrentMachineProductCalls = 0, long? failingMachineId = null) =>
        new(
            ProductsFleetMachines(),
            ProductsFleetProducts(),
            expectedConcurrentMachineProductCalls,
            failingMachineId);

    private static IEnumerable<long> MachineIds() => Enumerable.Range(10, 6).Select(machineId => (long)machineId);

    /// <summary>
    /// A fleet whose first machine-product request cancels the caller's token and then observes it,
    /// which is how <c>GetPickListTests</c> pins the same guarantee for the pick-list fan-out.
    /// </summary>
    private static INayaxLynxClient CancellingFleet(
        IReadOnlyList<NayaxMachine> machines, CancellationTokenSource cancellation)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. machines]);
        nayax.Setup(client => client.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns<long, CancellationToken>((_, machineCancellationToken) =>
            {
                cancellation.Cancel();
                machineCancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new List<NayaxMachineProduct>());
            });
        return nayax.Object;
    }

    private static NayaxMachineProduct Mapping(long machineId, long nayaxProductId, int par, int missingStockByMdb) =>
        new()
        {
            MachineID = machineId,
            NayaxProductID = nayaxProductId,
            PAR = par,
            MissingStockByMDB = missingStockByMdb,
            VendOutAlertThreshold = 1,
        };

    /// <summary>One completed sale at each site, so the revenue figures stay attributable per site.</summary>
    private static SiteCompletedSaleFact[] Sales() =>
    [
        new(MachineId: 10, SettlementValue: 5m, MachineAuthorizationTime: Time.NowUtc),
        new(MachineId: 20, SettlementValue: 9m, MachineAuthorizationTime: Time.NowUtc),
    ];

    /// <summary>
    /// A fleet whose machine-product requests all stay in flight until the test releases them. That is
    /// what makes the bound observable: a request that is still running cannot be replaced by the next
    /// one, so the number of requests that started is the number the implementation allows at once.
    /// <see cref="RecordingNayaxLynxClient"/>'s gate cannot show this, because it releases a request as
    /// soon as the expected number have started and the released one then finishes before the caller
    /// starts the next.
    /// </summary>
    private sealed class BlockingFanOutNayaxClient : INayaxLynxClient
    {
        private readonly List<NayaxMachine> _machines;
        private readonly IReadOnlyDictionary<long, List<NayaxMachineProduct>> _productsByMachine;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _startedSignals = new();
        private int _started;
        private int _inFlight;
        private int _maxInFlight;

        public BlockingFanOutNayaxClient(
            IEnumerable<NayaxMachine> machines,
            IReadOnlyDictionary<long, List<NayaxMachineProduct>> productsByMachine)
        {
            _machines = machines.ToList();
            _productsByMachine = productsByMachine;
        }

        public int MaxConcurrentMachineProductCalls => Volatile.Read(ref _maxInFlight);

        public int MachineProductCallCount => Volatile.Read(ref _started);

        /// <summary>Completes once <paramref name="count"/> machine-product requests have started.</summary>
        public Task StartedAtLeastAsync(int count)
        {
            var signal = _startedSignals.GetOrAdd(
                count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Volatile.Read(ref _started) >= count) signal.TrySetResult();
            return signal.Task;
        }

        /// <summary>Lets every started and every later request complete.</summary>
        public void ReleaseAll() => _release.TrySetResult();

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
            Task.FromResult(_machines);

        public async Task<List<NayaxMachineProduct>> GetMachineProductsAsync(
            long machineId, CancellationToken ct = default)
        {
            var started = Interlocked.Increment(ref _started);
            InterlockedMax(ref _maxInFlight, Interlocked.Increment(ref _inFlight));
            foreach (var (count, signal) in _startedSignals)
            {
                if (started >= count) signal.TrySetResult();
            }

            await _release.Task.WaitAsync(ct);

            Interlocked.Decrement(ref _inFlight);
            return _productsByMachine.TryGetValue(machineId, out var products) ? products : [];
        }

        private static void InterlockedMax(ref int location, int value)
        {
            int initial;
            do
            {
                initial = Volatile.Read(ref location);
                if (value <= initial) return;
            } while (Interlocked.CompareExchange(ref location, value, initial) != initial);
        }

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
