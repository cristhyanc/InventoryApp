using Inventory.Application.Nayax;
using Inventory.Infrastructure.Nayax;

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
/// <para>Every read first asks <see cref="INayaxRequestCredentialProvider"/> for the business's
/// credential, exactly as the real client's credential handler does before any request
/// (issue #1276). That provider is the production one, unchanged: a business with no stored
/// connection - which is every seeded E2E business - is refused with
/// <see cref="NayaxNotConnectedException"/>, so the pages that read the fleet show the same
/// not-connected state production shows such a business (the Dashboard's provider panels locked,
/// reorder in storage-only mode) instead of a connected business with an empty fleet. The
/// credential it returns, if a connection were ever stored, is never sent anywhere: this host
/// still has no HTTP client.</para>
///
/// It is only ever registered behind <c>InventoryApi.Auth.E2ETesting.E2ETestEnvironment</c>; see
/// <c>Program.cs</c>.
/// </summary>
public sealed class E2ETestNayaxLynxClient : INayaxFleetProvenanceClient
{
    private readonly INayaxRequestCredentialProvider _credentials;

    public E2ETestNayaxLynxClient(INayaxRequestCredentialProvider credentials) => _credentials = credentials;

    private const string NoFleetMessage =
        "The end-to-end testing host serves no Nayax machine data. A test that needs a machine "
            + "must arrange it locally rather than expect a remote fleet.";

    public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => Empty<NayaxDevice>(ct);

    public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) => Empty<NayaxMachine>(ct);

    // This host caches nothing either, so the fresh read is the same empty fleet (issue #840).
    public Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) => Empty<NayaxMachine>(ct);

    /// <summary>
    /// The same empty fleet, with <strong>no</strong> account series (issue #978): this host reaches
    /// no Nayax account, so no read was ever authenticated as one and it cannot say which account
    /// that would have been. Answering none is the honest answer, and the recorder refuses to attribute
    /// a count it cannot name an account for - so an E2E run records no snapshot rather than one
    /// stamped with a series nobody read.
    /// </summary>
    public async Task<NayaxFleetReadResult> ReadFleetFreshAsync(CancellationToken ct = default) =>
        new(await Empty<NayaxMachine>(ct).ConfigureAwait(false), AccountSeries: null);

    public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default) =>
        Empty<NayaxMachineProduct>(ct);

    // Nor are the machine's selections cached here, so the fresh read is the same empty list
    // (issue #842).
    public Task<List<NayaxMachineProduct>> GetMachineProductsFreshAsync(
        long machineId, CancellationToken ct = default) =>
        Empty<NayaxMachineProduct>(ct);

    public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
        long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
        Refuse<List<NayaxMachineProduct>>(ct);

    public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => Empty<NayaxProduct>(ct);

    public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
        Empty<NayaxProductGroup>(ct);

    public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) =>
        Empty<NayaxLastSalesReport>(ct);

    public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) =>
        Refuse<NayaxMachine>(ct);

    public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default) =>
        Empty<NayaxMachineAlert>(ct);

    /// <summary>
    /// The same refusal the real client gives a business with no usable connection, then the
    /// honest empty answer for a business that has one: the synthetic businesses own no machines.
    /// </summary>
    private async Task<List<T>> Empty<T>(CancellationToken ct)
    {
        await _credentials.GetForOperationAsync(ct).ConfigureAwait(false);
        return [];
    }

    /// <summary>
    /// Refuses a business with no usable connection exactly as <see cref="Empty{T}"/> does, and
    /// otherwise throws rather than invent a machine.
    /// </summary>
    private async Task<T> Refuse<T>(CancellationToken ct)
    {
        await _credentials.GetForOperationAsync(ct).ConfigureAwait(false);
        throw new NotSupportedException(NoFleetMessage);
    }
}
