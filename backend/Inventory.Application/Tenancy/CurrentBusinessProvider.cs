using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// Resolves the authenticated Entra actor to an application-owned business by combining the two
/// ports with the deterministic Domain rule:
/// <list type="number">
///   <item>the InventoryApi boundary supplies the validated <c>(tid, oid)</c> pair;</item>
///   <item>the membership store returns every membership recorded for that pair;</item>
///   <item><see cref="BusinessMembershipResolutionPolicy"/> decides, failing closed on missing,
///   revoked, duplicate, or ambiguous membership.</item>
/// </list>
///
/// Registered per request scope, and the resolution is memoised for the lifetime of that scope
/// so several use cases in one request cannot disagree about the current business or the caller's
/// role in it, and do not each re-query the membership table. Nothing is cached across requests:
/// a revoked membership, and equally a changed role (issue #521), takes effect on the next
/// request.
/// </summary>
public sealed class CurrentBusinessProvider : ICurrentBusinessProvider
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IBusinessMembershipStore _membershipStore;
    private BusinessMembershipResolution? _resolution;

    public CurrentBusinessProvider(
        IAuthenticatedActorAccessor actorAccessor,
        IBusinessMembershipStore membershipStore)
    {
        _actorAccessor = actorAccessor;
        _membershipStore = membershipStore;
    }

    public async Task<BusinessMembershipResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_resolution is not null)
        {
            return _resolution;
        }

        var actorResult = _actorAccessor.GetCurrentActor();
        if (actorResult.Actor is null)
        {
            // The accessor always reports a reason when it cannot identify the actor; treating a
            // missing one as NotAuthenticated keeps the unresolved path denied rather than open.
            return _resolution = BusinessMembershipResolution.Denied(
                actorResult.DenialReason ?? BusinessAccessDenialReason.NotAuthenticated);
        }

        var memberships = await _membershipStore
            .FindMembershipsAsync(actorResult.Actor, cancellationToken)
            .ConfigureAwait(false);

        return _resolution = BusinessMembershipResolutionPolicy.Resolve(memberships);
    }

    public async Task<BusinessId> RequireBusinessIdAsync(CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);

        return resolution.ResolvedBusinessId
            ?? throw new BusinessAccessDeniedException(
                resolution.DenialReason ?? BusinessAccessDenialReason.NotAuthenticated);
    }

    public async Task<BusinessRole> RequireRoleAsync(CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);

        // Reads the same memoised resolution as the business id, so the role and the business a
        // request acts in always come from one membership lookup and cannot disagree.
        return resolution.ResolvedRole
            ?? throw new BusinessAccessDeniedException(
                resolution.DenialReason ?? BusinessAccessDenialReason.NotAuthenticated);
    }
}
