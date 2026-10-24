using Inventory.Domain.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// What the signed-in caller may do, and the business they are doing it in (issue #521, extended by
/// issue #551).
///
/// It describes exactly one caller - the one asking - and carries no identifier for them, for
/// anyone else, or for the business: other members and other businesses are deliberately outside
/// this contract, and a tenant key in a response is a key a later request could try to send back.
/// </summary>
/// <param name="Role">
/// The role the caller holds in the business, or <see langword="null"/> when they hold none because
/// they are reading it through a support session (issue #551). A platform administrator is a member
/// of no business, so reporting a role for them would describe access nobody granted - and the
/// capabilities below are not that role's either.
/// </param>
/// <param name="Capabilities">
/// Everything this request may do, in the agreed table's order, from
/// <see cref="CurrentCapabilities"/> - whose two sources are <see cref="RoleCapabilities"/> for a
/// member and <see cref="SupportSessionAccessPolicy"/> for a support session. It is the same
/// definition the capability policies enforce, so the reported list and what the API actually
/// permits cannot drift apart.
/// </param>
/// <param name="BusinessName">The business's operator-facing display name; never an identifier.</param>
/// <param name="TimeZoneId">
/// The business's IANA time zone (issue #499), so a client that has asked this question needs no
/// second call to render dates in the business's own calendar.
/// </param>
/// <param name="SupportSession">
/// The support session this request is inside, or <see langword="null"/> for an ordinary member -
/// which is every request today's UI makes. It is what lets a client show, unmistakably, that the
/// data on screen belongs to somebody else and that nothing on it can be changed.
/// </param>
/// <param name="HistoricalGstClassificationVisible">
/// Whether the application currently <em>offers</em> Historical GST Classification to anybody but a
/// Super Admin (issue #873), from
/// <see cref="Inventory.Application.PlatformOptions.GetOptionVisibility"/>. It is a shared
/// application-wide setting rather than anything about this caller, and it is reported here because
/// this is the one answer the shell builds its menu from - an option the application has switched
/// off has to stop being offered in the same read that says what the member may do.
///
/// <para><strong>It is not a capability and never implies one.</strong>
/// <see cref="Capabilities"/> is unchanged by it in both states: <see langword="false"/> hides an
/// entry from the people it was switched off for and revokes nothing, and <see langword="true"/>
/// restores it only to a member whose capabilities already allowed it. The classification
/// endpoints keep authorizing every request by <c>Data.Repair</c> whatever this says.</para>
/// </param>
public sealed record CurrentAccess(
    BusinessRole? Role,
    IReadOnlyList<Capability> Capabilities,
    string BusinessName,
    string TimeZoneId,
    CurrentSupportSession? SupportSession,
    bool HistoricalGstClassificationVisible);

/// <summary>
/// The support session a request is inside, as the caller's own access report describes it
/// (issue #551).
/// </summary>
/// <param name="BusinessName">
/// Whose data is being looked at. It is the same name as <see cref="CurrentAccess.BusinessName"/>
/// and is repeated here on purpose: a client reading this object alone must not have to know that
/// the two are the same, and a banner that said "you are viewing" with no name would be a banner
/// that could be shown by mistake.
/// </param>
/// <param name="ExpiresAtUtc">
/// When the session stops being usable. Fixed by the server when the session started; no request
/// can choose or extend it.
/// </param>
/// <param name="ReadOnly">
/// Always <see langword="true"/>. It is a stated fact rather than an implied one so a client never
/// has to infer "support session" means "read-only", and so a future session that were not
/// read-only could not arrive unannounced.
/// </param>
public sealed record CurrentSupportSession(string BusinessName, DateTime ExpiresAtUtc, bool ReadOnly);
