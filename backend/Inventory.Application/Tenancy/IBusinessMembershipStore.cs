using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// Narrow persistence port for application-owned business memberships.
///
/// It returns <em>every</em> matching membership rather than a single best match: deciding what
/// zero, one, duplicate, or several memberships mean is the deterministic job of
/// <see cref="BusinessMembershipResolutionPolicy"/>, and an adapter that silently picked one
/// would hide exactly the ambiguity the boundary has to fail closed on.
/// </summary>
public interface IBusinessMembershipStore
{
    Task<IReadOnlyList<ActorBusinessMembership>> FindMembershipsAsync(
        ActorIdentity actor,
        CancellationToken cancellationToken);
}
