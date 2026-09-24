namespace Inventory.Domain.Tenancy;

/// <summary>
/// Why an actor has no current business. Every value denies access: resolution fails closed,
/// so a caller that cannot produce a <see cref="BusinessId"/> must read no business data at all
/// (issue #64).
/// </summary>
public enum BusinessAccessDenialReason
{
    /// <summary>There is no authenticated actor on the current request.</summary>
    NotAuthenticated = 1,

    /// <summary>
    /// The request is authenticated but the token does not carry a usable <c>(tid, oid)</c> pair,
    /// so the actor cannot be identified. Never fall back to email or the directory tenant alone.
    /// </summary>
    UnidentifiableActor = 2,

    /// <summary>The actor is authenticated and identifiable but has no business membership.</summary>
    MembershipMissing = 3,

    /// <summary>Every membership the actor has was revoked.</summary>
    MembershipInactive = 4,

    /// <summary>
    /// The actor has more than one active membership - either duplicate rows or memberships in
    /// two businesses. There is no tenant switching in this rollout, so the current business is
    /// undecidable and access is denied rather than guessed.
    /// </summary>
    MembershipAmbiguous = 5,

    /// <summary>The single active membership points at a business that is no longer active.</summary>
    BusinessInactive = 6,
}
