namespace Inventory.Application.Nayax;

/// <summary>
/// The application-facing port for the Nayax Lynx REST API (issue #49). Configuration-agnostic:
/// callers depend on this contract only, never on how a base URL, operator ID, or bearer token
/// reached the implementation. <c>Inventory.Infrastructure.Nayax.NayaxLynxClient</c> is the
/// concrete HTTP/configuration adapter.
/// Docs: https://devzone.nayax.com/docs/manage-data-operations/lynx-api
/// </summary>
public interface INayaxLynxClient
{
    Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default);

    /// <summary>
    /// The business's machine list (<c>GET /v1/machines</c>).
    ///
    /// <para>The answer may be up to ten minutes old. The composition root decorates this port with
    /// <c>Inventory.Infrastructure.Nayax.MachineListCachingNayaxLynxClient</c> (issue #840), which
    /// keeps one list per business and credential revision for that long, because one Dashboard
    /// visit used to read it five times and the fleet changes rarely. The decorator still resolves
    /// the current business's connection, status gate and support-session refusal before every
    /// answer, so a caller sees the same refusals at the same moments as before.</para>
    ///
    /// <para>A caller whose correctness depends on seeing a machine added minutes ago - one that
    /// records business-wide coverage, links provider machines, or builds a plan over the whole
    /// fleet - calls <see cref="GetMachinesFreshAsync"/> instead.</para>
    /// </summary>
    Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default);

    /// <summary>
    /// The business's machine list read from Nayax now, never from the cache above, replacing
    /// whatever that cache held for this business and credential revision (issue #840).
    ///
    /// <para>It exists so a caller can state that requirement in its own code rather than inherit
    /// it from how the port happens to be composed: an implementation that caches nothing answers
    /// both methods identically, which is exactly what <c>NayaxLynxClient</c> itself does.</para>
    /// </summary>
    Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default);
    Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default);
    Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default);
    Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default);
    Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default);
    Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default);
    Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default);

    /// <summary>
    /// The machine's last-reported alerts, including Event 501 ("Stock Adjust for Machine")
    /// stock-adjustment alerts consumed by the Sync Restock workflow (issue #183).
    /// Docs: https://devzone.nayax.com/reference/lynx/machines/get-machine-last-alerts
    /// </summary>
    Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default);
}
