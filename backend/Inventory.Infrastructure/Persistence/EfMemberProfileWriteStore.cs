using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMemberProfileWriteStore"/> (issue #524). Like every
/// other adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence
/// model it writes.
/// </summary>
public sealed class EfMemberProfileWriteStore : IMemberProfileWriteStore
{
    private readonly AppDbContext _db;

    public EfMemberProfileWriteStore(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// One <c>UPDATE</c>, naming two columns and matching one row.
    ///
    /// Everything that decides whether to write is in the statement, not in a read this adapter
    /// made first: the identity, the active flag and "the stored values differ from these" are all
    /// in the <c>WHERE</c> clause, so there is no window between deciding and writing, and two
    /// refreshes of the same membership cannot both conclude a write is needed. The rows-affected
    /// count is therefore the answer to "was it out of date", with no second query to ask it.
    ///
    /// <c>ExecuteUpdateAsync</c> rather than loading the entity and calling <c>SaveChangesAsync</c>:
    /// the point of this path is that it cannot touch anything else. It names the two display
    /// columns, so the role, the revocation flag and both instants are untouchable here even by
    /// mistake, and no membership entity is tracked, which keeps a cosmetic refresh from saving
    /// anything else a context in its scope happened to be holding.
    ///
    /// <c>BusinessMembership</c> carries no tenant query filter - it is one of the three structural
    /// non-tenant-owned tables (docs/architecture.md § Tenant ownership) - so this update works in
    /// the drain's own scope, which has no business resolved. What stops it reaching another
    /// business's member is not a tenant filter but the identity in the <c>WHERE</c> clause: it is
    /// the <c>(tid, oid)</c> pair the request authenticated as, and nothing a request supplies can
    /// choose it.
    /// </summary>
    public Task<int> RefreshActiveMembershipProfileAsync(
        ActorIdentity identity,
        MemberProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(profile);

        var displayName = profile.DisplayName;
        var email = profile.Email;

        return _db.BusinessMemberships
            .Where(membership =>
                membership.DirectoryTenantId == identity.DirectoryTenantId
                && membership.ObjectId == identity.ObjectId
                // A revoked row is history: it keeps the details it was revoked with.
                && membership.IsActive
                // Null-aware by EF Core's own null semantics, which is what makes the first refresh
                // of a membership that has never stored either value count as a difference. The
                // relational tests cover both directions, including a value becoming null again.
                && (membership.DisplayName != displayName || membership.Email != email))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(membership => membership.DisplayName, displayName)
                    .SetProperty(membership => membership.Email, email),
                cancellationToken);
    }
}
