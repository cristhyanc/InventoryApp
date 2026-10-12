using Inventory.Application.Members;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMemberInviteRedemptionStore"/> (issue #532). Like every
/// other adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence
/// models it reads and writes.
///
/// <para><strong>It is the whole of what a caller with no business can reach, in one file.</strong>
/// Three tables - invites, memberships and the business's status column - and five statements. The
/// request it serves carries a <em>denied</em> <c>BusinessScope</c>, because nothing resolved a
/// business for a person who is not yet a member of one, and the two tables it writes are
/// structurally non-tenant-owned (docs/architecture.md § Tenant ownership), so no query filter
/// hides a row from it and <c>BusinessOwnershipEnforcer</c> stamps nothing for it. That makes the
/// business id in these statements the entire boundary, which is why every one of them names it and
/// why it comes from <see cref="RedeemableMemberInvite.BusinessId"/> - the invite row the presented
/// token identified - and never from the request. There is no <c>IgnoreQueryFilters</c>, no raw SQL
/// and no unscoped context anywhere in this path.</para>
///
/// <para><strong>Nothing here reads or writes business data.</strong> No product, purchase, sale or
/// document is reachable through this adapter: those are all tenant-owned, the request's scope is
/// denied, and the filters are therefore fully in force over everything else on the context.</para>
///
/// <para><strong>Every call runs inside the caller's transaction.</strong> The redemption invokes
/// these methods from inside <c>MembershipWriteGuard</c>'s <c>BEGIN IMMEDIATE</c> transaction on
/// this same context, so the insert's <c>SaveChanges</c> and the two conditional
/// <c>ExecuteUpdate</c> statements all commit or roll back together with it.</para>
/// </summary>
public sealed class EfMemberInviteRedemptionStore : IMemberInviteRedemptionStore
{
    private readonly AppDbContext _db;

    public EfMemberInviteRedemptionStore(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The invite a token answers to, with its business's status read in the same statement.
    ///
    /// <para>It is an inner join, so an invite whose business row is missing is simply not found -
    /// the fail-closed direction, since there is no status to judge it by. Two matches are refused
    /// the same way: the per-business unique index deliberately allows two businesses to hold one
    /// hash, so "which business did this token mean?" has no answer, and an ambiguous invite is
    /// denied rather than resolved by picking one, exactly as an ambiguous membership is. A row
    /// whose owner column is not a usable key is refused by <see cref="BusinessId.TryFrom"/> for
    /// the same reason.</para>
    /// </summary>
    public async Task<RedeemableMemberInvite?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken)
    {
        var matches = await _db.MemberInvites
            .AsNoTracking()
            .Where(invite => invite.TokenHash == tokenHash)
            .Join(
                _db.Businesses.AsNoTracking(),
                invite => invite.BusinessId,
                business => business.Id,
                (invite, business) => new
                {
                    invite.Id,
                    invite.BusinessId,
                    invite.Role,
                    invite.Status,
                    invite.ExpiresAtUtc,
                    BusinessStatus = business.Status,
                })
            .OrderBy(row => row.Id)
            // Two is all this needs to know: one row is an answer, more than one is an ambiguity it
            // refuses, and reading the rest would tell it nothing it would act on.
            .Take(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count != 1 || !BusinessId.TryFrom(matches[0].BusinessId, out var businessId))
        {
            return null;
        }

        var row = matches[0];

        return new RedeemableMemberInvite(
            row.Id,
            businessId,
            // The stored role and status exactly as they are, including values this deployment does
            // not declare: MemberInviteRedemptionPolicy has to see them to refuse them.
            row.Role,
            row.Status,
            row.ExpiresAtUtc,
            row.BusinessStatus);
    }

    /// <inheritdoc />
    public async Task<RedeemingMemberMembership?> FindMembershipAsync(
        BusinessId businessId,
        ActorIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // The per-(actor, business) unique index allows one row, so this is a single answer rather
        // than a list. Both identity columns are NOCASE, so a hand-seeded row in another GUID
        // spelling is still this person's row.
        var row = await Membership(businessId)
            .AsNoTracking()
            .Where(membership =>
                membership.DirectoryTenantId == identity.DirectoryTenantId
                && membership.ObjectId == identity.ObjectId)
            .Select(membership => new { membership.Id, membership.IsActive })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : new RedeemingMemberMembership(row.Id, row.IsActive);
    }

    /// <inheritdoc />
    public async Task<int> CreateActiveMembershipAsync(
        BusinessId businessId,
        ActorIdentity identity,
        MemberProfile profile,
        BusinessRole role,
        DateTime createdAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(profile);

        var membership = new Models.BusinessMembership
        {
            BusinessId = businessId.Value,
            DirectoryTenantId = identity.DirectoryTenantId,
            ObjectId = identity.ObjectId,
            // Display-only, and absent stays absent rather than becoming a placeholder (issue
            // #524). Neither column is read by any access decision.
            DisplayName = profile.DisplayName,
            Email = profile.Email,
            Role = role,
            IsActive = true,
            CreatedAtUtc = createdAtUtc,
            // One instant for the creation and the state change alike, so the row never describes a
            // state it reached at some other time (issue #522).
            StatusChangedAtUtc = createdAtUtc,
        };

        _db.BusinessMemberships.Add(membership);

        // Saved inside the caller's transaction, and before returning, because MembershipWriteGuard
        // re-reads the identity's and the business's rows afterwards to judge what this write left
        // behind - an unsaved change would leave it judging the old state. This is also where the
        // filtered one-active-membership index can refuse the insert, which the guard turns into
        // the stable membership conflict.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return membership.Id;
    }

    /// <inheritdoc />
    public async Task<bool> ReactivateMembershipAsync(
        BusinessId businessId,
        int membershipId,
        BusinessRole role,
        MemberProfile profile,
        DateTime reactivatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var displayName = profile.DisplayName;
        var email = profile.Email;

        var updated = await Membership(businessId)
            .Where(membership => membership.Id == membershipId)
            // "Only while it is still revoked", in the WHERE clause rather than checked a moment
            // earlier: a row another caller reactivated in between is not reactivated again on the
            // strength of a stale read, it simply does not match.
            .Where(membership => !membership.IsActive)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(membership => membership.IsActive, true)
                    .SetProperty(membership => membership.Role, role)
                    .SetProperty(membership => membership.StatusChangedAtUtc, reactivatedAtUtc)
                    // A claim the token did not carry leaves the stored value alone rather than
                    // erasing it: a token that says nothing must not delete what an earlier one
                    // said (issue #524).
                    .SetProperty(
                        membership => membership.DisplayName,
                        membership => displayName ?? membership.DisplayName)
                    .SetProperty(
                        membership => membership.Email,
                        membership => email ?? membership.Email),
                cancellationToken)
            .ConfigureAwait(false);

        return updated == 1;
    }

    /// <inheritdoc />
    public async Task<bool> MarkUsedAsync(
        BusinessId businessId,
        int inviteId,
        int membershipId,
        DateTime usedAtUtc,
        CancellationToken cancellationToken)
    {
        var updated = await _db.MemberInvites
            .Where(invite => invite.BusinessId == businessId.Value && invite.Id == inviteId)
            // The liveness condition of MemberInvitePolicy.IsLive, in SQL, so the check and the
            // write are one operation: a second redemption of the same link matches nothing here.
            .Where(invite =>
                invite.Status == MemberInviteStatus.Pending && invite.ExpiresAtUtc > usedAtUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(invite => invite.Status, MemberInviteStatus.Used)
                    // All three in the one statement as the state they describe, so the row never
                    // records a redemption instant, or a membership, for a state it does not hold.
                    .SetProperty(invite => invite.UsedAtUtc, usedAtUtc)
                    .SetProperty(invite => invite.RedeemedByMembershipId, membershipId),
                cancellationToken)
            .ConfigureAwait(false);

        return updated == 1;
    }

    /// <summary>
    /// One business's memberships. Every membership statement in this adapter starts here, so the
    /// owner cannot be omitted on one of them - and the business id it names only ever came from the
    /// invite row, never from the request.
    /// </summary>
    private IQueryable<Models.BusinessMembership> Membership(BusinessId businessId) =>
        _db.BusinessMemberships.Where(membership => membership.BusinessId == businessId.Value);
}
