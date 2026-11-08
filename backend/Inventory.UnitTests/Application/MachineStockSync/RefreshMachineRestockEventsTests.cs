using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Application.Subscriptions;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Subscriptions;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// The Dashboard's hourly restock-event fetch (issue #1122): it fetches every provider-named machine
/// through the Sync Restock import, at most once per business per hour, never where the reminder
/// itself does not apply, stops on a refusal of the whole business and skips a machine that fails on
/// its own.
/// </summary>
public class RefreshMachineRestockEventsTests
{
    private const int BusinessKey = 1;
    private const int CurrentRevision = 4;
    private const long AvenueHotelId = 11;
    private const long StationCafeId = 12;
    private const long WorkshopFridgeId = 13;
    private const long AvenueHotelNayaxId = 900;
    private const long StationCafeNayaxId = 901;

    private static readonly DateTime NowUtc = new(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Every_provider_named_machine_is_fetched_by_its_nayax_number()
    {
        var fixture = new Fixture();

        var outcome = await fixture.UseCase().Handle(CancellationToken.None);

        Assert.Equal(new MachineRestockFetchOutcome(MachineRestockFetchResult.Fetched, 2, 0), outcome);
        Assert.Equal(
            [new MachineIdentity(AvenueHotelId, AvenueHotelNayaxId), new MachineIdentity(StationCafeId, StationCafeNayaxId)],
            fixture.Fetch.Fetched);
    }

    [Fact]
    public async Task A_second_visit_inside_the_hour_makes_no_nayax_call()
    {
        var fixture = new Fixture();
        await fixture.UseCase().Handle(CancellationToken.None);
        fixture.Fetch.Fetched.Clear();

        fixture.Clock.UtcNow = NowUtc.AddMinutes(30);
        var outcome = await fixture.UseCase().Handle(CancellationToken.None);

        Assert.Same(MachineRestockFetchOutcome.RecentlyFetched, outcome);
        Assert.Empty(fixture.Fetch.Fetched);

        fixture.Clock.UtcNow = NowUtc.AddHours(1);
        await fixture.UseCase().Handle(CancellationToken.None);
        Assert.Equal(2, fixture.Fetch.Fetched.Count);
    }

    [Fact]
    public async Task A_new_credential_revision_fetches_at_once()
    {
        var fixture = new Fixture();
        await fixture.UseCase().Handle(CancellationToken.None);
        fixture.Fetch.Fetched.Clear();

        fixture.Connections = new FakeNayaxConnectionStore("op", "token", NayaxConnectionStatus.Ready, CurrentRevision + 1);
        var outcome = await fixture.UseCase().Handle(CancellationToken.None);

        Assert.Equal(MachineRestockFetchResult.Fetched, outcome.Result);
        Assert.Equal(2, fixture.Fetch.Fetched.Count);
    }

    [Fact]
    public async Task A_support_session_fetches_nothing_and_takes_no_hour()
    {
        var fixture = new Fixture();
        var scope = new BusinessScope();
        scope.ResolveForSupportSession(BusinessId.From(BusinessKey));

        var outcome = await fixture.UseCase(businessScope: scope).Handle(CancellationToken.None);

        Assert.Same(MachineRestockFetchOutcome.NotApplicable, outcome);
        Assert.Empty(fixture.Fetch.Fetched);
        Assert.Equal(0, fixture.Throttle.TrackedKeys);
    }

    [Theory]
    [InlineData(SubscriptionPlan.Free)]
    [InlineData(SubscriptionPlan.Starter)]
    public async Task A_plan_without_the_provider_fetches_nothing(SubscriptionPlan plan)
    {
        var fixture = new Fixture();

        var outcome = await fixture.UseCase(plan: TestCurrentPlan.On(plan)).Handle(CancellationToken.None);

        Assert.Same(MachineRestockFetchOutcome.NotApplicable, outcome);
        Assert.Empty(fixture.Fetch.Fetched);
        Assert.Equal(0, fixture.Throttle.TrackedKeys);
    }

    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task A_connection_the_gate_refuses_fetches_nothing(NayaxConnectionStatus status)
    {
        var fixture = new Fixture
        {
            Connections = new FakeNayaxConnectionStore("op", "token", status, CurrentRevision),
        };

        var outcome = await fixture.UseCase().Handle(CancellationToken.None);

        Assert.Same(MachineRestockFetchOutcome.NotApplicable, outcome);
        Assert.Empty(fixture.Fetch.Fetched);
    }

    [Fact]
    public async Task No_stored_connection_fetches_nothing()
    {
        var fixture = new Fixture { Connections = new FakeNayaxConnectionStore() };

        Assert.Same(MachineRestockFetchOutcome.NotApplicable, await fixture.UseCase().Handle(CancellationToken.None));
        Assert.Empty(fixture.Fetch.Fetched);
    }

    /// <summary>A failure of one machine is that machine's alone: the next one is still fetched.</summary>
    [Fact]
    public async Task A_machine_that_fails_on_its_own_is_skipped()
    {
        var fixture = new Fixture();
        fixture.Fetch.Failures[AvenueHotelId] = new HttpRequestException("timeout");

        var outcome = await fixture.UseCase().Handle(CancellationToken.None);

        Assert.Equal(new MachineRestockFetchOutcome(MachineRestockFetchResult.Fetched, 1, 1), outcome);
        Assert.Equal([new MachineIdentity(StationCafeId, StationCafeNayaxId)], fixture.Fetch.Fetched);
    }

    public static TheoryData<Exception> WholeBusinessRefusals() => new()
    {
        new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention),
        new NayaxPermissionNotGrantedException("machine alerts"),
        new SupportSessionNayaxUnavailableException(),
    };

    /// <summary>
    /// A refusal of the whole business would refuse every remaining machine too, so the run stops
    /// and calls no further machine - and keeps its hour, so the next visit does not ask again.
    /// </summary>
    [Theory]
    [MemberData(nameof(WholeBusinessRefusals))]
    public async Task A_refusal_of_the_whole_business_stops_the_run(Exception refusal)
    {
        var fixture = new Fixture();
        fixture.Fetch.Failures[AvenueHotelId] = refusal;

        var outcome = await fixture.UseCase().Handle(CancellationToken.None);

        Assert.Equal(new MachineRestockFetchOutcome(MachineRestockFetchResult.Stopped, 0, 1), outcome);
        Assert.Empty(fixture.Fetch.Fetched);
        Assert.Equal(1, fixture.Fetch.Attempts);

        fixture.Clock.UtcNow = NowUtc.AddMinutes(5);
        Assert.Same(MachineRestockFetchOutcome.RecentlyFetched, await fixture.UseCase().Handle(CancellationToken.None));
    }

    /// <summary>The visitor who started the fetch left before it finished: the next visit fetches.</summary>
    [Fact]
    public async Task A_cancelled_fetch_gives_the_hour_back()
    {
        var fixture = new Fixture();
        fixture.Fetch.Failures[AvenueHotelId] = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.UseCase().Handle(CancellationToken.None));
        Assert.Equal(0, fixture.Throttle.TrackedKeys);

        fixture.Fetch.Failures.Clear();
        var outcome = await fixture.UseCase().Handle(CancellationToken.None);
        Assert.Equal(MachineRestockFetchResult.Fetched, outcome.Result);
    }

    private sealed class Fixture
    {
        public FakeClock Clock { get; } = new(NowUtc);

        public MachineRestockFetchThrottle Throttle { get; }

        public RecordingRestockEventFetch Fetch { get; } = new();

        public FakeNayaxConnectionStore Connections { get; set; } =
            new("op", "token", NayaxConnectionStatus.Ready, CurrentRevision);

        public FakeMachineRestockSyncCoverageStore Coverage { get; } = new FakeMachineRestockSyncCoverageStore().WithMachines(
            new MachineRestockSyncCoverageRecord(AvenueHotelId, "Avenue Hotel", null, null, 0, null),
            new MachineRestockSyncCoverageRecord(StationCafeId, "Station Cafe", null, null, 0, null),
            // Listed by the coverage read but resolving to no Nayax number: never fetched.
            new MachineRestockSyncCoverageRecord(WorkshopFridgeId, "Workshop Fridge", null, null, 0, null));

        public FakeNayaxMachineLinkStore Links { get; } = FakeNayaxMachineLinkStore.Linking(
            (AvenueHotelNayaxId, AvenueHotelId), (StationCafeNayaxId, StationCafeId));

        public Fixture()
        {
            Throttle = new MachineRestockFetchThrottle(Clock);
        }

        public RefreshMachineRestockEvents UseCase(IBusinessScope? businessScope = null, CurrentPlan? plan = null) =>
            new(
                businessScope ?? ResolvedMember(),
                plan ?? TestCurrentPlan.Unrestricted(),
                Connections,
                new FakeCurrentBusinessProvider(BusinessKey),
                Throttle,
                Coverage,
                Links,
                Fetch,
                Clock,
                new FakeBusinessCalendar(NowUtc));

        private static IBusinessScope ResolvedMember()
        {
            var scope = new BusinessScope();
            scope.Resolve(BusinessId.From(BusinessKey));
            return scope;
        }
    }

    private sealed class RecordingRestockEventFetch : IMachineRestockEventFetch
    {
        public List<MachineIdentity> Fetched { get; } = [];

        public Dictionary<long, Exception> Failures { get; } = [];

        public int Attempts { get; private set; }

        public Task FetchAsync(MachineIdentity machine, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Failures.TryGetValue(machine.LocalMachineId, out var failure))
            {
                throw failure;
            }

            Fetched.Add(machine);
            return Task.CompletedTask;
        }
    }
}
