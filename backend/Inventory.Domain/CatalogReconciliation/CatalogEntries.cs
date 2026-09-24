namespace Inventory.Domain.CatalogReconciliation;

/// <summary>One identity as Nayax currently reports it.</summary>
public readonly record struct RemoteCatalogEntry(long ExternalId, string Name);

/// <summary>
/// One identity as local history records it.
///
/// <paramref name="Name"/> is the latest reliable local name - the name local history most recently
/// recorded for this identity - and is the only name compared against Nayax's current name.
/// <paramref name="HistoricalNames"/> lists the other distinct names local history recorded for the
/// same identity earlier, most recently used first. An ordinary rename therefore leaves a non-empty
/// history and is reported as context, not as a conflict.
///
/// <paramref name="CurrentNameIsAmbiguous"/> is the only local evidence of a genuinely ambiguous
/// identity: local history records more than one name at its most recent observation, so no single
/// current local name can be determined (for example two <c>NayaxSales</c> rows for the same
/// <c>MachineID</c> at the same latest authorization time carrying different <c>MachineName</c>
/// values). <paramref name="Name"/> then holds one of the tied names, chosen deterministically.
/// </summary>
public sealed record LocalCatalogEntry(
    long ExternalId,
    string Name,
    IReadOnlyList<string> HistoricalNames,
    bool CurrentNameIsAmbiguous = false)
{
    public LocalCatalogEntry(long externalId, string name)
        : this(externalId, name, [])
    {
    }
}

/// <summary>
/// One row of a reconciliation result: an identity, its resolved <see cref="SourceReconciliationState"/>,
/// the current name each side reports, the earlier local names recorded for it, and a human-readable
/// explanation suitable for an admin/data-quality view.
///
/// <paramref name="LocalName"/> is the latest reliable local name, never an earlier one.
/// <paramref name="HistoricalLocalNames"/> is context only - it never changes the state on its own.
/// </summary>
public sealed record ReconciliationEntry(
    long ExternalId,
    SourceReconciliationState State,
    string? LocalName,
    string? RemoteName,
    IReadOnlyList<string> HistoricalLocalNames,
    string? Note);
