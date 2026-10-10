using Inventory.Domain.Tenancy;

namespace Inventory.Application.Businesses;

/// <summary>
/// Narrow persistence port for a business's own record (issue #499).
///
/// The business id it takes is only ever one that tenancy resolution produced from the
/// authenticated actor's membership (<see cref="BusinessId"/>), never a value a request supplied.
/// It returns <see langword="null"/> rather than throwing when no such business exists, so the
/// caller decides what a missing business means instead of the adapter.
/// </summary>
public interface IBusinessProfileStore
{
    Task<BusinessProfile?> FindAsync(BusinessId businessId, CancellationToken cancellationToken);
}
