using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// The mutable per-request implementation of <see cref="IBusinessScope"/>.
///
/// It starts <see cref="BusinessScopeState.Denied"/> and can only ever be moved forward to a
/// resolved business, once. That ordering is the safety property: a request that has not yet
/// been through business resolution, or that failed it, reads and writes nothing, and nothing
/// later in the request can quietly repoint it at a different business.
/// </summary>
public sealed class BusinessScope : IBusinessScope
{
    public BusinessScopeState State { get; private set; } = BusinessScopeState.Denied;

    public int? BusinessId { get; private set; }

    /// <summary>
    /// Publishes the resolved business for the rest of the request. Called once, by the API
    /// boundary, after membership resolution has succeeded.
    /// </summary>
    public void Resolve(BusinessId businessId)
    {
        if (State == BusinessScopeState.Resolved)
        {
            throw new InvalidOperationException(
                "The current business has already been resolved for this scope and cannot be changed.");
        }

        State = BusinessScopeState.Resolved;
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
