namespace InventoryApi.DTOs;

/// <summary>
/// What the signed-in member may do, as <c>GET /api/me/access</c> returns it (issue #521).
///
/// It describes the caller and nobody else: no member list, no other business, and no identifier
/// for the caller or their business. The role and the capabilities are spelled as their published
/// names - <c>Owner</c>, <c>Dashboard.ViewFinancials</c> - rather than numbers, because a number
/// is an internal representation a client would have to map and a renumbering would silently
/// change.
/// </summary>
/// <param name="Role">The caller's role: <c>Owner</c>, <c>Manager</c> or <c>Operator</c>.</param>
/// <param name="Capabilities">
/// Every capability that role grants, as its <c>Area.Action</c> name, in the vocabulary's own
/// order. A capability that is absent is not granted; there is no wildcard.
/// </param>
/// <param name="BusinessName">The business's display name; never an identifier.</param>
/// <param name="TimeZoneId">
/// The business's IANA time zone (issue #499), the same value <c>GET /api/business/current</c>
/// reports, so a client that reads access does not need a second call to render dates.
/// </param>
public sealed record MeAccessResponse(
    string Role,
    IReadOnlyList<string> Capabilities,
    string BusinessName,
    string TimeZoneId);

/// <summary>
/// Which state the signed-in person's account is in, as <c>GET /api/me/account-state</c> returns
/// it (issue #523), so the frontend can show the right screen instead of a bare 403.
///
/// These three fields are the whole contract. The response deliberately carries nothing about a
/// business - not a name, not an id, not whether one exists - because the caller may be somebody
/// no business has approved, and the state alone is what decides the screen.
/// </summary>
/// <param name="State">
/// The published <c>Inventory.Domain.Tenancy.AccountState</c> name - <c>Member</c>,
/// <c>NoMembership</c>, <c>MembershipRevoked</c>, <c>BusinessDeactivated</c>,
/// <c>MembershipAmbiguous</c>, and once issue #507 adds the business status
/// <c>ApplicationPending</c> or <c>ApplicationRejected</c> - rather than a number a client would
/// have to map and a renumbering would silently change.
/// </param>
/// <param name="OnboardingEnabled">
/// Whether a person with no membership may apply for a business themselves. Always
/// <see langword="false"/> until issue #507 adds the flag.
/// </param>
/// <param name="RejectionReason">
/// Why an application was rejected; <see langword="null"/> for every other state, and therefore
/// always <see langword="null"/> until issue #507. Serialized as an explicit <c>null</c> rather
/// than omitted, so the field is a stable part of the shape.
/// </param>
public sealed record MeAccountStateResponse(
    string State,
    bool OnboardingEnabled,
    string? RejectionReason);
