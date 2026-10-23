using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Application.Sites;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Sites;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// What issue #842 is for, measured on the shape that caused it: one Dashboard visit, over the real
/// use cases the three requests run.
///
/// <para>Opening the Dashboard issues <c>GET api/dashboard/summary</c> and
/// <c>GET api/products/alerts/low-stock</c> - both <see cref="CalculateReorderNeeds"/> - and
/// <c>GET api/sites</c> (<see cref="GetSiteSummaries"/>), largely at the same moment. Each of them
/// fans out over the fleet, so with ten machines that used to be thirty <c>machineProducts</c>
/// requests where ten would do. Here the three requests are genuinely simultaneous - every upstream
/// read is held open until all three have asked for every machine, so none of them can be answered
/// by a finished entry - and what must hold is that Nayax saw one request per machine, plus the one
/// fleet read issue #840 already established.</para>
///
/// <para>Three separate requests, three decorators, three credential providers, three facts stores:
/// what they share is the two process-wide caches, which is exactly what the composition root shares
/// between two simultaneous HTTP requests of one business.</para>
/// </summary>
public class DashboardMachineProductsFanOutTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);
    private static readonly long[] MachineIds = [10, 11, 12];
    private const long SiteId = 7;
    private const int BusinessKey = 1;

    // Obviously fake values. No real token or operator account belongs here.
    private const string OperatorId = "2002736764";
    private const string FakeToken = "fake-token-not-a-real-credential";

    [Fact]
    public async Task One_dashboard_visit_reads_each_machines_products_once()
    {
        // Each of the three requests asks for every machine, and no upstream read completes until all
        // of them have: a request answered from a finished entry would prove the cache but not the
        // single-flight, and the Dashboard's requests overlap in production.
        var gate = new ArrivalGate(MachineIds.Length * 3);
        var upstream = new GatedNayaxLynxClient(gate);
        var machineLists = new NayaxMachineListCache(new FakeClock(Now));
        var machineProducts = new NayaxMachineProductsCache(new FakeClock(Now));

        var summary = Request(upstream, gate, machineLists, machineProducts);
        var lowStock = Request(upstream, gate, machineLists, machineProducts);
        var sites = Request(upstream, gate, machineLists, machineProducts);

        var summaryTask = Task.Run(() => Reorder(summary).Handle(CancellationToken.None));
        var lowStockTask = Task.Run(() => Reorder(lowStock).Handle(CancellationToken.None));
        var sitesTask = Task.Run(() => Sites(sites).Handle(CancellationToken.None));

        var reorderResults = await Task.WhenAll(summaryTask, lowStockTask);
        var siteSummaries = await sitesTask;

        // One request per machine, and one fleet read for the three requests (issue #840).
        Assert.Equal(MachineIds.Length, upstream.MachineProductReads);
        Assert.Equal(MachineIds, upstream.MachinesRead);
        Assert.Equal(1, upstream.FleetReads);

        // And all three answers are the real ones, built from the selections that one read per machine
        // brought back: the whole fleet accounted for, and each machine's need counted once per
        // request rather than once in total.
        Assert.All(reorderResults, result =>
        {
            Assert.Equal(ReorderMode.MachineAware, result.Mode);
            Assert.Equal(MachineIds, result.MachineIds);
            Assert.Equal(
                MachineIds.Length * GatedNayaxLynxClient.MissingStockPerMachine,
                result.MachineReplenishmentNeedByProductId[GatedNayaxLynxClient.ProductId]);
        });

        var site = Assert.Single(siteSummaries);
        Assert.Equal(SiteId, site.SiteId);
        Assert.Equal(MachineIds.Length, site.MachineCount);
    }

    private static CalculateReorderNeeds Reorder(INayaxLynxClient nayax) =>
        new(nayax, new EmptyOutstandingOrders(), FakeNayaxProductLinkStore.EveryIdLinkedToItself());

    private static GetSiteSummaries Sites(INayaxLynxClient nayax)
    {
        var time = new FixedSydneyTime(Now);

        return new GetSiteSummaries(
            nayax,
            new RecordingSiteFactsStore(),
            FakeNayaxProductLinkStore.EveryIdLinkedToItself(),
            new SiteNameResolver(),
            time.Clock,
            time.Calendar,
            TestCurrentCapabilities.Owner);
    }

    /// <summary>
    /// One request's view of the port: its own decorator over its own credential provider and
    /// business scope, with an arrival recorder in front so the test can tell when every request has
    /// asked for every machine.
    /// </summary>
    private static INayaxLynxClient Request(
        INayaxLynxCachedReadClient upstream,
        ArrivalGate gate,
        NayaxMachineListCache machineLists,
        NayaxMachineProductsCache machineProducts)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(BusinessKey));

        return new ArrivalRecordingNayaxLynxClient(
            new CachingNayaxLynxClient(
                upstream,
                new FakeNayaxRequestCredentialProvider(OperatorId, FakeToken),
                scope,
                machineLists,
                machineProducts),
            gate);
    }

    /// <summary>
    /// Counts how many machine-product calls the requests have made and completes once every one of
    /// them has arrived, so "simultaneous" is a state the test waits for rather than a hope about
    /// scheduling. Bounded by <see cref="Wait"/>: a request that never arrives fails the test with
    /// its own message instead of hanging the suite.
    /// </summary>
    private sealed class ArrivalGate
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        private readonly int _expected;
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public ArrivalGate(int expected) => _expected = expected;

        public void Arrive()
        {
            if (Interlocked.Increment(ref _arrivals) >= _expected)
            {
                _allArrived.TrySetResult();
            }
        }

        public async Task WaitForAllAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _allArrived.Task.WaitAsync(Wait, cancellationToken);
            }
            catch (TimeoutException)
            {
                Assert.Fail(
                    $"Only {Volatile.Read(ref _arrivals)} of {_expected} machine-product calls arrived "
                    + "within the wait, so the Dashboard's three requests never overlapped.");
            }
        }
    }

    /// <summary>
    /// The port as one request sees it, recording that this request has asked for a machine's products
    /// before the decorator decides whether that costs a Nayax request.
    /// </summary>
    private sealed class ArrivalRecordingNayaxLynxClient(INayaxLynxClient inner, ArrivalGate gate) : INayaxLynxClient
    {
        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(
            long machineId, CancellationToken ct = default)
        {
            gate.Arrive();
            return inner.GetMachineProductsAsync(machineId, ct);
        }

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
            inner.GetMachinesAsync(ct);

        public Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) =>
            inner.GetMachinesFreshAsync(ct);

        public Task<List<NayaxMachineProduct>> GetMachineProductsFreshAsync(
            long machineId, CancellationToken ct = default) =>
            inner.GetMachineProductsFreshAsync(machineId, ct);

        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
            long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
            inner.CreateMachineProductsAsync(machineId, products, ct);

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
            inner.GetDevicesAsync(ct);

        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) =>
            inner.GetProductsAsync(ct);

        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
            inner.GetProductGroupssAsync(ct);

        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(
            long machineId, CancellationToken ct = default) =>
            inner.GetMachineLastSalesAsync(machineId, ct);

        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) =>
            inner.GetMachineAsync(machineId, ct);

        public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(
            long machineId, CancellationToken ct = default) =>
            inner.GetMachineLastAlertsAsync(machineId, ct);
    }

    /// <summary>
    /// The upstream the decorator reads through: a fleet of three machines on one site, one selection
    /// per machine, and every machine-product read held open until the gate says every request has
    /// asked. It counts what Nayax was actually asked for, which is what this test is about.
    /// </summary>
    private sealed class GatedNayaxLynxClient(ArrivalGate gate) : INayaxLynxCachedReadClient
    {
        public const long ProductId = 500;
        public const int MissingStockPerMachine = 4;

        private readonly List<long> _machinesRead = [];
        private int _fleetReads;
        private int _machineProductReads;

        public int FleetReads => Volatile.Read(ref _fleetReads);

        public int MachineProductReads => Volatile.Read(ref _machineProductReads);

        /// <summary>The machines Nayax was asked about, in ascending order.</summary>
        public IReadOnlyList<long> MachinesRead
        {
            get
            {
                lock (_machinesRead)
                {
                    return _machinesRead.Order().ToList();
                }
            }
        }

        public Task<List<NayaxMachine>> ReadMachinesAsync(
            NayaxRequestCredential credential, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _fleetReads);

            return Task.FromResult(MachineIds
                .Select(machineId => new NayaxMachine
                {
                    MachineID = machineId,
                    CustomerID = SiteId,
                    MachineName = $"Harbour {machineId}",
                })
                .ToList());
        }

        public async Task<List<NayaxMachineProduct>> ReadMachineProductsAsync(
            long machineId, NayaxRequestCredential credential, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _machineProductReads);
            lock (_machinesRead)
            {
                _machinesRead.Add(machineId);
            }

            await gate.WaitForAllAsync(ct);

            return
            [
                new NayaxMachineProduct
                {
                    NayaxProductID = ProductId,
                    MachineID = machineId,
                    ProductName = "Chips",
                    MDBCode = 1,
                    PAR = 10,
                    MissingStockByMDB = MissingStockPerMachine,
                    RetailPrice = 3.50m,
                },
            ];
        }

        public Task<List<NayaxMachineProduct>> WriteMachineProductsAsync(
            long machineId,
            List<NayaxMachineProduct> products,
            NayaxRequestCredential credential,
            CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(
            long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<List<NayaxMachineProduct>> GetMachineProductsFreshAsync(
            long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
            long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(
            long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(
            long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException(Unused);

        private const string Unused =
            "A Dashboard visit reads the fleet and each machine's products, through the members that "
            + "carry the credential the decorator resolved and gated. Nothing else belongs in this test.";
    }

    /// <summary>No outstanding supplier orders: this test is about the Nayax requests, not reordering.</summary>
    private sealed class EmptyOutstandingOrders : IOutstandingSupplierOrderQuantityStore
    {
        public Task<IReadOnlyDictionary<long, decimal>> GetOutstandingQuantitiesByProductAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<long, decimal>>(new Dictionary<long, decimal>());
    }
}
