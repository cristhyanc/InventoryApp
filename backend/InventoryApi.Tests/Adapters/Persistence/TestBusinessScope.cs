using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Builds the <see cref="IBusinessScope"/> an <c>AppDbContext</c> is constructed with, so a
/// relational test can act as a specific business - or as a caller with no business at all.
/// </summary>
public static class TestBusinessScope
{
    /// <summary>A scope resolved to one business, as a successful request would have.</summary>
    public static IBusinessScope For(int businessId)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(businessId));
        return scope;
    }

    /// <summary>
    /// A caller whose business could not be resolved - no membership, or an ambiguous one. Reads
    /// must return nothing and tenant-owned writes must be refused.
    /// </summary>
    public static IBusinessScope Denied() => new BusinessScope();
}
