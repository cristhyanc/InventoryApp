using Inventory.Domain.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// What the signed-in member may do, and the business they are doing it in (issue #521).
///
/// It describes exactly one member - the caller - and carries no identifier for them, for anyone
/// else, or for the business: other members and other businesses are deliberately outside this
/// contract, and a tenant key in a response is a key a later request could try to send back.
/// </summary>
/// <param name="Role">The role the caller holds in their own business.</param>
/// <param name="Capabilities">
/// Everything that role grants, in the agreed table's order, from
/// <see cref="RoleCapabilities"/> - the same definition issue #502's enforcement will read, so the
/// reported list and what the API actually permits cannot drift apart.
/// </param>
/// <param name="BusinessName">The business's operator-facing display name; never an identifier.</param>
/// <param name="TimeZoneId">
/// The business's IANA time zone (issue #499), so a client that has asked this question needs no
/// second call to render dates in the business's own calendar.
/// </param>
public sealed record CurrentAccess(
    BusinessRole Role,
    IReadOnlyList<Capability> Capabilities,
    string BusinessName,
    string TimeZoneId);
