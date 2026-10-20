using Inventory.Domain.Providers;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// The one EF read that turns a Nayax machine number into this business's own machine id, and back
/// (issue #780, parent #329 child G2a). It sits beside the <see cref="AppDbContext"/> whose entities
/// it is written against, the same place <see cref="EfSalesProviderQueries"/> and
/// <see cref="EfNayaxSalesQueries"/> sit.
///
/// <para><strong>Why it is here rather than only behind the Application port.</strong>
/// <c>Inventory.Infrastructure.Persistence.EfNayaxMachineLinkStore</c> is the
/// <c>INayaxMachineLinkStore</c> adapter the use cases hold, and it is this read; the reporting fact
/// providers need the same translation inside a statement of their own, and a second
/// implementation of "which machine is this number" is exactly the drift the one-lookup rule exists
/// to prevent. So both call this.</para>
///
/// <para><strong>Only while the business sells through Nayax.</strong> A machine's
/// <c>ExternalMachineId</c> holds whatever provider that business sells through and does not say who
/// issued it, so every read here goes through
/// <see cref="EfSalesProviderQueries.MachinesIdentifiedBy"/>, in the same statement. A business with
/// no provider, or another one, resolves nothing. The ordinary tenant query filter scopes the
/// machine read; no business predicate is written here.</para>
/// </summary>
internal static class EfMachineLinkQueries
{
    /// <summary>
    /// The local machine id for each given Nayax machine number this business has recorded, keyed
    /// by the number. A number recorded on no machine is absent, never mapped to itself.
    /// </summary>
    public static async Task<Dictionary<long, long>> LocalMachineIdsByNayaxIdAsync(
        AppDbContext db, IReadOnlyCollection<long> nayaxMachineIds, CancellationToken cancellationToken)
    {
        if (nayaxMachineIds.Count == 0)
            return [];

        // The stored external id is opaque text compared exactly, so the lookup is translated into
        // the text form first - NayaxExternalId is the one place that conversion is written -
        // rather than by casting the column, which would neither use the unique index nor stay
        // correct for a provider whose ids are not numbers.
        var externalIds = nayaxMachineIds.Distinct().Select(NayaxExternalId.From).ToList();

        var machines = await EfSalesProviderQueries
            .MachinesIdentifiedBy(db, ExternalProvider.Nayax)
            .Where(machine => externalIds.Contains(machine.ExternalMachineId!))
            .Select(machine => new { ExternalMachineId = machine.ExternalMachineId!, machine.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var localMachineIdByNayaxId = new Dictionary<long, long>(machines.Count);
        foreach (var machine in machines)
        {
            // A stored id this application could not have written is not a Nayax machine number, so
            // it resolves to nothing rather than to a nearby number; the filtered unique index on
            // (BusinessId, ExternalMachineId) is what makes the remaining keys distinct.
            if (NayaxExternalId.TryParse(machine.ExternalMachineId, out var nayaxMachineId))
                localMachineIdByNayaxId[nayaxMachineId] = machine.Id;
        }

        return localMachineIdByNayaxId;
    }
}
