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
