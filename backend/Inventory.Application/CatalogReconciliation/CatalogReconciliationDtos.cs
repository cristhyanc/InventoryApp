namespace Inventory.Application.CatalogReconciliation;

/// <summary>
/// One reconciled identity for the API/admin view. <see cref="State"/> is the
/// <c>Inventory.Domain.CatalogReconciliation.SourceReconciliationState</c> name
/// (<c>"Present"</c>, <c>"Added"</c>, <c>"MissingRemotely"</c>, <c>"MappingChanged"</c>, or
/// <c>"ConflictingIdentity"</c>).
/// </summary>
public sealed record CatalogReconciliationEntryDto(
    long ExternalId,
    string State,
    string? LocalName,
    string? RemoteName,
    string? Note);

/// <summary>
/// The complete Nayax catalog reconciliation result: every product and machine identity known
/// locally or remotely, each with its resolved source state. Suitable for an admin/data-quality
/// view - it is read-only and never deletes or mutates a local record.
/// </summary>
public sealed record CatalogReconciliationReportDto(
    IReadOnlyList<CatalogReconciliationEntryDto> Products,
    IReadOnlyList<CatalogReconciliationEntryDto> Machines)
{
    public int ProductIssueCount => Products.Count(entry => entry.State != "Present");

    public int MachineIssueCount => Machines.Count(entry => entry.State != "Present");
}
