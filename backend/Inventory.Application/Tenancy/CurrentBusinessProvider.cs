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
///
/// <para><strong>A support session is the one other way a business becomes current, and only for
/// <see cref="RequireBusinessIdAsync"/> (issue #551).</strong> A platform administrator reading a
/// business through a session is a member of nothing, so <see cref="ResolveAsync"/> keeps meaning
/// exactly "what does this actor's membership resolve to" - denied - and
/// <see cref="RequireRoleAsync"/> keeps failing closed, because there is no role to report and
/// inventing one would put a business role on somebody who holds none. What a support request does
/// have is a business, and <see cref="RequireBusinessIdAsync"/> answers with it so a use case that
/// has to pass a business id to a port acts in the business the tenant query filters are already
/// scoped to.</para>
///
/// <para>That answer is read from <see cref="IBusinessScope"/> rather than resolved again here, and
/// the reason is that the two must be the same business by construction: the API boundary publishes
/// the scope after matching the presented session id, the query filters read it, and so does this.
/// A second resolution would be a second chance to disagree. It is safe to read at this point in
/// the request precisely because <see cref="RequireBusinessIdAsync"/> is called by use cases, which
/// run after that middleware - unlike the capability authorization handler, which runs before it
/// and therefore resolves the session itself.</para>
/// </summary>
public sealed class CurrentBusinessProvider : ICurrentBusinessProvider
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IBusinessMembershipStore _membershipStore;
    private readonly IBusinessScope _businessScope;
    private BusinessMembershipResolution? _resolution;

    public CurrentBusinessProvider(
        IAuthenticatedActorAccessor actorAccessor,
        IBusinessMembershipStore membershipStore,
        IBusinessScope businessScope)
    {
        _actorAccessor = actorAccessor;
        _membershipStore = membershipStore;
        _businessScope = businessScope;
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
        // The support-session business comes first and is not a fallback in either direction: a
        // request is either inside a session or it is not, and the scope published at the boundary
        // is the one fact that says which. A session's business is already the business every
        // tenant-owned read in this request is filtered to, so answering with anything else here
        // would let a use case act on a business it cannot read.
        if (_businessScope.State == BusinessScopeState.SupportReadOnly &&
            _businessScope.BusinessId is { } supportedBusinessId &&
            BusinessId.TryFrom(supportedBusinessId, out var supported))
        {
            return supported;
        }

        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);

        return resolution.ResolvedBusinessId
            ?? throw new BusinessAccessDeniedException(
                resolution.DenialReason ?? BusinessAccessDenialReason.NotAuthenticated);
    }

    public async Task<BusinessRole> RequireRoleAsync(CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);

        // Reads the same memoised membership resolution, so a member's role and the business they
        // act in always come from one membership lookup and cannot disagree. A support session
        // deliberately resolves no role here: the administrator holds none, and what a support
        // request may do comes from Access.CurrentCapabilities' own fixed set instead.
        return resolution.ResolvedRole
            ?? throw new BusinessAccessDeniedException(
                resolution.DenialReason ?? BusinessAccessDenialReason.NotAuthenticated);
    }
}
