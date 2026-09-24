namespace Inventory.Domain.CatalogReconciliation;

/// <summary>
/// The source-state of one local/remote catalog identity (a product or a machine) as observed by
/// comparing Nayax's current data against local historical records. Remote absence is data-quality
/// information for human review, never a signal to delete the local record - see
/// <see cref="CatalogReconciliationPolicy"/>.
/// </summary>
public enum SourceReconciliationState
{
    /// <summary>Local and remote agree on identity and name.</summary>
    Present,

    /// <summary>Nayax returns this identity but there is no local record of it yet.</summary>
    Added,

    /// <summary>A local record exists but Nayax no longer returns this identity.</summary>
    MissingRemotely,

    /// <summary>
    /// Local and remote agree on identity but disagree on their current name (a rename or remap).
    /// Only the latest reliable local name is compared; earlier local names are context.
    /// </summary>
    MappingChanged,

    /// <summary>
    /// The identity is genuinely ambiguous: Nayax returns more than one entry for the identifier in
    /// one snapshot, or local history records more than one name for it at its most recent
    /// observation. A completed historical rename is not a conflict.
    /// </summary>
    ConflictingIdentity
}
