using Inventory.Domain.Tenancy;

namespace Inventory.Application.Businesses;

/// <summary>
/// Narrow persistence port for a business's own settings (issue #548): the read behind
/// <c>GET /api/business/settings</c> and the write behind its <c>PUT</c>.
///
/// It is deliberately separate from <see cref="IBusinessProfileStore"/>, which answers the one
/// question every request asks (the current business's name and zone, for the shell and the
/// calendar) and is read on every single request. These two are the Owner-only settings surface,
/// one of them a write, and keeping them apart is what stops a hot read path and a rare write from
/// sharing a contract.
///
/// The business id both members take is only ever one that tenancy resolution produced from the
/// authenticated actor's membership, never a value a request supplied, and both return
/// <see langword="null"/> rather than throwing when no such business exists so the caller decides
/// what a missing business means.
/// </summary>
public interface IBusinessSettingsStore
{
    /// <summary>The business's current settings, or <see langword="null"/> when the row is gone.</summary>
    Task<BusinessSettings?> FindAsync(BusinessId businessId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="submitted"/> as the business's settings and reports what that
    /// changed.
    ///
    /// <para>The adapter reads the row it is about to overwrite and compares it inside the same
    /// operation as the write, because the change set is the audit trail's evidence: deciding it
    /// from a row read earlier, in another query, would describe a state that was already gone.
    /// Every setting is written, so a save is all of them or none of them - there is no partial
    /// result and no field a refusal could leave half-applied.</para>
    /// </summary>
    /// <returns>
    /// The stored settings with the fields the write changed, or <see langword="null"/> when the
    /// business no longer exists - in which case nothing was written.
    /// </returns>
    Task<BusinessSettingsSaveResult?> SaveAsync(
        BusinessId businessId,
        BusinessSettings submitted,
        CancellationToken cancellationToken);
}

/// <summary>
/// What a settings save stored, and which settings it changed (issue #548).
/// </summary>
/// <param name="Saved">The settings as they now stand, read back from the write.</param>
/// <param name="ChangedFields">
/// The settings whose value the write actually changed, in <see cref="BusinessSettingsField"/>
/// declaration order. Empty when the submitted settings matched the stored ones, which is not a
/// failure: a resubmission is simply not a change, and nothing is audited for it.
/// </param>
public sealed record BusinessSettingsSaveResult(
    BusinessSettings Saved,
    IReadOnlyList<BusinessSettingsField> ChangedFields);
