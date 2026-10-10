using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;

namespace Inventory.Application.Businesses;

/// <summary>
/// Reads the signed-in operator's own business: its name and the IANA time zone its business days
/// are kept in (issue #499).
///
/// It exists so the frontend can render operator-facing instants and resolve calendar inputs in
/// the business's own zone instead of a hard-coded constant. The business is resolved from the
/// authenticated actor's membership through <see cref="ICurrentBusinessProvider"/> - the use case
/// takes no identifier at all, so no route, query, body or header value can choose whose zone is
/// returned.
/// </summary>
public sealed class GetCurrentBusiness
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IBusinessProfileStore _store;

    public GetCurrentBusiness(ICurrentBusinessProvider currentBusiness, IBusinessProfileStore store)
    {
        _currentBusiness = currentBusiness;
        _store = store;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership, so there is no current business to describe.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The resolved business no longer exists. That is a broken tenancy state rather than a
    /// caller error, and it fails closed: no name, no zone and no substitute.
    /// </exception>
    public async Task<BusinessProfile> Handle(CancellationToken cancellationToken)
    {
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken);

        return await _store.FindAsync(businessId, cancellationToken)
            ?? throw new DomainConflictException(
                "The signed-in account's business is no longer available.");
    }
}
