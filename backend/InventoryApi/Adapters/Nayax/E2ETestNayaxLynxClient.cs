using Inventory.Application.Nayax;

namespace InventoryApi.Adapters.Nayax;

/// <summary>
/// The Nayax Lynx port as the dedicated end-to-end testing host implements it: a fleet of no
/// machines and a remote catalogue of nothing (issue #46).
///
/// It exists because the E2E suite must be deterministic and must not touch a live external
/// service, and because some of the workflows it covers legitimately read the Nayax fleet - the
/// reorder page's machine replenishment need comes from
/// <c>Inventory.Application.Reorder.CalculateReorderNeeds</c>, which calls this port. Registering
/// this implementation in the E2E host instead of the real HTTP client means that host has no
/// Nayax HTTP client at all: there is nothing configured to call, so no test run can reach the
/// operator account, with or without a token.
///
/// It is a double for <em>our own</em> port, deliberately not a fake Nayax HTTP endpoint. Nothing
/// here models a Nayax request or response shape, so it asserts nothing about the Nayax API
/// contract and cannot drift from it (AGENTS.md § Nayax contract verification).
///
/// The empty reads are honest for the synthetic E2E businesses, which own no machines: every
/// product's machine replenishment need is genuinely zero. The two members that cannot answer
/// emptily - one machine by id, and creating machine products - throw rather than invent a
/// machine, so a test that depends on remote machine data fails loudly instead of asserting
/// against fabricated values.
///
/// It is only ever registered behind <c>InventoryApi.Auth.E2ETesting.E2ETestEnvironment</c>; see
/// <c>Program.cs</c>.
/// </summary>
public sealed class E2ETestNayaxLynxClient : INayaxLynxClient
{
    private const string NoFleetMessage =
        "The end-to-end testing host serves no Nayax machine data. A test that needs a machine "
            + "must arrange it locally rather than expect a remote fleet.";

    public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => Empty<NayaxDevice>();

    public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) => Empty<NayaxMachine>();

    public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default) =>
        Empty<NayaxMachineProduct>();

    public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
        long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
        throw new NotSupportedException(NoFleetMessage);

    public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => Empty<NayaxProduct>();

    public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
        Empty<NayaxProductGroup>();

    public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) =>
        Empty<NayaxLastSalesReport>();

    public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) =>
        throw new NotSupportedException(NoFleetMessage);

    public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default) =>
        Empty<NayaxMachineAlert>();

    private static Task<List<T>> Empty<T>() => Task.FromResult(new List<T>());
}
