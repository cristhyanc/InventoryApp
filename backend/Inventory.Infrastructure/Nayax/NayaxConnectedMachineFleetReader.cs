using Inventory.Application.ConnectedMachines;
using Inventory.Application.Nayax;
using Inventory.Domain.ConnectedMachines;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// Reads the current business's Nayax fleet so the daily job can count it (issue #978).
///
/// <para><strong>It is the existing fleet read and nothing new.</strong> It calls
/// <see cref="INayaxLynxClient.GetMachinesFreshAsync"/> - the whole-fleet Lynx call the machine list
/// already uses, which passes no <c>ResultsLimit</c> and so returns the whole fleet - and the client
/// resolves the business's own operator id and token per call through
/// <c>INayaxRequestCredentialProvider</c>, with the status gate and the support-session refusal
/// applied exactly where every other ordinary Nayax call applies them. There is no global credential
/// to fall back to and this adapter holds none.</para>
///
/// <para><strong>Why it is given its client rather than resolving the port.</strong> The composition
/// root hands it the <em>inner</em> client (<see cref="NayaxLynxClient"/>), not the registered
/// <see cref="INayaxLynxClient"/>, because the registered port is wrapped by
/// <see cref="ConnectedMachineCountingNayaxLynxClient"/> - the refresh path that records a snapshot
/// from a fleet read some other feature performed. A reader that resolved the port would be asking
/// that decorator to call the thing that calls it, and the job's read would be recorded twice over.
/// Reading through the inner client changes no refusal: the gate and the support-session rule are the
/// credential provider's, not the decorator's.</para>
///
/// <para><strong>The read says which account answered it.</strong> It calls
/// <see cref="INayaxFleetProvenanceClient.ReadFleetFreshAsync"/>, so the fleet arrives with the
/// account series of the credential snapshot that authenticated the request, and the
/// <see cref="ConnectedMachineFleetRead"/> it answers carries that series into the recorder. That is
/// what closes the job path's own race: the job decides whether to spend a call <em>before</em> the
/// call, and an Owner can save a different Nayax account in the window between that decision and the
/// answer - so the series the snapshot is stamped with has to come from the request, never from the
/// earlier eligibility read (issue #978).</para>
///
/// <para>A failure propagates. There is no retry here beyond the client's own bounded resilience
/// handler, and nothing here turns a failed read into a smaller fleet: the job stores nothing for a
/// business whose read failed, and its last complete snapshot remains the count.</para>
/// </summary>
public sealed class NayaxConnectedMachineFleetReader : IConnectedMachineFleetReader
{
    private readonly INayaxFleetProvenanceClient _client;

    public NayaxConnectedMachineFleetReader(INayaxFleetProvenanceClient client)
    {
        _client = client;
    }

    /// <inheritdoc />
    public async Task<ConnectedMachineFleetRead> ReadAsync(CancellationToken cancellationToken)
    {
        var fleet = await _client.ReadFleetFreshAsync(cancellationToken).ConfigureAwait(false);

        // A successful whole-fleet read is a complete read; an unsuccessful one threw and is never
        // reported as a fleet at all. The series is the request's own, not a connection read of this
        // adapter's.
        return ConnectedMachineFleetRead.Complete(
            fleet.AccountSeries,
            fleet.Machines.Select(machine => (long?)machine.MachineID));
    }
}
