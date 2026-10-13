namespace InventoryApi.DTOs;

/// <summary>
/// What the signed-in caller may do, as <c>GET /api/me/access</c> returns it (issue #521, extended
/// by issue #551).
///
/// It describes the caller and nobody else: no member list, no other business, and no identifier
/// for the caller or their business. The role and the capabilities are spelled as their published
/// names - <c>Owner</c>, <c>Dashboard.ViewFinancials</c> - rather than numbers, because a number
/// is an internal representation a client would have to map and a renumbering would silently
/// change.
/// </summary>
/// <param name="Role">
/// The caller's role - <c>Owner</c>, <c>Manager</c> or <c>Operator</c> - or <see langword="null"/>
/// when they hold none because they are a platform administrator reading this business through a
/// support session (issue #551). Serialized as an explicit <c>null</c> rather than omitted, so the
/// field is a stable part of the shape.
/// </param>
/// <param name="Capabilities">
/// Every capability this request holds, as its <c>Area.Action</c> name, in the vocabulary's own
/// order - the role's, or a support session's fixed read-only set. A capability that is absent is
/// not granted; there is no wildcard.
/// </param>
/// <param name="BusinessName">The business's display name; never an identifier.</param>
/// <param name="TimeZoneId">
/// The business's IANA time zone (issue #499), the same value <c>GET /api/business/current</c>
/// reports, so a client that reads access does not need a second call to render dates.
/// </param>
/// <param name="SupportSession">
/// The support session this request is inside, or <see langword="null"/> for an ordinary member,
/// which is every request today's UI makes. Its presence is how a client knows the data on screen
/// belongs to somebody else and that nothing on it can be changed.
/// </param>
public sealed record MeAccessResponse(
    string? Role,
    IReadOnlyList<string> Capabilities,
    string BusinessName,
    string TimeZoneId,
    MeSupportSessionResponse? SupportSession);

/// <summary>
/// The support session a request is inside, as <c>GET /api/me/access</c> reports it (issue #551).
///
/// It deliberately carries no session id - that value is a credential, reported once by the start
/// endpoint and never again - and no business or actor identifier.
/// </summary>
/// <param name="BusinessName">Whose data is being looked at.</param>
/// <param name="ExpiresAtUtc">
/// When the session stops being usable. The server fixed it when the session started; no request can
/// choose or extend it.
/// </param>
/// <param name="ReadOnly">
/// Always <see langword="true"/>, stated rather than implied so a client never has to infer that a
/// support session means a read-only one.
/// </param>
public sealed record MeSupportSessionResponse(string BusinessName, DateTime ExpiresAtUtc, bool ReadOnly);

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
/// <c>NoMembership</c>, <c>ApplicationPending</c>, <c>ApplicationRejected</c>,
/// <c>MembershipRevoked</c>, <c>BusinessDeactivated</c> or <c>MembershipAmbiguous</c> - rather
/// than a number a client would have to map and a renumbering would silently change. The two
/// application states became reportable with the business lifecycle status of issue #540.
/// </param>
/// <param name="OnboardingEnabled">
/// Whether a person with no membership may apply for a business themselves. Always
/// <see langword="false"/> until the application endpoints of issue #541 add the flag.
/// </param>
/// <param name="RejectionReason">
/// Why an application was rejected; <see langword="null"/> for every other state, and also for a
/// rejection whose reason was not recorded. Serialized as an explicit <c>null</c> rather than
/// omitted, so the field is a stable part of the shape.
/// </param>
public sealed record MeAccountStateResponse(
    string State,
    bool OnboardingEnabled,
    string? RejectionReason);
