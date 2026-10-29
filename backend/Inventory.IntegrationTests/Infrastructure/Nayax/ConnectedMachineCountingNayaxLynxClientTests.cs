using Inventory.Application.ConnectedMachines;
using Inventory.Application.Nayax;
using Inventory.Domain.ConnectedMachines;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Http;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The refresh path of issue #978: the decorator the composition root puts outside the machine-list
/// cache, which records the day's connected-machine snapshot from a complete fleet read the
/// application was performing anyway.
///
/// <para>Three properties matter, and each is a thing this decorator could otherwise quietly break.
/// A fresh whole-fleet read is offered to the count, so the count costs <strong>no extra Nayax call
/// on a page load</strong>. A <em>cached</em> machine list is not - an answer up to ten minutes old is
/// not a read of the account, and dating a count to the wrong moment is worse than not taking one.
/// And a snapshot that cannot be recorded never reaches the caller: the read succeeded, the caller is
/// a sales sync or a dashboard refresh, and billing evidence must not be able to fail a feature.</para>
/// </summary>
public class ConnectedMachineCountingNayaxLynxClientTests
{
    [Fact]
    public async Task A_complete_fresh_fleet_read_is_recorded_as_the_days_snapshot()
    {
        var recorder = new RecordingSnapshotRecorder();
        var inner = new FleetNayaxLynxClient(101, 102, 103);
        var decorated = Decorate(inner, recorder);

        var machines = await decorated.GetMachinesFreshAsync(CancellationToken.None);

        Assert.Equal(3, machines.Count);

        var offered = Assert.Single(recorder.Offered);
        Assert.True(offered.IsComplete);
        Assert.Equal(new long[] { 101, 102, 103 }, offered.ProviderMachineIds);
    }

    /// <summary>
    /// A cached machine list is not a read of the account, so nothing is recorded from it - which is
    /// also what keeps a page that reads the list five times from offering five snapshots.
    /// </summary>
    [Fact]
    public async Task A_cached_machine_list_is_not_recorded()
    {
        var recorder = new RecordingSnapshotRecorder();
        var decorated = Decorate(new FleetNayaxLynxClient(101), recorder);

        await decorated.GetMachinesAsync(CancellationToken.None);

        Assert.Empty(recorder.Offered);
    }

    /// <summary>
    /// Recording never breaks the caller: the fleet read already succeeded, so a failure to store the
    /// evidence is abandoned and the machines are returned exactly as they were read. The next
    /// complete read of the day is the retry.
    /// </summary>
    [Fact]
    public async Task A_failure_to_record_leaves_the_caller_unaffected()
    {
        var recorder = new RecordingSnapshotRecorder
        {
            Failure = new InvalidOperationException("The snapshot could not be written."),
        };
        var decorated = Decorate(new FleetNayaxLynxClient(101, 102), recorder);

        var machines = await decorated.GetMachinesFreshAsync(CancellationToken.None);

        Assert.Equal(2, machines.Count);
        Assert.Single(recorder.Offered);
    }

    /// <summary>
    /// Every other member of the port is pure delegation: this decorator exists to notice one read,
    /// not to change the client.
    /// </summary>
    [Fact]
    public async Task Everything_else_reaches_the_client_untouched()
    {
        var inner = new FleetNayaxLynxClient(101);
        var decorated = Decorate(inner, new RecordingSnapshotRecorder());

        await decorated.GetDevicesAsync(CancellationToken.None);
        await decorated.GetProductsAsync(CancellationToken.None);
        await decorated.GetProductGroupssAsync(CancellationToken.None);
        await decorated.GetMachineAsync(101, CancellationToken.None);
        await decorated.GetMachineProductsAsync(101, CancellationToken.None);
        await decorated.GetMachineProductsFreshAsync(101, CancellationToken.None);
        await decorated.CreateMachineProductsAsync(101, [], CancellationToken.None);
        await decorated.GetMachineLastSalesAsync(101, CancellationToken.None);
        await decorated.GetMachineLastAlertsAsync(101, CancellationToken.None);

        Assert.Equal(9, inner.OtherCalls);
    }

    private static ConnectedMachineCountingNayaxLynxClient Decorate(
        INayaxLynxClient inner,
        IConnectedMachineSnapshotRecorder recorder) =>
        new(inner, recorder, new CapturingLogger<ConnectedMachineCountingNayaxLynxClient>());

    /// <summary>
    /// The recorder as this decorator sees it: what it was offered, and whether recording failed.
    /// </summary>
    private sealed class RecordingSnapshotRecorder : IConnectedMachineSnapshotRecorder
    {
        public List<ConnectedMachineFleetRead> Offered { get; } = [];

        public Exception? Failure { get; set; }

        public Task<ConnectedMachineSnapshotOutcome> RecordFromFleetReadAsync(
            ConnectedMachineFleetRead read,
            CancellationToken cancellationToken)
        {
            Offered.Add(read);

            return Failure is not null
                ? throw Failure
                : Task.FromResult(ConnectedMachineSnapshotOutcome.Recorded);
        }

        public Task<ConnectedMachineSnapshotAttempt> ReadFleetAndRecordAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The refresh path never asks the recorder to read the fleet itself.");
    }

    /// <summary>A client answering one fixed fleet, counting everything it was asked.</summary>
    private sealed class FleetNayaxLynxClient : INayaxLynxClient
    {
        private readonly long[] _machineIds;

        public FleetNayaxLynxClient(params long[] machineIds) => _machineIds = machineIds;

        public int OtherCalls { get; private set; }

        public Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) =>
            Task.FromResult(_machineIds.Select(id => new NayaxMachine { MachineID = id }).ToList());

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
            GetMachinesFreshAsync(ct);

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
            Counted<NayaxDevice>();

        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(
            long machineId, CancellationToken ct = default) =>
            Counted<NayaxMachineProduct>();

        public Task<List<NayaxMachineProduct>> GetMachineProductsFreshAsync(
            long machineId, CancellationToken ct = default) =>
            Counted<NayaxMachineProduct>();

        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
            long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
            Counted<NayaxMachineProduct>();

        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) =>
            Counted<NayaxProduct>();

        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
            Counted<NayaxProductGroup>();

        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(
            long machineId, CancellationToken ct = default) =>
            Counted<NayaxLastSalesReport>();

        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default)
        {
            OtherCalls++;
            return Task.FromResult(new NayaxMachine { MachineID = machineId });
        }

        public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(
            long machineId, CancellationToken ct = default) =>
            Counted<NayaxMachineAlert>();

        private Task<List<T>> Counted<T>()
        {
            OtherCalls++;
            return Task.FromResult(new List<T>());
        }
    }
}
