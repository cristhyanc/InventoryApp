using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Businesses;

/// <summary>
/// Reads the settings of the signed-in Owner's own business (issue #548): its name, contact
/// address, ABN, GST registration and time zone.
///
/// Like <see cref="GetCurrentBusiness"/> it takes no identifier at all - the business comes from
/// the authenticated actor's membership through <see cref="ICurrentBusinessProvider"/> - so no
/// route, query, body or header value can choose whose settings are returned. Which <em>roles</em>
/// may ask is the endpoint's <c>Business.Manage</c> capability, decided before this runs.
/// </summary>
public sealed class GetBusinessSettings
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IBusinessSettingsStore _store;

    public GetBusinessSettings(ICurrentBusinessProvider currentBusiness, IBusinessSettingsStore store)
    {
        _currentBusiness = currentBusiness;
        _store = store;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership, so there are no settings to describe.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The resolved business no longer exists. That is a broken tenancy state rather than a caller
    /// error, and it fails closed: no settings, and no substitute.
    /// </exception>
    public async Task<BusinessSettings> Handle(CancellationToken cancellationToken)
    {
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken).ConfigureAwait(false);

        return await _store.FindAsync(businessId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainConflictException(BusinessSettingsMessages.BusinessUnavailable);
    }
}

/// <summary>
/// The caller-facing sentences the business-settings use cases refuse with (issue #548), in one
/// place so the read and the write answer a missing business identically.
/// </summary>
public static class BusinessSettingsMessages
{
    /// <summary>
    /// A resolved membership pointing at a business row that is gone. It names no business and no
    /// identifier, and the API boundary publishes it as <c>409</c>.
    /// </summary>
    public const string BusinessUnavailable = "The signed-in account's business is no longer available.";
}
