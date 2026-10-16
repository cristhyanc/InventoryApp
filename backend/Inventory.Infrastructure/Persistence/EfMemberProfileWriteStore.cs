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
    /// One <c>UPDATE</c>, naming only the columns the token actually reported and matching one row.
    ///
    /// Everything that decides whether to write is in the statement, not in a read this adapter
    /// made first: the identity, the active flag and "the stored values differ from these" are all
    /// in the <c>WHERE</c> clause, so there is no window between deciding and writing, and two
    /// refreshes of the same membership cannot both conclude a write is needed. The rows-affected
    /// count is therefore the answer to "was it out of date", with no second query to ask it.
    ///
    /// <para><strong>A claim the token did not carry leaves the stored value alone</strong> (issue
    /// #681). Until then this statement always named both columns, so a token that reported a name
    /// and no email address erased a stored email address - while the invite redemption's own
    /// reactivation, from the same issue #524, deliberately kept it
    /// (<c>EfMemberInviteRedemptionStore.ReactivateMembershipAsync</c>). Two write paths for one
    /// pair of columns disagreeing about that is a difference a member sees: their email address
    /// disappears from the Owner's members list and comes back, depending on which path ran last and
    /// which claims their identity provider happened to include. The redemption's rule is the one
    /// kept, because an absent claim is not a statement that the value is gone - it is the absence
    /// of a statement - and because this column exists to show an Owner who their members are.
    /// Erasing a member's email address because one token omitted a claim makes that worse, not more
    /// private. A value a token reports as <em>different</em> is still written, and a member's
    /// details are still never more than what their own latest token said about them.</para>
    ///
    /// <c>ExecuteUpdateAsync</c> rather than loading the entity and calling <c>SaveChangesAsync</c>:
    /// the point of this path is that it cannot touch anything else. It names only display columns,
    /// so the role, the revocation flag and both instants are untouchable here even by mistake, and
    /// no membership entity is tracked, which keeps a cosmetic refresh from saving anything else a
    /// context in its scope happened to be holding.
    ///
    /// <c>BusinessMembership</c> carries no tenant query filter - it is one of the five structural
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

        // One row: the identity's own active membership. A revoked row is history and keeps the
        // details it was revoked with.
        var membershipOfThisIdentity = _db.BusinessMemberships
            .Where(membership =>
                membership.DirectoryTenantId == identity.DirectoryTenantId
                && membership.ObjectId == identity.ObjectId
                && membership.IsActive);

        // Which columns the statement names is decided here, in C#, rather than inside the
        // expression: `ExecuteUpdate` takes an expression that cannot be composed at runtime, and a
        // "write it only if the claim was present" condition inside one setter is not something EF
        // can translate. The difference comparisons stay null-aware by EF Core's own null semantics,
        // which is what makes the first refresh of a membership that has never stored a value count
        // as a difference.
        if (displayName is not null && email is not null)
        {
            return membershipOfThisIdentity
                .Where(membership =>
                    membership.DisplayName != displayName || membership.Email != email)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(membership => membership.DisplayName, displayName)
                        .SetProperty(membership => membership.Email, email),
                    cancellationToken);
        }

        if (displayName is not null)
        {
            return membershipOfThisIdentity
                .Where(membership => membership.DisplayName != displayName)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(membership => membership.DisplayName, displayName),
                    cancellationToken);
        }

        if (email is not null)
        {
            return membershipOfThisIdentity
                .Where(membership => membership.Email != email)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(membership => membership.Email, email),
                    cancellationToken);
        }

        // Unreachable: RefreshMemberProfile refuses a profile with no value at all before it reaches
        // this port. Answering "no row changed" rather than running a statement that names no column
        // is the fail-safe direction if a later caller ever gets here.
        return Task.FromResult(0);
    }
}
