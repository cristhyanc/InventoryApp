using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IBusinessMembershipStore"/>. Like every other adapter in
/// this folder it lives in Inventory.Infrastructure beside the <see cref="AppDbContext"/> and the
/// persistence models it depends on, which moved there in issue #307; the adapters followed them in
/// issue #309 (Persistence 8/8 of #153).
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
                    membership.Role,
                    MembershipIsActive = membership.IsActive,
                    BusinessIsActive = business.IsActive,
                })
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new ActorBusinessMembership(
                BusinessId.From(row.Id),
                // The stored role exactly as it is, including a value no BusinessRole declares.
                // Normalising it here would hide the one case the policy has to deny.
                row.Role,
                row.MembershipIsActive,
                row.BusinessIsActive))
            .ToList();
    }
}
