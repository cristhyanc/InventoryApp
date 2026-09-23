namespace Inventory.Application.Tenancy;

/// <summary>
/// The current business as the persistence layer needs it: synchronously, and without an await.
///
/// <see cref="ICurrentBusinessProvider"/> is the abstraction use cases call; resolving it is
/// asynchronous because it reads membership rows. EF Core global query filters and SaveChanges
/// enforcement cannot await, so the resolved answer is published here once per request and read
/// from here afterwards.
/// </summary>
public interface IBusinessScope
{
    BusinessScopeState State { get; }

    /// <summary>
    /// The current business key, or <c>null</c> when this scope is not
    /// <see cref="BusinessScopeState.Resolved"/>. Persistence must treat <c>null</c> as
    /// "no business data at all", never as "all businesses".
    /// </summary>
    int? BusinessId { get; }
}

public enum BusinessScopeState
{
    /// <summary>
    /// No business has been resolved for this scope and none will be. Persistence fails closed:
    /// tenant-owned reads return nothing and tenant-owned writes are rejected.
    /// </summary>
    Denied = 0,

    /// <summary>A business was resolved; its data, and only its data, is in scope.</summary>
    Resolved = 1,

    /// <summary>
    /// Tenant scoping is deliberately not applied: this scope sees and may write every
    /// business's data.
    ///
    /// It is never a default and never arrives by omission - a caller has to pass
    /// <see cref="UnscopedBusinessScope.Instance"/> explicitly to get it. It exists for
    /// controlled setup, migration and cross-business maintenance code. The composition root
    /// never produces it (see the DI test that pins that down), so no authenticated request can
    /// run unscoped.
    /// </summary>
    Unscoped = 2,
}
