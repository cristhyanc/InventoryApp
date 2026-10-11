namespace Inventory.Domain.Tenancy;

/// <summary>
/// One membership fact: this actor is approved for this business, in this role.
/// </summary>
/// <param name="BusinessId">The business this membership approves the actor for.</param>
/// <param name="Role">
/// The stored role, exactly as persistence read it (issue #521) - including a value no
/// <see cref="BusinessRole"/> declares. It is deliberately not validated or corrected here:
/// <see cref="BusinessMembershipResolutionPolicy"/> is the one place that decides what an
/// unrecognised role means, and an adapter that quietly replaced it with a default would be
/// granting access nobody recorded.
/// </param>
/// <param name="IsActive">
/// The membership's own state. Carried alongside <paramref name="BusinessIsActive"/> so the
/// resolution rule stays deterministic and testable without a second lookup.
/// </param>
/// <param name="BusinessIsActive">The owning business's state.</param>
public readonly record struct ActorBusinessMembership(
    BusinessId BusinessId,
    BusinessRole Role,
    bool IsActive,
    bool BusinessIsActive);
