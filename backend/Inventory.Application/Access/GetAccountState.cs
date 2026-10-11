using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// Answers "what state is my account in?" for any signed-in person, including one who has no
/// usable membership and is therefore refused by every other business endpoint (issue #523).
///
/// It takes no identifier at all: the identity comes from the authenticated actor through
/// <see cref="IAuthenticatedActorAccessor"/>, so no route, query, body or header value can ask
/// about somebody else's account, and the answer only ever describes the caller.
///
/// <para>It reads the membership records directly through <see cref="IBusinessMembershipStore"/>
/// rather than through <see cref="ICurrentBusinessProvider"/>, because the two answer different
/// questions. The provider reports one access decision - resolved, or denied with a reason - while
/// the precedence of <see cref="AccountStatePolicy"/> is about the records themselves, including
/// the inactive ones a resolution has no use for. Reading the records keeps one rule in one place
/// instead of reconstructing it from denial reasons, and it is what issue #507 extends when a
/// business gains a status.</para>
///
/// <para><strong>Answering grants nothing.</strong> The request reached here past a
/// <em>denied</em> business scope (see <c>InventoryApi.Auth.MembershipNotRequiredEndpointAttribute</c>),
/// so the tenant query filters and the ownership enforcement are fully in force for it and it
/// reads no business data at all - the membership table it does read is one of the structural
/// non-tenant-owned tables, because filtering what resolves the boundary would be circular.</para>
///
/// <para>A caller whose token identifies nobody - no authenticated actor, or no usable
/// <c>(tid, oid)</c> pair - is reported as <see cref="AccountState.NoMembership"/>, which is their
/// situation exactly: an identity nothing can be recorded against has no membership. No lookup is
/// attempted for them, and the answer stays the same shape, so the endpoint cannot be used to tell
/// one unusable token apart from another.</para>
/// </summary>
public sealed class GetAccountState
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IBusinessMembershipStore _membershipStore;

    public GetAccountState(
        IAuthenticatedActorAccessor actorAccessor,
        IBusinessMembershipStore membershipStore)
    {
        _actorAccessor = actorAccessor;
        _membershipStore = membershipStore;
    }

    public async Task<AccountStateResult> Handle(CancellationToken cancellationToken)
    {
        var actorResult = _actorAccessor.GetCurrentActor();

        if (actorResult.Actor is null)
        {
            return Result(AccountState.NoMembership);
        }

        var memberships = await _membershipStore
            .FindMembershipsAsync(actorResult.Actor, cancellationToken)
            .ConfigureAwait(false);

        return Result(AccountStatePolicy.Determine(memberships));
    }

    // onboardingEnabled and rejectionReason are decided here rather than at the API boundary, so
    // issue #507 adds the self-service onboarding flag and the stored rejection reason in the
    // Application layer and the controller keeps only its mapping to JSON.
    private static AccountStateResult Result(AccountState state) =>
        new(state, OnboardingEnabled: false, RejectionReason: null);
}
