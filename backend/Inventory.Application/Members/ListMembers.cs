using Inventory.Application.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// The current business's members, for the Owner who manages them (issue #533).
///
/// It takes no identifier at all: the business comes from the authenticated actor's membership
/// through <see cref="ICurrentBusinessProvider"/>, so no route, query, body or header value can
/// choose whose members are listed. Revoked memberships are included, because a revoked approval is
/// the one state an Owner can act on by reinstating it, and the list is the only place it is
/// visible.
/// </summary>
public sealed class ListMembers
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IBusinessMemberStore _store;

    public ListMembers(ICurrentBusinessProvider currentBusiness, IBusinessMemberStore store)
    {
        _currentBusiness = currentBusiness;
        _store = store;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership, so there is no business whose members could be listed.
    /// </exception>
    public async Task<IReadOnlyList<BusinessMember>> Handle(CancellationToken cancellationToken)
    {
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken)
            .ConfigureAwait(false);

        return await _store.ListAsync(businessId, cancellationToken).ConfigureAwait(false);
    }
}
