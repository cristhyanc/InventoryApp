namespace Inventory.Domain.Tenancy;

/// <summary>
/// One membership fact: this actor is approved for this business. <paramref name="IsActive"/>
/// is the membership's own state and <paramref name="BusinessIsActive"/> is the owning
/// business's state; both are carried so the resolution rule stays deterministic and testable
/// without a second lookup.
/// </summary>
public readonly record struct ActorBusinessMembership(
    BusinessId BusinessId,
    bool IsActive,
    bool BusinessIsActive);
