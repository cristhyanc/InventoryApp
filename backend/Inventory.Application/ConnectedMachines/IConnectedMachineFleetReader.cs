using Inventory.Domain.ConnectedMachines;

namespace Inventory.Application.ConnectedMachines;

/// <summary>
/// One complete read of the current business's Nayax fleet, for the sole purpose of counting it
/// (issue #978).
///
/// <para>It is the existing fleet read the machine list already uses - the Lynx whole-fleet call
/// behind <c>INayaxLynxClient.GetMachinesFreshAsync</c> - authenticated with the business's own
/// stored credentials through the existing credential provider, with the status gate and the
/// support-session refusal applied exactly where every other ordinary Nayax call applies them. It
/// takes no business, no operator id and no token: there is no global credential to fall back to
/// (AGENTS.md § Nayax integration).</para>
///
/// <para>It is a separate port from the client itself for one concrete reason: the refresh path
/// records a snapshot from the fleet read a feature has <em>already</em> performed, which is a
/// decorator over that client, so a reader that resolved the port would be asking the decorator to
/// call the thing that calls it. This port is implemented over the uncached, undecorated client, and
/// the daily job is its only caller.</para>
/// </summary>
public interface IConnectedMachineFleetReader
{
    /// <summary>
    /// Reads the current business's fleet now.
    ///
    /// <para>A read that cannot be completed is not reported as a smaller fleet: the adapter either
    /// answers a complete read or lets the failure propagate, and the caller stores nothing from an
    /// incomplete one.</para>
    /// </summary>
    Task<ConnectedMachineFleetRead> ReadAsync(CancellationToken cancellationToken);
}
