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
/// <param name="AttachmentStorage">
/// The business's purchase and expense attachment storage (issue #968), or an explicit <c>null</c>
/// while nothing is enforced - because then there is no allowance, nothing is counted and nothing
/// is refused. See <see cref="MeAttachmentStorageResponse"/>.
/// </param>
/// <param name="ConnectedMachines">
/// What the business's connected machines cost it (issue #978), or <c>null</c> while nothing is
/// enforced - which is the shipped state, where nothing about the business is read at all. Being over
/// the allowance is billed, never blocked; see <see cref="MeConnectedMachinesResponse"/>.
/// </param>
public sealed record MeEntitlementResponse(
    bool Enforced,
    string? Plan,
    string Source,
    DateTime? EndsAtUtc,
    MeAttachmentStorageResponse? AttachmentStorage,
    MeConnectedMachinesResponse? ConnectedMachines);

/// <summary>
/// How much of its attachment storage allowance a business is using, as
/// <c>GET /api/me/access</c> reports it (issue #968).
///
/// <para>It is reported here rather than from an endpoint of its own for the reason the
/// entitlement is: the warning a page shows at 80% and the explanation it gives for a refused
/// upload at 100% have to come from the same answer as the plan they are about. There is no
/// caller-supplied business id and no cache: the figures are the resolved business's own, read per
/// request.</para>
///
/// <para>It <strong>withholds nothing</strong>. At or over the allowance only new attachment
/// <em>uploads</em> are refused: viewing, downloading and deleting attachments, and recording
/// purchases and expenses without a document, keep working exactly as they do under it, and no
/// attachment is ever deleted automatically - including after a downgrade that leaves a business
/// above its new allowance.</para>
/// </summary>
/// <param name="UsedBytes">
/// The recorded sizes of the attachments the business's purchases and expenses refer to, plus the
/// uploads it currently has in progress. A stored file no record refers to is not charged to the
/// business, and an attachment whose size was never recorded counts as nothing rather than as a
/// guess; the reconciliation command reports both.
/// </param>
/// <param name="LimitBytes">
/// The effective plan's allowance, from <c>Inventory.Domain.Subscriptions.PlanCatalogue</c>. 1 GB
/// is 1,000,000,000 bytes.
/// </param>
/// <param name="WarningLevel">
/// The published <c>Inventory.Domain.Attachments.AttachmentStorageWarningLevel</c> name -
/// <c>Normal</c> under 80% of the allowance, <c>Warning</c> from 80%, <c>Full</c> at or over it -
/// rather than a number a client would have to map, and rather than a percentage each client would
/// round its own way.
/// </param>
public sealed record MeAttachmentStorageResponse(
    long UsedBytes, long LimitBytes, string WarningLevel);

/// <summary>
/// The business's connected machines, as <c>GET /api/me/access</c> reports them beside the plan
/// (issue #978, child of #965).
///
/// <para><strong>A connected machine is a machine on the business's Nayax account</strong>, linked in
/// this application or not - the decision of 29 October 2026, because Nayax imports sales for every
/// machine on the account either way. A manual machine has no provider id and is never counted.</para>
///
/// <para><strong>The count is recorded evidence, not a live read.</strong> It is the stored snapshot
/// of the latest complete fleet read for the business's <em>current</em> Nayax account, so this
/// response costs no Nayax call: a count taken earlier today is <c>Current</c>, an older one stays the
/// count and is reported <c>Stale</c> with its age, and a connection that cannot be read reports
/// <c>Unavailable</c> with a reason - never a zero.</para>
///
/// <para><strong>Being over the allowance is billed, never blocked.</strong> No client may read
/// <paramref name="MachinesOverAllowance"/> as a limit: linking a machine, importing sales, the
/// reports and the pick list are unaffected by it, and nothing removes a link or any history. What an
/// extra machine costs is the plan catalogue's, and the page that shows it is #970's work.</para>
/// </summary>
/// <param name="IncludedByPlan">
/// How many connected machines the effective plan includes - five on Pro, none on a plan with no
/// Nayax connection.
/// </param>
/// <param name="PurchasedAdditional">
/// How many extra connected machines the business has bought. It is <c>0</c> until the billing
/// integration of #969 records a purchase; an unrecorded purchase is none rather than a guess.
/// </param>
/// <param name="Allowance">
/// <paramref name="IncludedByPlan"/> plus <paramref name="PurchasedAdditional"/>: the number the
/// count is compared against.
/// </param>
/// <param name="Count">
/// The machines the current account was last counted at, or <c>null</c> when there is no count. Null,
/// never zero: a count nobody could take is not an empty fleet.
/// </param>
/// <param name="AsOfDateUtc">The UTC day that count was taken for, or <c>null</c> when there is none.</param>
/// <param name="AgeInDays">
/// How many UTC days old the count is: <c>0</c> while it is current, its real age while it is stale,
/// and <c>null</c> when there is no count.
/// </param>
/// <param name="State">
/// How well known the count is, as the published name of
/// <c>Inventory.Domain.ConnectedMachines.ConnectedMachineCountState</c>: <c>Current</c>, <c>Stale</c>
/// or <c>Unavailable</c>.
/// </param>
/// <param name="UnavailableReason">
/// Why there is no count, as the published name of
/// <c>Inventory.Domain.ConnectedMachines.ConnectedMachineCountUnavailableReason</c> -
/// <c>NotConnected</c>, <c>ConnectionNeedsAttention</c>, <c>NeverCounted</c> or
/// <c>AccountNotYetCounted</c> - and <c>None</c> when there is one.
/// </param>
/// <param name="MachinesOverAllowance">
/// How many counted machines are above the allowance, never negative, and <c>null</c> when there is no
/// count - because "none over" and "not known" are different answers.
/// </param>
public sealed record MeConnectedMachinesResponse(
    int IncludedByPlan,
    int PurchasedAdditional,
    int Allowance,
    int? Count,
    DateTime? AsOfDateUtc,
    int? AgeInDays,
    string State,
    string UnavailableReason,
    int? MachinesOverAllowance);

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
