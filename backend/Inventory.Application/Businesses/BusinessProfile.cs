namespace Inventory.Application.Businesses;

/// <summary>
/// The operator-facing facts about the current business that are not themselves business data
/// (issue #499): what it is called, and the IANA time zone its calendar days are kept in.
///
/// It deliberately carries no business identifier. Nothing outside tenancy resolution needs one,
/// and a tenant key in a response body is a key a later request could try to send back.
/// </summary>
/// <param name="Name">The operator-facing display name; never an identifier.</param>
/// <param name="TimeZoneId">
/// The IANA time-zone id business dates are derived in, for example <c>Australia/Sydney</c>.
/// </param>
public sealed record BusinessProfile(string Name, string TimeZoneId);
