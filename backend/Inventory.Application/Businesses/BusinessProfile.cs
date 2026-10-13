using Inventory.Domain.Tenancy;

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
/// <param name="Status">
/// The business's stored lifecycle status, exactly as stored - including a value no deployment
/// declares (issue #540).
///
/// It is here for the one caller that has to ask the question of a business it did not resolve from
/// a membership: a support session (issue #551) names a business the session row recorded, and that
/// business must still be <see cref="BusinessStatus.Active"/> for the session to be usable. The
/// ordinary membership path asks the same question earlier, in
/// <see cref="BusinessMembershipResolutionPolicy"/>, so nothing on that path reads this. It is
/// deliberately not reported to a member: no response built from this record carries it.
/// </param>
public sealed record BusinessProfile(string Name, string TimeZoneId, BusinessStatus Status);
