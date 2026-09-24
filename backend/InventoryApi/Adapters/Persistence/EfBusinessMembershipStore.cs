using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IBusinessMembershipStore"/>. Like the other
/// adapters in this folder it lives in InventoryApi, not Inventory.Infrastructure, because it
/// depends on <see cref="AppDbContext"/> and the persistence models, which still live in
/// InventoryApi. Move it once those relocate.
/// </summary>
public sealed class EfBusinessMembershipStore : IBusinessMembershipStore
{
    private readonly AppDbContext _db;

    public EfBusinessMembershipStore(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns every membership recorded for the actor, including revoked ones and duplicates
    /// across businesses. The query deliberately applies no "take the first" or "only active"
    /// narrowing: <see cref="BusinessMembershipResolutionPolicy"/> owns that decision, and
    /// hiding a second row here would turn an ambiguous actor into a silently resolved one.
    ///
    /// It joins Businesses explicitly rather than relying on the lazy-loading proxy so the
    /// owning business's active flag comes back in the same round trip.
    /// </summary>
    public async Task<IReadOnlyList<ActorBusinessMembership>> FindMembershipsAsync(
        ActorIdentity actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var rows = await _db.BusinessMemberships
            .AsNoTracking()
            .Where(membership =>
                membership.DirectoryTenantId == actor.DirectoryTenantId
                && membership.ObjectId == actor.ObjectId)
            .Join(
                _db.Businesses.AsNoTracking(),
                membership => membership.BusinessId,
                business => business.Id,
                (membership, business) => new
                {
                    business.Id,
                    MembershipIsActive = membership.IsActive,
                    BusinessIsActive = business.IsActive,
                })
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new ActorBusinessMembership(
                BusinessId.From(row.Id),
                row.MembershipIsActive,
                row.BusinessIsActive))
            .ToList();
    }
}
