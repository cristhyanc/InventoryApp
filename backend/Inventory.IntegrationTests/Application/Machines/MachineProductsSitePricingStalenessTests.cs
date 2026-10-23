using System.Net;
using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Sites;
using Inventory.Application.Tenancy;
using Inventory.Domain.Sites;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Products;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Tests.Infrastructure.Nayax;
using Xunit;

namespace InventoryApi.Tests.Application.Machines;

/// <summary>
/// What issue #846 costs and what it buys, through the <em>real</em> machine-list cache of issue #840
/// rather than a stand-in for it: <see cref="ListMachineProducts"/> takes the machine's site from
/// <c>GetMachinesAsync</c>, so the machine page makes one Nayax machine read instead of two - and a
/// machine moved to another site in Nayax keeps the previous site's pricing until that cached list
/// expires ten minutes later (the staleness Cristhyan accepted on 23 October 2026).
///
/// <para>The whole composition is the real one apart from the clock and what Nayax answers: the
/// <see cref="MachineListCachingNayaxLynxClient"/> decorator, its
/// <see cref="NayaxMachineListCache"/> over a <see cref="FakeClock"/> a test moves itself, and the
/// real <see cref="ResolveMachineProductPricing"/>. A test that faked the ten minutes could not say
/// whether the pricing catches up at all.</para>
/// </summary>
public class MachineProductsSitePricingStalenessTests
{
    private const long MachineId = 5;
    private const long SiteA = 91;
    private const long SiteB = 92;

    // Obviously fake values. No real token or operator account belongs here.
    private const string OperatorId = "2002736764";
    private const string FakeToken = "fake-token-not-a-real-credential";

    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Site A's pricing, then site A's pricing again while the list is still held, then site B's on
    /// the first load after it expires - and one machine-list read per expiry rather than per load.
    /// The machine read is never made at all: the page's dashboard request is the one that makes it.
    /// </summary>
    [Fact]
    public async Task Site_pricing_follows_the_cached_machine_list_and_catches_up_once_it_expires()
    {
        var clock = new FakeClock(Now);
        var nayax = new ScriptedNayaxClient(SiteA);
        var listing = Listing(nayax, clock);

        var atSiteA = Assert.Single(await listing.Handle(MachineId, CancellationToken.None));

        // The machine moves to site B in Nayax. Nothing tells this application about it.
        nayax.MoveTo(SiteB);
        clock.UtcNow = Now.Add(NayaxMachineListCache.Lifetime) - TimeSpan.FromSeconds(1);
        var stillAtSiteA = Assert.Single(await listing.Handle(MachineId, CancellationToken.None));

        clock.UtcNow = Now + NayaxMachineListCache.Lifetime;
        var atSiteB = Assert.Single(await listing.Handle(MachineId, CancellationToken.None));

        // 5 - 2 cost - 0.50 site A commission - 0.22 fee including GST, and the break-even price at
        // site A's 10c per dollar; then site B's 1.00 commission and 20c per dollar.
        Assert.Equal(2.28m, atSiteA.SuggestedNetValue);
        Assert.Equal(5.55m, atSiteA.SuggestedPriceValue);
        Assert.Equal(2.28m, stillAtSiteA.SuggestedNetValue);
        Assert.Equal(5.55m, stillAtSiteA.SuggestedPriceValue);
        Assert.Equal(1.78m, atSiteB.SuggestedNetValue);
        Assert.Equal(7.4m, atSiteB.SuggestedPriceValue);

        // Two machine-list reads for three loads - the second load was held - and no machine read.
        Assert.Equal(2, nayax.MachineListReads);
        Assert.Equal(0, nayax.MachineReads);
        Assert.Equal(3, nayax.MachineProductReads);
    }

    /// <summary>
    /// Everything but the site is the same listing: the slot facts and the rows a load answers with
    /// are identical before and after the cached list catches up, so the staleness is confined to the
    /// site-based suggestions.
    /// </summary>
    [Fact]
    public async Task Nothing_but_the_site_based_suggestions_goes_stale()
    {
        var clock = new FakeClock(Now);
        var nayax = new ScriptedNayaxClient(SiteA);
        var listing = Listing(nayax, clock);

        var before = Assert.Single(await listing.Handle(MachineId, CancellationToken.None));

        nayax.MoveTo(SiteB);
        clock.UtcNow = Now + NayaxMachineListCache.Lifetime;
        var after = Assert.Single(await listing.Handle(MachineId, CancellationToken.None));

        Assert.Equal(
            before with { SuggestedNetValue = after.SuggestedNetValue, SuggestedPriceValue = after.SuggestedPriceValue },
            after);
    }

    /// <summary>
    /// A machine the cached list does not hold - added in Nayax within the last ten minutes - is read
    /// on its own and priced by what that read says, every load: the decorator caches the machine list
    /// and nothing else.
    /// </summary>
    [Fact]
    public async Task A_machine_the_list_does_not_hold_is_read_itself_on_every_load()
    {
        var clock = new FakeClock(Now);
        var nayax = new ScriptedNayaxClient(SiteA, listTheMachine: false);
        var listing = Listing(nayax, clock);

        var first = Assert.Single(await listing.Handle(MachineId, CancellationToken.None));
        var second = Assert.Single(await listing.Handle(MachineId, CancellationToken.None));

        Assert.Equal(2.28m, first.SuggestedNetValue);
        Assert.Equal(2.28m, second.SuggestedNetValue);
        Assert.Equal(2, nayax.MachineReads);
    }

    /// <summary>
    /// And a machine Nayax does not know still fails as the controlled upstream failure the HTTP
    /// boundary turns into a 502, with the machine products never read - the answer issue #846 must
    /// leave exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_machine_nayax_does_not_know_still_fails_as_the_upstream_failure()
    {
        var clock = new FakeClock(Now);
        var unknownToNayax = new NayaxUpstreamException(
            "GetMachineAsync", HttpMethod.Get, $"machines/{MachineId}/", HttpStatusCode.NotFound);
        var nayax = new ScriptedNayaxClient(SiteA, listTheMachine: false) { MachineReadFailure = unknownToNayax };

        var thrown = await Assert.ThrowsAsync<NayaxUpstreamException>(() =>
            Listing(nayax, clock).Handle(MachineId, CancellationToken.None));

        Assert.Same(unknownToNayax, thrown);
        Assert.Equal(0, nayax.MachineProductReads);
    }

    /// <summary>
    /// The machine product listing over the port as the composition root registers it: the
    /// machine-list cache in front of the Nayax client, one business, one credential revision.
    /// </summary>
    private static ListMachineProducts Listing(ScriptedNayaxClient nayax, FakeClock clock)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(1));

        var decorated = new MachineListCachingNayaxLynxClient(
            nayax,
            new FakeNayaxRequestCredentialProvider(OperatorId, FakeToken),
            scope,
            new NayaxMachineListCache(clock));

        return new ListMachineProducts(
            decorated,
            new FakeProductCatalogStore(FakeProductCatalogStore.Product(200, "Coke", averageUnitCost: 2m)),
            FakeNayaxProductLinkStore.EveryIdLinkedToItself(),
            new ResolveMachineProductPricing(
                new PerSiteCommissionFactsStore(), new FakeBusinessCalendar(new DateTime(2026, 3, 12))));
    }

    /// <summary>
    /// A commission port whose answer depends on the site it is asked about, which is what makes the
    /// site a row was priced with observable in the row itself.
    /// </summary>
    private sealed class PerSiteCommissionFactsStore : ISiteFactsStore
    {
        private static readonly Dictionary<long, IReadOnlyDictionary<decimal, decimal>> CommissionBySite = new()
        {
            [SiteA] = new Dictionary<decimal, decimal> { [5m] = 0.50m, [1m] = 0.10m },
            [SiteB] = new Dictionary<decimal, decimal> { [5m] = 1.00m, [1m] = 0.20m },
        };

        public Task<SiteCardCommissionResolution> ResolveCardCommissionAsync(
            long siteId,
            DateTime asOfDate,
            IReadOnlyCollection<decimal> candidateRetailPrices,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SiteCardCommissionResolution(
                ConfigurationUnavailable: false, CommissionBySite[siteId]));

        public Task<decimal?> ResolveEffectiveFeeExGstAsync(DateTime asOfDate, CancellationToken cancellationToken) =>
            Task.FromResult<decimal?>(0.2m);

        public Task<IReadOnlyList<SiteProductActivityFact>> GetProductActivityAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SiteCompletedSaleFact>> GetRecentCompletedSalesAsync(
            IReadOnlyCollection<long> machineIds, DateTime since, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<long, SiteProductCostBasis>> GetProductCostBasisAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A stand-in for <see cref="NayaxLynxClient"/> whose fleet a test moves between sites and which
    /// counts each read, so a test can say which read an answer came from and how many requests a
    /// load spent.
    ///
    /// <para>Both port machine-list members throw, exactly as
    /// <c>MachineListCachingNayaxLynxClientTests</c>' double does: the decorator must read the
    /// upstream list through <see cref="INayaxLynxMachineListClient.ReadMachinesAsync"/>, with the
    /// credential it resolved and gated itself.</para>
    /// </summary>
    private sealed class ScriptedNayaxClient : INayaxLynxMachineListClient
    {
        private readonly bool _listTheMachine;
        private long _siteId;
        private int _machineListReads;
        private int _machineReads;
        private int _machineProductReads;

        public ScriptedNayaxClient(long siteId, bool listTheMachine = true)
        {
            _siteId = siteId;
            _listTheMachine = listTheMachine;
        }

        public int MachineListReads => Volatile.Read(ref _machineListReads);

        public int MachineReads => Volatile.Read(ref _machineReads);

        public int MachineProductReads => Volatile.Read(ref _machineProductReads);

        /// <summary>What a single-machine read throws, as it does for a machine Nayax does not know.</summary>
        public Exception? MachineReadFailure { get; init; }

        /// <summary>The machine is at another site in Nayax from now on.</summary>
        public void MoveTo(long siteId) => Volatile.Write(ref _siteId, siteId);

        public Task<List<NayaxMachine>> ReadMachinesAsync(
            NayaxRequestCredential credential, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _machineListReads);

            return Task.FromResult<List<NayaxMachine>>(_listTheMachine
                ? [Machine()]
                : [new NayaxMachine { MachineID = 999, CustomerID = 404 }]);
        }

        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _machineReads);

            return MachineReadFailure is { } failure
                ? Task.FromException<NayaxMachine>(failure)
                : Task.FromResult(Machine());
        }

        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(
            long machineId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _machineProductReads);

            return Task.FromResult<List<NayaxMachineProduct>>(
            [
                new NayaxMachineProduct
                {
                    NayaxProductID = 200,
                    MachineID = machineId,
                    RetailPrice = 5m,
                    MDBCode = 12,
                    PAR = 10,
                    MissingStockByMDB = 4,
                },
            ]);
        }

        private NayaxMachine Machine() => new()
        {
            MachineID = MachineId,
            MachineName = "Machine 5",
            CustomerID = Volatile.Read(ref _siteId),
        };

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(
                "The decorator reads the upstream machine list through ReadMachinesAsync, with the "
                + "credential it resolved and gated itself.");

        public Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(
                "The decorator reads the upstream machine list through ReadMachinesAsync, with the "
                + "credential it resolved and gated itself.");

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
            long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(
            long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(
            long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
