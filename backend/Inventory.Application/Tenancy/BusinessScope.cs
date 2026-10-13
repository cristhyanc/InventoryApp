using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// The mutable per-request implementation of <see cref="IBusinessScope"/>.
///
/// It starts <see cref="BusinessScopeState.Denied"/> and can only ever be moved forward to a
/// resolved business, once. That ordering is the safety property: a request that has not yet
/// been through business resolution, or that failed it, reads and writes nothing, and nothing
/// later in the request can quietly repoint it at a different business.
///
/// <para>Issue #551 adds the second way forward, and it is the same shape: a support session
/// resolves the business it was granted for as <see cref="BusinessScopeState.SupportReadOnly"/>,
/// also once, and also with no way back to <see cref="BusinessScopeState.Denied"/> or across to
/// the other resolved state. One request is either a member acting in their own business or a
/// platform administrator reading one business through a session - never both, and never one and
/// then the other.</para>
/// </summary>
public sealed class BusinessScope : IBusinessScope
{
    public BusinessScopeState State { get; private set; } = BusinessScopeState.Denied;

    public int? BusinessId { get; private set; }

    /// <summary>
    /// Publishes the resolved business for the rest of the request. Called once, by the API
    /// boundary, after membership resolution has succeeded.
    /// </summary>
    public void Resolve(BusinessId businessId) =>
        MoveTo(BusinessScopeState.Resolved, businessId);

    /// <summary>
    /// Publishes the business a platform administrator's support session grants read access to
    /// (issue #551). Called once, by the API boundary, after the presented session id has been
    /// matched to an active session of that same administrator and the business has been confirmed
    /// to be active.
    ///
    /// <para>The business id is the one stored on the session row the server matched, never a
    /// value the request supplied: the request carries a session id and nothing else that could
    /// name a business.</para>
    /// </summary>
    public void ResolveForSupportSession(BusinessId businessId) =>
        MoveTo(BusinessScopeState.SupportReadOnly, businessId);

    private void MoveTo(BusinessScopeState state, BusinessId businessId)
    {
        if (State is BusinessScopeState.Resolved or BusinessScopeState.SupportReadOnly)
        {
            throw new InvalidOperationException(
                "The current business has already been resolved for this scope and cannot be changed.");
        }

        State = state;
        BusinessId = businessId.Value;
    }
}

/// <summary>
/// A scope that applies no tenant filtering, for the legacy direct-construction path described
/// on <see cref="BusinessScopeState.Unscoped"/>. It is never registered in the composition root.
/// </summary>
public sealed class UnscopedBusinessScope : IBusinessScope
{
    public static UnscopedBusinessScope Instance { get; } = new();

    public BusinessScopeState State => BusinessScopeState.Unscoped;

    public int? BusinessId => null;
}
