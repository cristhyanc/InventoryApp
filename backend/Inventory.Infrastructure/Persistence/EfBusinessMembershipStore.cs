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
    /// owning business's lifecycle status comes back in the same round trip.
    ///
    /// Three facts travel with each row beyond the membership itself, and all three are read here
    /// so one query answers both rules that consume them: the business's
    /// <see cref="BusinessStatus"/> (issue #540), which <see cref="BusinessMembershipResolutionPolicy"/>
    /// resolves access from; the business's rejection reason, which
    /// <see cref="Inventory.Domain.Tenancy.AccountStatePolicy"/> reports to a refused applicant's
    /// own identity; and the membership's own <c>StatusChangedAtUtc</c> (issue #522), which that
    /// precedence orders inactive records by. Reading them in a second query would let the rule
    /// decide on a status and a reason that no longer belong together.
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
                    membership.StatusChangedAtUtc,
                    BusinessStatus = business.Status,
                    business.RejectionReason,
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
                // The stored status exactly as it is, for the same reason the role is: an
                // undeclared value is the one case the resolution policy has to deny.
                row.BusinessStatus,
                row.StatusChangedAtUtc,
                row.RejectionReason))
            .ToList();
    }
}
