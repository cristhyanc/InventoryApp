using Inventory.Application.Nayax;
using Inventory.Application.Sites;

namespace Inventory.Infrastructure.Sites;

/// <summary>
/// Implementation of <see cref="ISiteNameResolver"/>. Nayax's <c>CustomerID</c> identifies a site
/// but the API carries no site name of its own, so the display name is the first token of the
/// site's first named machine, falling back to <c>Site {siteId}</c>.
///
/// Issue #306 merged the former <c>InventoryApi.Services.SiteNameResolver</c> helper and its
/// <c>InventoryApi.Adapters.Persistence.SiteNameResolverAdapter</c> wrapper into this one
/// Infrastructure resident - the last file to leave <c>InventoryApi/Services</c> - and
/// <c>AddInfrastructureServices()</c> now registers it. It belongs here rather than in
/// <c>Inventory.Application</c> because it is an adapter for a remote system's missing field, and
/// it needed no <c>AppDbContext</c> to move, unlike the EF adapters #153 relocated later (issues
/// #308 and #309).
///
/// <see cref="FromMachines"/> stays available as a static entry point for
/// <c>Inventory.Infrastructure.Reporting.Persistence.EfTransactionSalesReportFactsProvider</c>, which resolves a
/// site name per streamed transaction row inside a static iterator rather than through the
/// injected port, exactly as it did before the move. Both paths run the same rule, so the site
/// dashboard, the commission report and the transaction report cannot drift apart.
/// </summary>
public sealed class SiteNameResolver : ISiteNameResolver
{
    public string Resolve(IReadOnlyList<NayaxMachine> machines, long siteId) => FromMachines(machines, siteId);

    public static string FromMachines(IEnumerable<NayaxMachine> machines, long siteId)
    {
        var machineName = machines.Select(machine => machine.MachineName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        return machineName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? $"Site {siteId}";
    }
}
