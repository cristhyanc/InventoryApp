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
///
/// <para>The hourly Nayax sales reconciliation (the review of issue #1104's production run) adds a
/// marked variant of <see cref="Resolve"/> for work the <em>host</em> starts on a business's behalf,
/// with no request and no signed-in user behind it: <see cref="ResolveForHostedWork"/>. It is the
/// same <see cref="BusinessScopeState.Resolved"/> state - the query filters and the ownership
/// enforcer treat it exactly as a member's scope - and the only difference is
/// <see cref="IsHostedWork"/>, which is what lets <see cref="CurrentBusinessProvider"/> answer the
/// business id for a scope that has no membership to resolve it from. It is still once-only.</para>
/// </summary>
public sealed class BusinessScope : IBusinessScope
{
    public BusinessScopeState State { get; private set; } = BusinessScopeState.Denied;

    public int? BusinessId { get; private set; }

    /// <inheritdoc />
    public bool IsHostedWork { get; private set; }

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

    /// <summary>
    /// Publishes the business a hosted background service is working for, in a service scope that
    /// service opened for that business alone. Called once, by the host adapter that opened the
    /// scope, with a business id it read from a stored row - never from a request, a message body or
    /// any other caller-supplied value.
    ///
    /// <para>It is <see cref="BusinessScopeState.Resolved"/> for every read and write, exactly like
    /// <see cref="Resolve"/>. It differs only in marking the scope as <see cref="IsHostedWork"/>, so
    /// that <see cref="CurrentBusinessProvider.RequireBusinessIdAsync"/> can answer this business
    /// without a membership. It grants no role and no capability: the scope still resolves no
    /// membership, so anything that asks for one fails closed exactly as it did before.</para>
    ///
    /// <para>The API boundary never calls it. A request's scope is resolved only by
    /// <see cref="Resolve"/> (from the caller's membership) or <see cref="ResolveForSupportSession"/>.</para>
    /// </summary>
    public void ResolveForHostedWork(BusinessId businessId)
    {
        MoveTo(BusinessScopeState.Resolved, businessId);
        IsHostedWork = true;
    }

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

    public bool IsHostedWork => false;
}
