using Inventory.Domain.Tenancy;

namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// The narrowest possible read of one business's GST registration (issue #1318): the single setting
/// that decides whether a BAS summary is shown at all.
///
/// <para>It is deliberately not <c>IBusinessSettingsStore</c>, which is the Owner-only business
/// settings surface and also carries the business's name, contact address and ABN. The BAS summary
/// is a <c>Reports.View</c> surface that a Manager may read, so what it may learn about the business
/// is one boolean and nothing else; a port that cannot return a contact address cannot leak
/// one.</para>
///
/// <para>The business id is only ever one tenancy resolution produced from the authenticated actor's
/// membership, never a value a request supplied - <c>Businesses</c> is the owning table and no
/// query filter scopes it, so the key is what confines this read to the caller's own
/// business.</para>
/// </summary>
public interface IGstRegistrationStore
{
    /// <summary>
    /// Whether the business is registered for GST: <see langword="true"/> for yes,
    /// <see langword="false"/> for no, and <see langword="null"/> when nobody has recorded an answer
    /// - or when the business row is gone, which is the same fail-closed answer rather than a
    /// different one, because neither licenses showing a BAS estimate.
    /// </summary>
    Task<bool?> FindGstRegisteredAsync(BusinessId businessId, CancellationToken cancellationToken);
}
