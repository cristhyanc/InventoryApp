using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Domain.CatalogReconciliation;

/// <summary>
/// Deterministic comparison between local catalog history and Nayax's current data for one entity
/// type (products, or machines derived from sale history). Never deletes or mutates anything: it
/// only classifies each identity's <see cref="SourceReconciliationState"/> so a human can review it.
///
/// One row is produced per identity seen on either side, decided by priority so the result is
/// deterministic when more than one condition applies to the same identity:
/// 1. Nayax itself reports more than one entry for the identifier (an upstream identity conflict).
/// 2. Local history cannot resolve one current name for the identifier, because more than one name is
///    recorded at its most recent observation (a local identity conflict).
/// 3. The identifier has a local record but Nayax no longer returns it.
/// 4. Nayax returns the identifier but there is no local record of it yet.
/// 5. Both sides have the identifier but disagree on the current name (a rename or remap).
/// 6. Otherwise, the identifier is present and consistent on both sides.
///
/// Only <see cref="LocalCatalogEntry.Name"/> - the latest reliable local name - takes part in the
/// current-name comparison. Earlier names in <see cref="LocalCatalogEntry.HistoricalNames"/> are
/// reported as context on every row and never decide a state by themselves, so an ordinary historical
/// rename whose latest local name now agrees with Nayax reconciles as
/// <see cref="SourceReconciliationState.Present"/> rather than being treated as a permanent conflict.
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
        var historicalLocalNames = local?.HistoricalNames ?? [];

        ReconciliationEntry Entry(SourceReconciliationState state, string? localName, string? remoteName, string? note) =>
            new(id, state, localName, remoteName, historicalLocalNames, WithHistoryContext(id, note, historicalLocalNames));

        if (remoteEntries is { Count: > 1 })
        {
            var distinctRemoteNames = remoteEntries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return Entry(SourceReconciliationState.ConflictingIdentity, local?.Name, remote?.Name,
                $"Nayax returned {remoteEntries.Count} entries for identifier {id}: {FormatNames(distinctRemoteNames)}.");
        }

        if (local is { CurrentNameIsAmbiguous: true })
            return Entry(SourceReconciliationState.ConflictingIdentity, local.Name, remote?.Name,
                $"Local history records more than one name for identifier {id} at its most recent observation, so its current local name cannot be determined.");

        if (local is null)
            return Entry(SourceReconciliationState.Added, null, remote?.Name,
                $"Nayax returns identifier {id} (\"{remote?.Name}\") with no local record.");

        if (remote is null)
            return Entry(SourceReconciliationState.MissingRemotely, local.Name, null,
                $"Identifier {id} (\"{local.Name}\") has a local record but Nayax no longer returns it.");

        if (!NamesMatch(local.Name, remote.Value.Name))
            return Entry(SourceReconciliationState.MappingChanged, local.Name, remote.Value.Name,
                $"Identifier {id} is currently named \"{local.Name}\" locally but \"{remote.Value.Name}\" in Nayax.");

        return Entry(SourceReconciliationState.Present, local.Name, remote.Value.Name, null);
    }

    /// <summary>
    /// Appends the earlier local names as context to whatever the state's own note says. A rename that
    /// the state no longer treats as a problem stays visible to a human reviewing the report.
    /// </summary>
    private static string? WithHistoryContext(long id, string? note, IReadOnlyList<string> historicalLocalNames)
    {
        if (historicalLocalNames.Count == 0)
            return note;

        var context = $"Local history previously recorded identifier {id} as {FormatNames(historicalLocalNames)}.";
        return note is null ? context : $"{note} {context}";
    }

    private static bool NamesMatch(string localName, string remoteName) =>
        string.Equals(ProductMatcher.NormalizeName(localName), ProductMatcher.NormalizeName(remoteName), StringComparison.OrdinalIgnoreCase);

    private static string FormatNames(IEnumerable<string> names) =>
        string.Join(", ", names.Select(name => $"\"{name}\""));
}
