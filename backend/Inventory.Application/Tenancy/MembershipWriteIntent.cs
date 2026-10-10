using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// What a membership write means, and therefore which rules <see cref="MembershipWriteGuard"/>
/// re-checks around it (issue #522).
///
/// The operation states its own intent instead of the guard inferring it from the rows that
/// changed. Inferring would mean the guard had to understand every present and future membership
/// operation, and the one it failed to recognise would silently get no check at all; stating it
/// means an operation that forgets a rule is visible in its own call, in review.
/// </summary>
/// <param name="IdentityGainingAnActiveMembership">
/// The identity that will hold an active membership once the write completes - a creation, a
/// reactivation, or an approval that confirms one. <see cref="MembershipEligibility"/> is
/// re-checked for it inside the transaction. <c>null</c> for a write that activates nothing, such
/// as a revocation or a role change.
/// </param>
/// <param name="ExcludedMembershipId">
/// The membership the write is itself about, left out of that eligibility count so an operation on
/// an already-active membership is not refused by its own row. Only meaningful together with
/// <paramref name="IdentityGainingAnActiveMembership"/>.
/// </param>
/// <param name="BusinessKeepingAnActiveOwner">
/// The business that must still have an active Owner once the write completes - set by a
/// revocation or a role change. <see cref="BusinessOwnerRetention"/> is checked against the
/// business's rows as the write left them, so the rule is about the resulting state rather than
/// about which operation caused it. <c>null</c> when the write cannot remove an Owner, or when the
/// business is one no Owner is required for yet (an application being rejected, for example).
/// </param>
public readonly record struct MembershipWriteIntent(
    ActorIdentity? IdentityGainingAnActiveMembership = null,
    int? ExcludedMembershipId = null,
    BusinessId? BusinessKeepingAnActiveOwner = null);
