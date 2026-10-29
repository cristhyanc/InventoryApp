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
/// <param name="HistoricalGstClassificationVisible">
/// Whether the application currently <em>offers</em> Historical GST Classification to anybody but a
/// Super Admin (issue #873) - one shared application-wide setting, reported here because this is the
/// read the application shell builds its menu from. It is deliberately not a capability and implies
/// none: <paramref name="Capabilities"/> is identical in both states, <see langword="false"/> hides
/// the entry from the members it was switched off for, <see langword="true"/> restores it only to
/// members whose capabilities already allowed it, and the classification endpoints keep authorizing
/// every request by <c>Data.Repair</c> either way.
/// </param>
/// <param name="Entitlement">
/// What the business has <em>bought</em> (issue #966): the effective plan, or - in the shipped state -
/// that nothing is enforced at all. Always present, never omitted, so a client never has to tell
/// "the API does not report this" from "nothing is enforced"; see
/// <see cref="MeEntitlementResponse"/>. It is deliberately not a capability and implies none.
/// </param>
public sealed record MeAccessResponse(
    string? Role,
    IReadOnlyList<string> Capabilities,
    string BusinessName,
    string TimeZoneId,
    MeSupportSessionResponse? SupportSession,
    bool HistoricalGstClassificationVisible,
    MeEntitlementResponse Entitlement);

/// <summary>
/// The business's plan entitlement, as <c>GET /api/me/access</c> reports it (issue #966).
///
/// <para>There is deliberately no separate read endpoint and no caller-supplied business id: this
/// is computed per request, with no server cache, for the business the request already resolved -
/// which inside a support session is the business being viewed, so a supporter sees that business's
/// own plan. A role preview never changes it.</para>
///
/// <para>It carries no price, no storage figure and no machine allowance. Those are the plan
/// catalogue's (<c>Inventory.Domain.Subscriptions.PlanCatalogue</c>), and the page that shows them
/// is issue #970's work; repeating them in every access read would be a second place a price could
/// be wrong.</para>
/// </summary>
/// <param name="Enforced">
/// Whether plan entitlement applies at all. <see langword="false"/> is the shipped state and today's
/// behaviour - every business unrestricted - and a client that reads it offers everything, exactly as
/// it does now. It is <see langword="false"/> both while the rollout setting is off and while the
/// grandfathering step has not completed, which is one answer on purpose: in both states nothing is
/// withheld from anybody.
/// </param>
/// <param name="Plan">
/// The effective plan's published name - <c>Free</c>, <c>Starter</c> or <c>Pro</c> - while
/// <paramref name="Enforced"/> is <see langword="true"/>, and an explicit <c>null</c> while it is
/// not. Null rather than <c>Pro</c>, because "unrestricted" is not a plan anybody is on; null rather
/// than <c>Free</c>, because reporting Free would lock a business nothing is enforcing anything for.
/// A name rather than a number, for the reason the role and the capabilities are names.
/// </param>
/// <param name="Source">
/// Where that plan came from, as the published name of
/// <c>Inventory.Domain.Subscriptions.EntitlementSource</c>: <c>EnforcementDisabled</c>,
/// <c>GrandfatheringNotCompleted</c>, <c>ManualGrant</c>, <c>BillingSubscription</c> or
/// <c>NoActiveRecord</c>. It is reported so a client (and a support request) can tell a
/// complimentary or grandfathered plan from a paid one, and an unrestricted deployment from a
/// business that has nothing active.
/// </param>
/// <param name="EndsAtUtc">
/// When the record that decided the plan stops entitling it, <strong>exclusive</strong>, or
/// <c>null</c> when it has no recorded end - which is what a grandfathered grant has until an owner
/// sets one. It is that one record's end, never a combination of both records' dates.
/// </param>
public sealed record MeEntitlementResponse(
    bool Enforced,
    string? Plan,
    string Source,
    DateTime? EndsAtUtc);

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
