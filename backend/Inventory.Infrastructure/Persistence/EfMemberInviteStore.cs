using System.Linq.Expressions;
using Inventory.Application.Members;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMemberInviteStore"/> (issue #531). Like every other
/// adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence model
/// it reads and writes.
///
/// <para><strong>It adds no business predicate of its own.</strong> An invite is ordinary
/// tenant-owned data: reads go through the central <see cref="AppDbContext"/> query filter and the
/// insert is stamped by <c>BusinessOwnershipEnforcer</c> on save, so one business can never read,
/// cancel or replace another's invite. This is deliberately unlike
/// <see cref="EfBusinessMemberStore"/>, which names the business in every statement because the
/// membership table has no filter to defer to.</para>
///
/// <para><strong>The two cancellations are conditional <c>ExecuteUpdate</c> statements.</strong>
/// Each names only the two columns a cancellation changes and carries its own "only while this
/// invite is still live" condition, so the comparison and the write are one operation in the
/// database rather than a read followed by a trusting write. <c>ExecuteUpdate</c> runs in the
/// database and does not pass through <c>SaveChanges</c>, so <c>BusinessOwnershipEnforcer</c> never
/// sees it: its <c>WHERE</c> clause still carries the owning business, because EF applies the same
/// query filter to the statement's source query, and this adapter additionally refuses to run at
/// all without a single resolved business - an unscoped context would otherwise be the one way an
/// <c>UPDATE</c> here could reach every business's invites. That is the same guard, for the same
/// reason, as <see cref="EfNayaxConnectionTestDraftStore"/>'s.</para>
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
        NewMemberInvite invite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invite);

        // BusinessId is left unset on purpose: BusinessOwnershipEnforcer stamps the caller's
        // business on save, which is also what refuses the write if no business is resolved.
        var row = new Models.MemberInvite
        {
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
    public async Task<IReadOnlyList<StoredMemberInvite>> ListAsync(CancellationToken cancellationToken) =>
        await _db.MemberInvites
            .AsNoTracking()
            .OrderBy(invite => invite.Id)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<StoredMemberInvite?> FindAsync(int inviteId, CancellationToken cancellationToken) =>
        _db.MemberInvites
            .AsNoTracking()
            .Where(invite => invite.Id == inviteId)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> CancelAsync(
        int inviteId,
        DateTime cancelledAtUtc,
        CancellationToken cancellationToken)
    {
        RequireCurrentBusinessId();

        var updated = await Live(cancelledAtUtc)
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
    public Task<int> CancelAllLiveAsync(DateTime cancelledAtUtc, CancellationToken cancellationToken)
    {
        RequireCurrentBusinessId();

        // One statement, so a caller that has already opened a transaction on this context - the
        // business deactivation of #509 - has these cancellations commit or roll back with its own
        // write, with no second round trip in between.
        return Live(cancelledAtUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(invite => invite.Status, MemberInviteStatus.Cancelled)
                    .SetProperty(invite => invite.CancelledAtUtc, cancelledAtUtc),
                cancellationToken);
    }

    /// <summary>
    /// The invites that may still be cancelled at <paramref name="nowUtc"/>: pending, and not past
    /// their expiry. It is the SQL half of <c>MemberInvitePolicy.IsLive</c> - the same two
    /// conditions, in a <c>WHERE</c> clause so the check and the write cannot be separated. The
    /// owning business is in that clause too, contributed by the central query filter rather than
    /// by a predicate written here.
    /// </summary>
    private IQueryable<Models.MemberInvite> Live(DateTime nowUtc) =>
        _db.MemberInvites.Where(invite =>
            invite.Status == MemberInviteStatus.Pending && invite.ExpiresAtUtc > nowUtc);

    /// <summary>
    /// A write needs exactly one resolved business. Both an unresolved caller and a deliberately
    /// unscoped context report none, and neither may write here: <c>ExecuteUpdate</c> runs in the
    /// database without passing through <c>BusinessOwnershipEnforcer</c>, so an unscoped statement
    /// would carry no business predicate and cancel every business's invites.
    /// </summary>
    private void RequireCurrentBusinessId()
    {
        if (_db.CurrentBusinessId is not null)
        {
            return;
        }

        throw new CrossBusinessAccessException(
            "A member invite cannot be cancelled without a single resolved current business. The "
                + "caller has no business membership, or the context was constructed unscoped, so "
                + "the write is refused.");
    }
}
