using Inventory.Application.Members;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IBusinessMemberStore"/> (issue #533). Like every other
/// adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence model
/// it reads and writes.
///
/// <para><strong>Why the business id is in every statement.</strong> <c>BusinessMembership</c> is
/// one of the four structural non-tenant-owned tables (docs/architecture.md § Tenant ownership):
/// it is what resolves the tenant boundary, so filtering it centrally would be circular and it
/// carries no global query filter. The boundary for this one table is therefore the business id in
/// the <c>WHERE</c> clause - and it is only ever one tenancy resolution produced from the
/// authenticated actor's membership. Pairing it with the membership key in every lookup is what
/// makes another business's member not found rather than reachable. This is not the per-controller
/// <c>Where</c> clause that rule warns about: there is no central mechanism for this table to
/// defer to.</para>
///
/// <para><strong>Why the writes are targeted updates.</strong> Each one names the single column it
/// changes (plus the state instant that belongs with a state change), so a member change cannot
/// touch the display name, the email address, the identity pair or the created instant even by
/// mistake, and no membership entity is tracked. They run inside the <c>BEGIN IMMEDIATE</c>
/// transaction <c>MembershipWriteGuard</c> opened on this same <see cref="AppDbContext"/>, so the
/// guard's re-reads see them and a refusal rolls them back with the transaction.</para>
/// </summary>
public sealed class EfBusinessMemberStore : IBusinessMemberStore
{
    private readonly AppDbContext _db;

    public EfBusinessMemberStore(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Every membership of one business, ordered by its own key - the order the memberships were
    /// created in. Deterministic without the caller sorting, and deliberately not ordered by
    /// display name or email address: both are nullable display values that a member's next
    /// sign-in may change, so an ordering built on them would reshuffle a list for reasons nobody
    /// asked for. Presentation ordering belongs to the page.
    /// </summary>
    public async Task<IReadOnlyList<BusinessMember>> ListAsync(
        BusinessId businessId,
        CancellationToken cancellationToken)
    {
        var rows = await _db.BusinessMemberships
            .AsNoTracking()
            .Where(membership => membership.BusinessId == businessId.Value)
            .OrderBy(membership => membership.Id)
            .Select(membership => new
            {
                membership.Id,
                membership.DisplayName,
                membership.Email,
                membership.Role,
                membership.IsActive,
                membership.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => ToMember(
            row.Id,
            row.DisplayName,
            row.Email,
            row.Role,
            row.IsActive,
            row.CreatedAtUtc))];
    }

    public async Task<MemberMembership?> FindAsync(
        BusinessId businessId,
        int membershipId,
        CancellationToken cancellationToken)
    {
        var row = await _db.BusinessMemberships
            .AsNoTracking()
            .Where(membership =>
                membership.BusinessId == businessId.Value && membership.Id == membershipId)
            .Select(membership => new
            {
                membership.Id,
                membership.DirectoryTenantId,
                membership.ObjectId,
                membership.DisplayName,
                membership.Email,
                membership.Role,
                membership.IsActive,
                membership.CreatedAtUtc,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        // The stored pair is parsed and normalised by the same rule the sign-in path uses, so a
        // membership written in another GUID spelling still names the same identity. A pair that is
        // not an Entra identity at all yields none, and the caller fails closed on it.
        _ = ActorIdentity.TryCreate(row.DirectoryTenantId, row.ObjectId, out var identity);

        return new MemberMembership(
            ToMember(row.Id, row.DisplayName, row.Email, row.Role, row.IsActive, row.CreatedAtUtc),
            identity,
            // The stored role exactly as it is, including a value no BusinessRole declares: a
            // change has to be measured against, and audited from, what the row really holds.
            row.Role);
    }

    public Task ChangeRoleAsync(
        BusinessId businessId,
        int membershipId,
        BusinessRole role,
        CancellationToken cancellationToken) =>
        Membership(businessId, membershipId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(membership => membership.Role, role),
                cancellationToken);

    public Task ChangeActiveAsync(
        BusinessId businessId,
        int membershipId,
        bool isActive,
        DateTime statusChangedAtUtc,
        CancellationToken cancellationToken) =>
        Membership(businessId, membershipId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(membership => membership.IsActive, isActive)
                    // Set in the same statement as the state it describes, never afterwards, so
                    // the row never records a state change instant for a state it does not hold
                    // (issue #522).
                    .SetProperty(membership => membership.StatusChangedAtUtc, statusChangedAtUtc),
                cancellationToken);

    private IQueryable<Models.BusinessMembership> Membership(BusinessId businessId, int membershipId) =>
        _db.BusinessMemberships.Where(membership =>
            membership.BusinessId == businessId.Value && membership.Id == membershipId);

    private static BusinessMember ToMember(
        int membershipId,
        string? displayName,
        string? email,
        BusinessRole role,
        bool isActive,
        DateTime createdAtUtc) =>
        new(
            membershipId,
            displayName,
            email,
            // A stored role this deployment does not declare is reported as no known role rather
            // than as the value it happens to hold: it already denies that member access
            // everywhere, and naming it would put a role nobody granted on the wire.
            BusinessRoles.IsSupported(role) ? role : null,
            isActive,
            createdAtUtc);
}
