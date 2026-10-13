using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// Narrow persistence port for the current business's members (issue #533).
///
/// Every method takes the owning <see cref="BusinessId"/>, and it is only ever one that tenancy
/// resolution produced from the authenticated actor's membership - never a value a request
/// supplied. It is passed explicitly rather than left to the <c>AppDbContext</c> tenant query
/// filters because <c>BusinessMembership</c> is one of the five structural non-tenant-owned
/// tables (docs/architecture.md § Tenant ownership): filtering the table that resolves the tenant
/// boundary would be circular, so the boundary for this one table is the business id in the
/// statement. Pairing it with the membership key in every lookup is what makes another business's
/// member "not found" rather than reachable.
///
/// The write methods deliberately change one column each and persist their own change: they are
/// called from inside <see cref="Tenancy.MembershipWriteGuard"/>'s write transaction, which
/// re-reads the business afterwards to judge the state the write left behind, so an unsaved change
/// would leave the last-Owner rule judging the old state.
/// </summary>
public interface IBusinessMemberStore
{
    /// <summary>
    /// Every membership recorded for one business, active and revoked alike, in a deterministic
    /// order the caller does not have to re-sort. Revoked rows are included because a revoked
    /// approval is what an Owner reinstates, and hiding it would make the one state they can act
    /// on invisible.
    /// </summary>
    Task<IReadOnlyList<BusinessMember>> ListAsync(BusinessId businessId, CancellationToken cancellationToken);

    /// <summary>
    /// The facts one pending change is decided from, or <see langword="null"/> when that business
    /// holds no such membership - which is the same answer for a key that exists in another
    /// business and for one that exists nowhere, so neither is confirmed.
    /// </summary>
    Task<MemberMembership?> FindAsync(
        BusinessId businessId,
        int membershipId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a member's new role, and nothing else: a role change moves what a member may do, not
    /// whether their membership is current, so it deliberately leaves
    /// <c>StatusChangedAtUtc</c> alone (issue #522).
    /// </summary>
    Task ChangeRoleAsync(
        BusinessId businessId,
        int membershipId,
        BusinessRole role,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a member's new state together with the instant it changed, in one operation, so the
    /// row never describes a state it no longer holds (issue #522).
    /// </summary>
    Task ChangeActiveAsync(
        BusinessId businessId,
        int membershipId,
        bool isActive,
        DateTime statusChangedAtUtc,
        CancellationToken cancellationToken);
}
