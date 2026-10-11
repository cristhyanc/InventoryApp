namespace Inventory.Domain.Tenancy;

/// <summary>
/// One membership row of one business, as <see cref="BusinessOwnerRetention"/> sees it (issue
/// #522). It carries only what the rule reads: the role and whether the membership is active.
/// </summary>
/// <param name="Role">
/// The stored role, exactly as persistence read it - including a value no <see cref="BusinessRole"/>
/// declares. It is not normalised here for the same reason
/// <see cref="BusinessMembershipResolutionPolicy"/> does not normalise it: a row whose role this
/// code cannot read is not an Owner, and treating it as one would let the last real Owner be
/// removed.
/// </param>
/// <param name="IsActive">The membership's own state.</param>
public readonly record struct BusinessMembershipRole(BusinessRole Role, bool IsActive);

/// <summary>
/// The rule that a business always keeps someone who can administer it (issue #522): at least one
/// active membership in the <see cref="BusinessRole.Owner"/> role.
///
/// Only an Owner may manage members, roles, the integration and the business record
/// (<c>Inventory.Application.Access.RoleCapabilities</c>, issue #521). A business whose last
/// active Owner is revoked or demoted therefore cannot be administered by anybody inside it, and
/// nothing in the application can repair that: there is no self-service path back and no
/// cross-business write path, so the only remaining remedy is a human editing the database. That
/// is why this is checked as part of the write rather than reported afterwards.
///
/// It is checked on the state the business would be left in, not on the change that was
/// requested, so there is no list of "operations that could remove an Owner" to keep complete: a
/// revocation, a demotion, a role change in another member's row and anything a later task adds
/// are all judged the same way, by what the business still has.
/// </summary>
public static class BusinessOwnerRetention
{
    /// <summary>
    /// The one message a refusal is reported with. It states the rule rather than naming the
    /// member it would have affected.
    /// </summary>
    public const string LastActiveOwnerMessage =
        "A business must keep at least one active Owner. Give another member the Owner role first.";

    /// <summary>
    /// Whether the business those <paramref name="memberships"/> belong to still has an active
    /// Owner.
    /// </summary>
    /// <param name="memberships">
    /// Every membership recorded for the one business, active or not, read after the change inside
    /// the same write transaction. An empty or <c>null</c> collection is a business with no
    /// membership at all, which keeps no Owner and so fails the rule.
    /// </param>
    public static bool RetainsActiveOwner(IReadOnlyCollection<BusinessMembershipRole>? memberships) =>
        memberships is not null
        && memberships.Any(membership => membership.IsActive && membership.Role == BusinessRole.Owner);
}
