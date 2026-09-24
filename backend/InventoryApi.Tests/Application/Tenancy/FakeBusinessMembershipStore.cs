using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Tenancy;

/// <summary>
/// In-memory fake of the membership port. It records the actor it was asked about so a test can
/// prove the store is queried with the identity the boundary supplied, and counts queries so the
/// per-scope memoisation can be verified.
/// </summary>
public sealed class FakeBusinessMembershipStore : IBusinessMembershipStore
{
    private readonly IReadOnlyList<ActorBusinessMembership> _memberships;

    public FakeBusinessMembershipStore(params ActorBusinessMembership[] memberships)
    {
        _memberships = memberships;
    }

    public ActorIdentity? LastQueriedActor { get; private set; }

    public int QueryCount { get; private set; }

    public Task<IReadOnlyList<ActorBusinessMembership>> FindMembershipsAsync(
        ActorIdentity actor,
        CancellationToken cancellationToken)
    {
        LastQueriedActor = actor;
        QueryCount++;
        return Task.FromResult(_memberships);
    }
}
