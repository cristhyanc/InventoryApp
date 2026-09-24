namespace Inventory.Domain.CatalogReconciliation;

/// <summary>One identity as Nayax currently reports it.</summary>
public readonly record struct RemoteCatalogEntry(long ExternalId, string Name);

/// <summary>
/// One identity as local history records it. <paramref name="PriorNames"/> lists any other distinct
/// names local history has recorded for this identity besides <paramref name="Name"/> - for example a
/// machine whose sales rows carry two different <c>MachineName</c> values over time. An empty list
/// means local history is internally consistent for this identity.
/// </summary>
public sealed record LocalCatalogEntry(long ExternalId, string Name, IReadOnlyList<string> PriorNames)
{
    public LocalCatalogEntry(long externalId, string name)
        : this(externalId, name, [])
    {
    }
}

/// <summary>
/// One row of a reconciliation result: an identity, its resolved <see cref="SourceReconciliationState"/>,
/// the name(s) each side reports, and a human-readable explanation suitable for an admin/data-quality
/// view.
/// </summary>
public sealed record ReconciliationEntry(
    long ExternalId,
    SourceReconciliationState State,
    string? LocalName,
    string? RemoteName,
    string? Note);
