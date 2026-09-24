using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Domain.CatalogReconciliation;

/// <summary>
/// Deterministic comparison between local catalog history and Nayax's current data for one entity
/// type (products, or machines derived from sale history). Never deletes or mutates anything: it
/// only classifies each identity's <see cref="SourceReconciliationState"/> so a human can review it.
///
/// One row is produced per identity seen on either side, decided by priority so the result is
/// deterministic when more than one condition applies to the same identity:
/// 1. Nayax itself reports more than one entry for the identifier (an upstream data conflict).
/// 2. Local history recorded more than one distinct name for the identifier (a local data conflict -
///    for example a machine whose sales rows disagree on <c>MachineName</c>).
/// 3. The identifier has a local record but Nayax no longer returns it.
/// 4. Nayax returns the identifier but there is no local record of it yet.
/// 5. Both sides have the identifier but disagree on name (a rename or remap).
/// 6. Otherwise, the identifier is present and consistent on both sides.
///
/// Name comparison reuses <see cref="ProductMatcher.NormalizeName"/> - the same rule sale-to-product
/// matching already uses to strip a parenthetical price/code suffix - so a Nayax formatting-only
/// difference is not reported as a mapping change.
/// </summary>
public static class CatalogReconciliationPolicy
{
    public static IReadOnlyList<ReconciliationEntry> Reconcile(
        IReadOnlyCollection<LocalCatalogEntry> local,
        IReadOnlyCollection<RemoteCatalogEntry> remote)
    {
        var remoteGroups = remote
            .GroupBy(entry => entry.ExternalId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var localById = local.ToDictionary(entry => entry.ExternalId);

        var ids = new SortedSet<long>(localById.Keys);
        ids.UnionWith(remoteGroups.Keys);

        return ids
            .Select(id =>
            {
                localById.TryGetValue(id, out var localEntry);
                remoteGroups.TryGetValue(id, out var remoteEntries);
                return ReconcileOne(id, localEntry, remoteEntries);
            })
            .ToList();
    }

    private static ReconciliationEntry ReconcileOne(long id, LocalCatalogEntry? local, List<RemoteCatalogEntry>? remoteEntries)
    {
        var remote = remoteEntries is { Count: > 0 } ? remoteEntries[0] : (RemoteCatalogEntry?)null;

        if (remoteEntries is { Count: > 1 })
        {
            var distinctRemoteNames = remoteEntries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new ReconciliationEntry(id, SourceReconciliationState.ConflictingIdentity, local?.Name, remote?.Name,
                $"Nayax returned {remoteEntries.Count} entries for identifier {id}: {FormatNames(distinctRemoteNames)}.");
        }

        if (local is not null)
        {
            var distinctLocalNames = new[] { local.Name }.Concat(local.PriorNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctLocalNames.Count > 1)
                return new ReconciliationEntry(id, SourceReconciliationState.ConflictingIdentity, local.Name, remote?.Name,
                    $"Local history recorded identifier {id} under multiple names: {FormatNames(distinctLocalNames)}.");
        }

        if (local is null)
            return new ReconciliationEntry(id, SourceReconciliationState.Added, null, remote?.Name,
                $"Nayax returns identifier {id} (\"{remote?.Name}\") with no local record.");

        if (remote is null)
            return new ReconciliationEntry(id, SourceReconciliationState.MissingRemotely, local.Name, null,
                $"Identifier {id} (\"{local.Name}\") has a local record but Nayax no longer returns it.");

        if (!NamesMatch(local.Name, remote.Value.Name))
            return new ReconciliationEntry(id, SourceReconciliationState.MappingChanged, local.Name, remote.Value.Name,
                $"Identifier {id} is named \"{local.Name}\" locally but \"{remote.Value.Name}\" in Nayax.");

        return new ReconciliationEntry(id, SourceReconciliationState.Present, local.Name, remote.Value.Name, null);
    }

    private static bool NamesMatch(string localName, string remoteName) =>
        string.Equals(ProductMatcher.NormalizeName(localName), ProductMatcher.NormalizeName(remoteName), StringComparison.OrdinalIgnoreCase);

    private static string FormatNames(IEnumerable<string> names) =>
        string.Join(", ", names.Select(name => $"\"{name}\""));
}
