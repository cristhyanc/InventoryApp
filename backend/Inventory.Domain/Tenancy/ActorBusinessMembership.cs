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
/// The membership's own state. Carried alongside <paramref name="BusinessStatus"/> so the
/// resolution rule stays deterministic and testable without a second lookup.
/// </param>
/// <param name="BusinessStatus">
/// The owning business's lifecycle status (issue #540), exactly as persistence read it - including
/// a value this code does not declare. It replaced the former business active flag, and it is the
/// only business state any rule here reads: <see cref="BusinessMembershipResolutionPolicy"/>
/// resolves <see cref="Tenancy.BusinessStatus.Active"/> and denies everything else, and
/// <see cref="AccountStatePolicy"/> tells the three non-active statuses apart so the caller can be
/// shown what happened to their account.
/// </param>
/// <param name="StatusChangedAtUtc">
/// When this membership's own state last changed (issue #522's
/// <c>BusinessMembership.StatusChangedAtUtc</c>). It is what makes "the most recent inactive
/// record decides" answerable in <see cref="AccountStatePolicy"/>, so an old rejection never
/// outranks a newer revocation.
/// </param>
/// <param name="BusinessRejectionReason">
/// Why the owning business's application was refused, when its status is
/// <see cref="Tenancy.BusinessStatus.Rejected"/>, and <see langword="null"/> otherwise (issue
/// #540). It travels with the membership because the applicant is told their own rejection reason
/// through <see cref="AccountState.ApplicationRejected"/>; no rule reads it, and nothing else
/// about the business is carried here.
/// </param>
public readonly record struct ActorBusinessMembership(
    BusinessId BusinessId,
    BusinessRole Role,
    bool IsActive,
    BusinessStatus BusinessStatus,
    DateTime StatusChangedAtUtc,
    string? BusinessRejectionReason);
