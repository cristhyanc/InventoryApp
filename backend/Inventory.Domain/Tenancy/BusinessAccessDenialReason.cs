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

    /// <summary>
    /// The single active membership points at a business whose <see cref="BusinessStatus"/> is not
    /// <see cref="BusinessStatus.Active"/> (issue #540): one awaiting a decision on its
    /// application, one whose application was refused, one that was offboarded, or one carrying a
    /// status this code does not declare. The reason stays one value on purpose - the denial is
    /// the same, and which situation it was is reported only to the caller's own identity, by
    /// <see cref="AccountStatePolicy"/>.
    /// </summary>
    BusinessInactive = 6,

    /// <summary>
    /// The single active membership on an active business carries a role this code does not
    /// declare (issue #521): a column an older schema never filled, a hand-written value, or a
    /// role a newer deployment wrote. There is no capability table for it, so access is denied
    /// rather than granted at whatever the lowest declared role happens to be.
    /// </summary>
    RoleUnrecognised = 7,
}
