using Inventory.Application.Nayax;

namespace InventoryApi.Adapters.Nayax;

/// <summary>
/// The Nayax connection-check port as the dedicated end-to-end testing host implements it: present,
/// so the host resolves, and unusable for an actual check (issue #1276).
///
/// <see cref="InventoryApi.Controllers.NayaxConnectionController"/> binds all four of its actions through one
/// constructor, so resolving it - including for the one read-only <c>GET status</c> action -
/// requires every dependency `TestNayaxConnection` and `RetestNayaxConnection` take, among them
/// <see cref="INayaxConnectionCheckRunner"/>. Outside this host that port is registered beside the
/// real Nayax HTTP client (<c>AddNayaxLynxClient</c>), which this host never registers at all (see
/// <see cref="E2ETestNayaxLynxClient"/>), so leaving it unregistered here failed the controller's own
/// construction with an <see cref="InvalidOperationException"/> - on every action, including the
/// status read that touches no Nayax port at all.
///
/// It is a double for <em>our own</em> port, deliberately not a fake Nayax HTTP endpoint, so it
/// asserts nothing about the Nayax API contract and cannot drift from it (AGENTS.md § Nayax
/// contract verification). No seeded E2E business and no suite running against this host tests or
/// re-tests a connection - there is no live operator account to test one against - so the one
/// member this port has throws rather than invent a result, the same way the unsupported members of
/// <see cref="E2ETestNayaxLynxClient"/> do.
/// </summary>
public sealed class E2ETestNayaxConnectionCheckRunner : INayaxConnectionCheckRunner
{
    public Task<NayaxConnectionCheckReport> RunAsync(
        NayaxConnectionCredential candidate, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The end-to-end testing host tests no Nayax connection. A test that needs a connection "
                + "test result must arrange it locally rather than expect a live operator account.");
}
