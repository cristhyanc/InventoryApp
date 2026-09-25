using Inventory.Application.Nayax;

namespace InventoryApi.Services;

/// <summary>CustomerID identifies a Nayax site; Nayax provides no site name, so the first machine-name token is displayed.</summary>
public static class SiteNameResolver
{
    public static string FromMachines(IEnumerable<NayaxMachine> machines, long siteId)
    {
        var machineName = machines.Select(machine => machine.MachineName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        return machineName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? $"Site {siteId}";
    }
}
