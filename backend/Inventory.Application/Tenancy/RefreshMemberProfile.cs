using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>What one refresh did. Returned rather than logged, because the values must stay unlogged.</summary>
public enum MemberProfileRefreshOutcome
{
    /// <summary>The token reported neither a display name nor an email address, so nothing was written.</summary>
    NothingToStore,

    /// <summary>
    /// No row changed: the stored profile already matched the token, or the identity holds no
    /// active membership. Both are ordinary outcomes and neither is an error.
    /// </summary>
    NoRowChanged,

    /// <summary>The membership's two display columns were brought up to date.</summary>
    Refreshed,
}

/// <summary>
/// Stores the display-only profile a validated token reported on the caller's own active membership
/// (issue #524).
///
/// It runs outside the request that scheduled it, in its own service scope, so the
/// <c>AppDbContext</c> it writes through carries a <em>denied</em> business scope: the tenant query
/// filters and <c>BusinessOwnershipEnforcer</c> are fully in force, and this use case reads and
/// writes nothing that belongs to a business. <c>BusinessMembership</c> is one of the four
/// structural non-tenant-owned tables (docs/architecture.md § Tenant ownership), which is what lets
/// the refresh happen without a current business at all - and that is deliberate, because a
/// cosmetic write must not be the thing that needs a resolved tenant.
///
/// The identity is the one the request authenticated as. Nothing a request supplies chooses whose
/// row is written, and the two values it writes decide nothing: they are never read by an
/// authorization, membership, role or capability decision.
/// </summary>
public sealed class RefreshMemberProfile
{
    private readonly IMemberProfileWriteStore _store;

    public RefreshMemberProfile(IMemberProfileWriteStore store)
    {
        _store = store;
    }

    public async Task<MemberProfileRefreshOutcome> Handle(
        ActorIdentity identity,
        MemberProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(profile);

        // Re-checked here rather than trusted from the scheduler: this use case is what writes, so
        // "store nothing when the token said nothing" has to hold for every caller of it.
        if (!profile.HasAnyValue)
        {
            return MemberProfileRefreshOutcome.NothingToStore;
        }

        var changed = await _store
            .RefreshActiveMembershipProfileAsync(identity, profile, cancellationToken)
            .ConfigureAwait(false);

        return changed > 0
            ? MemberProfileRefreshOutcome.Refreshed
            : MemberProfileRefreshOutcome.NoRowChanged;
    }
}
