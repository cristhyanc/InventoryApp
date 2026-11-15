using Inventory.Application.Reporting.Gst;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IGstRegistrationStore"/> (issue #1318).
///
/// <para><c>Businesses</c> is one of the deliberately global tables - it is the owner, not owned -
/// so no query filter scopes it and the read is by primary key, exactly as
/// <c>EfBusinessSettingsStore</c> reads the same row. The key is only ever one tenancy resolution
/// produced from the authenticated actor's membership, so there is no predicate here a request could
/// influence and no other business's row is loaded.</para>
///
/// <para>It projects the one column it is allowed to know about. Selecting the whole entity and
/// reading a property off it would put the business's name, contact address and ABN in the hands of
/// a <c>Reports.View</c> caller's call stack for no reason.</para>
/// </summary>
public sealed class EfGstRegistrationStore : IGstRegistrationStore
{
    private readonly AppDbContext _db;

    public EfGstRegistrationStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool?> FindGstRegisteredAsync(BusinessId businessId, CancellationToken cancellationToken)
    {
        // The extra nullable layer separates "no such business" from "a business that recorded no
        // answer". Both are a null registration to the caller - neither licenses a BAS estimate -
        // but conflating them inside the query would make a missing row look like a stored value.
        var found = await _db.Businesses
            .AsNoTracking()
            .Where(business => business.Id == businessId.Value)
            .Select(business => new { business.GstRegistered })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return found?.GstRegistered;
    }
}
