using Inventory.Application.ConnectedMachines;
using Inventory.Application.Nayax;
using Inventory.Domain.ConnectedMachines;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// Records the day's connected-machine snapshot from a complete fleet read the application has
/// already performed (issue #978) - the refresh rule of that issue: "whenever the app already
/// performs a complete fleet read for a Pro business, record that day's snapshot from it".
///
/// <para><strong>It is one place on purpose.</strong> Several features read the whole fleet - the
/// provider machine review, the link apply, the latest-sales sync, the costing transition preview and
/// the sale-timestamp repair - and a snapshot taken in each of them would be five places to forget,
/// and five places for the claim to be written differently. Wrapping the port means a feature that
/// reads the whole fleet tomorrow is counted without being changed, and it is the reason the
/// connected-machine count costs <strong>no extra Nayax call on a page load</strong>: it reuses the
/// read that was happening anyway.</para>
///
/// <para><strong>It wraps the cached client rather than replacing it, and only the fresh read
/// counts.</strong> <see cref="GetMachinesAsync"/> may answer from the machine-list cache of issue
/// #840 - an answer up to ten minutes old is not a read of the account, and recording it would date a
/// count to the wrong moment - so only <see cref="GetMachinesFreshAsync"/>, the read a caller takes
/// precisely because it must see the fleet now, becomes evidence. Everything else is pure
/// delegation.</para>
///
/// <para><strong>Recording never breaks the caller.</strong> The read has already succeeded and the
/// caller is a sales sync, a machine review or a dashboard refresh; a snapshot that could not be
/// written is logged and abandoned, because the next complete read of the day records one and because
/// billing evidence must not be able to fail a feature. A cancelled request is not an error at all
/// and propagates as cancellation. The log line carries the business-free outcome and nothing about
/// the fleet, the credentials or the caller.</para>
/// </summary>
public sealed class ConnectedMachineCountingNayaxLynxClient : INayaxLynxClient
{
    private readonly INayaxLynxClient _inner;
    private readonly IConnectedMachineSnapshotRecorder _recorder;
    private readonly ILogger<ConnectedMachineCountingNayaxLynxClient> _logger;

    public ConnectedMachineCountingNayaxLynxClient(
        INayaxLynxClient inner,
        IConnectedMachineSnapshotRecorder recorder,
        ILogger<ConnectedMachineCountingNayaxLynxClient> logger)
    {
        _inner = inner;
        _recorder = recorder;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default)
    {
        var machines = await _inner.GetMachinesFreshAsync(ct).ConfigureAwait(false);

        await RecordAsync(machines, ct).ConfigureAwait(false);

        return machines;
    }

    /// <inheritdoc />
    public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
        _inner.GetMachinesAsync(ct);

    /// <inheritdoc />
    public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        _inner.GetDevicesAsync(ct);

    /// <inheritdoc />
    public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(
        long machineId, CancellationToken ct = default) =>
        _inner.GetMachineProductsAsync(machineId, ct);

    /// <inheritdoc />
    public Task<List<NayaxMachineProduct>> GetMachineProductsFreshAsync(
        long machineId, CancellationToken ct = default) =>
        _inner.GetMachineProductsFreshAsync(machineId, ct);

    /// <inheritdoc />
    public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
        long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
        _inner.CreateMachineProductsAsync(machineId, products, ct);

    /// <inheritdoc />
    public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) =>
        _inner.GetProductsAsync(ct);

    /// <inheritdoc />
    public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
        _inner.GetProductGroupssAsync(ct);

    /// <inheritdoc />
    public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(
        long machineId, CancellationToken ct = default) =>
        _inner.GetMachineLastSalesAsync(machineId, ct);

    /// <inheritdoc />
    public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) =>
        _inner.GetMachineAsync(machineId, ct);

    /// <inheritdoc />
    public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(
        long machineId, CancellationToken ct = default) =>
        _inner.GetMachineLastAlertsAsync(machineId, ct);

    private async Task RecordAsync(List<NayaxMachine> machines, CancellationToken ct)
    {
        try
        {
            var outcome = await _recorder
                .RecordFromFleetReadAsync(
                    ConnectedMachineFleetRead.Complete(machines.Select(machine => (long?)machine.MachineID)),
                    ct)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "A complete Nayax fleet read was offered to the connected-machine count with "
                    + "outcome {Outcome}.",
                outcome);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            // Deliberately value-free: the business, the machine ids and the count stay out of it,
            // and the next complete read of the day is the retry.
            _logger.LogWarning(
                exception,
                "A connected-machine snapshot could not be recorded from a complete Nayax fleet "
                    + "read and was abandoned. The read itself succeeded and the caller is "
                    + "unaffected; the business's last complete snapshot remains its count and is "
                    + "reported as stale with its age until a later read records one.");
        }
    }
}
