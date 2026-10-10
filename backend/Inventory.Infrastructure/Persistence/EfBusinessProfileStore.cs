using Inventory.Application.Businesses;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IBusinessProfileStore"/> (issue #499). Like every other
/// adapter in this folder it lives in Inventory.Infrastructure beside the <see cref="AppDbContext"/>
/// and the persistence models it depends on.
///
/// <c>Businesses</c> is one of the deliberately global tables - it is the owner, not owned - so the
/// lookup is by primary key and the key is only ever one tenancy resolution produced from the
/// authenticated actor's membership. That is what keeps this read, which no query filter can scope,
/// confined to the caller's own business.
/// </summary>
public sealed class EfBusinessProfileStore : IBusinessProfileStore
{
    private readonly AppDbContext _db;

    public EfBusinessProfileStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BusinessProfile?> FindAsync(BusinessId businessId, CancellationToken cancellationToken) =>
        await _db.Businesses
            .AsNoTracking()
            .Where(business => business.Id == businessId.Value)
            .Select(business => new BusinessProfile(business.Name, business.TimeZoneId))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}
