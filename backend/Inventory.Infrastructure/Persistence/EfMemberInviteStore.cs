using System.Linq.Expressions;
using Inventory.Application.Members;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMemberInviteStore"/> (issue #531, re-scoped by the
/// reviewed ownership decision on issue #532). Like every other adapter in this folder it lives
/// beside the <see cref="AppDbContext"/> and the persistence model it reads and writes.
///
/// <para><strong>Why the business id is in every statement.</strong> <c>MemberInvite</c> is one of
/// the five structural non-tenant-owned tables (docs/architecture.md § Tenant ownership): the
/// redemption of issue #532 has to find an invite by the token a caller with no membership presents,
/// and a central query filter keyed to a resolved business can never match that - so the table
/// carries no filter, and the boundary for it is the business id in the <c>WHERE</c> clause. It is
/// only ever one tenancy resolution produced from the authenticated actor's membership, and pairing
/// it with the invite key in every lookup is what makes another business's invite not found rather
/// than reachable. This is the same shape, for the same reason, as
/// <see cref="EfBusinessMemberStore"/>, and it is not the per-controller <c>Where</c> clause the
/// ownership rule warns against: there is no central mechanism for this table to defer to.</para>
///
/// <para><strong>The two cancellations are conditional <c>ExecuteUpdate</c> statements.</strong>
/// Each names only the two columns a cancellation changes and carries its own "only while this
/// invite is still live" condition beside the business, so the comparison and the write are one
/// operation in the database rather than a read followed by a trusting write.</para>
///
/// <para>The token's plaintext never reaches this adapter: the port carries
/// <see cref="NewMemberInvite.TokenHash"/>, which is all a row holds and all a redemption needs,
/// and neither read below projects even that.</para>
/// </summary>
public sealed class EfMemberInviteStore : IMemberInviteStore
{
    /// <summary>
    /// The one projection both reads use, so a column added to the entity cannot reach one read and
    /// not the other. It is an expression rather than a method so EF translates it into the
    /// <c>SELECT</c> list, and it deliberately omits <c>TokenHash</c>: reading invites must never
    /// load the one value that has to stay in the database.
    /// </summary>
    private static readonly Expression<Func<Models.MemberInvite, StoredMemberInvite>> Projection =
        invite => new StoredMemberInvite(
            invite.Id,
            invite.Role,
            invite.Note,
            invite.Status,
            invite.CreatedAtUtc,
            invite.ExpiresAtUtc,
            invite.UsedAtUtc,
            invite.CancelledAtUtc,
            invite.RedeemedByMembershipId);

    private readonly AppDbContext _db;

    public EfMemberInviteStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<StoredMemberInvite> CreateAsync(
        BusinessId businessId,
        NewMemberInvite invite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invite);

        // The owner is written here rather than stamped by BusinessOwnershipEnforcer, which does
        // not see this table any more. The value is the one the caller's membership resolved to; no
        // request value can reach it, and nothing ever updates the column afterwards.
        var row = new Models.MemberInvite
        {
            BusinessId = businessId.Value,
            Role = invite.Role,
            Note = invite.Note,
            TokenHash = invite.TokenHash,
            Status = MemberInviteStatus.Pending,
            CreatedByDirectoryTenantId = invite.CreatedBy.DirectoryTenantId,
            CreatedByObjectId = invite.CreatedBy.ObjectId,
            CreatedAtUtc = invite.CreatedAtUtc,
            ExpiresAtUtc = invite.ExpiresAtUtc,
        };

        _db.MemberInvites.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new StoredMemberInvite(
            row.Id,
            row.Role,
            row.Note,
            row.Status,
            row.CreatedAtUtc,
            row.ExpiresAtUtc,
            row.UsedAtUtc,
            row.CancelledAtUtc,
            row.RedeemedByMembershipId);
    }

    /// <summary>
    /// One business's invites, ordered by their own key - the order they were created in.
    /// Deterministic without the caller sorting, and deliberately not ordered by status or expiry:
    /// both change over time, so an ordering built on them would reshuffle a list for reasons
    /// nobody asked for. Presentation ordering belongs to the page.
    /// </summary>
    public async Task<IReadOnlyList<StoredMemberInvite>> ListAsync(
        BusinessId businessId,
        CancellationToken cancellationToken) =>
        await Of(businessId)
            .AsNoTracking()
            .OrderBy(invite => invite.Id)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<StoredMemberInvite?> FindAsync(
        BusinessId businessId,
        int inviteId,
        CancellationToken cancellationToken) =>
        Of(businessId)
            .AsNoTracking()
            .Where(invite => invite.Id == inviteId)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> CancelAsync(
        BusinessId businessId,
        int inviteId,
        DateTime cancelledAtUtc,
        CancellationToken cancellationToken)
    {
        var updated = await Live(businessId, cancelledAtUtc)
            .Where(invite => invite.Id == inviteId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(invite => invite.Status, MemberInviteStatus.Cancelled)
                    // Set in the same statement as the state it describes, never afterwards, so the
                    // row never records a cancellation instant for a state it does not hold.
                    .SetProperty(invite => invite.CancelledAtUtc, cancelledAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        return updated == 1;
    }

    /// <inheritdoc />
    public Task<int> CancelAllLiveAsync(
        BusinessId businessId,
        DateTime cancelledAtUtc,
        CancellationToken cancellationToken) =>
        // One statement, so a caller that has already opened a transaction on this context - the
        // business deactivation of #509 - has these cancellations commit or roll back with its own
        // write, with no second round trip in between.
        Live(businessId, cancelledAtUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(invite => invite.Status, MemberInviteStatus.Cancelled)
                    .SetProperty(invite => invite.CancelledAtUtc, cancelledAtUtc),
                cancellationToken);

    /// <summary>
    /// One business's invites. Every statement in this adapter starts here, which is what makes the
    /// owner impossible to omit on one method: there is no query over this table that does not go
    /// through it.
    /// </summary>
    private IQueryable<Models.MemberInvite> Of(BusinessId businessId) =>
        _db.MemberInvites.Where(invite => invite.BusinessId == businessId.Value);

    /// <summary>
    /// The invites of one business that may still be cancelled at <paramref name="nowUtc"/>:
    /// pending, and not past their expiry. It is the SQL half of <c>MemberInvitePolicy.IsLive</c> -
    /// the same two conditions, in a <c>WHERE</c> clause so the check and the write cannot be
    /// separated - with the owning business in that clause beside them.
    /// </summary>
    private IQueryable<Models.MemberInvite> Live(BusinessId businessId, DateTime nowUtc) =>
        Of(businessId).Where(invite =>
            invite.Status == MemberInviteStatus.Pending && invite.ExpiresAtUtc > nowUtc);
}
