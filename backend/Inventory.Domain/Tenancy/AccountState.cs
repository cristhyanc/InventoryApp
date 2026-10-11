namespace Inventory.Domain.Tenancy;

/// <summary>
/// Which state a signed-in person's account is in (issue #523), as
/// <c>GET /api/me/account-state</c> reports it so the frontend can show the right screen instead
/// of a bare <c>403</c>.
///
/// This vocabulary describes; it grants nothing. A state is reported to the caller about their own
/// identity only, and no value of it changes what any endpoint permits:
/// <see cref="BusinessMembershipResolutionPolicy"/> remains the only rule that decides access, and
/// it is unchanged. The names are the published contract - the API returns them as written here -
/// so a value must not be renamed to suit an internal refactor.
///
/// Every state names the caller's own situation and never another business: "deactivated" and
/// "rejected" say what happened to the account, not which business it was.
/// </summary>
public enum AccountState
{
    /// <summary>One active membership in an active business: the account works.</summary>
    Member = 1,

    /// <summary>No membership record at all for this identity.</summary>
    NoMembership = 2,

    /// <summary>
    /// One active membership in a business that has applied and is awaiting approval. It needs
    /// the <c>Pending</c> business status issue #507 adds, so nothing reports it yet.
    /// </summary>
    ApplicationPending = 3,

    /// <summary>
    /// The most recent inactive membership belongs to a business whose application was rejected.
    /// It needs the <c>Rejected</c> business status issue #507 adds, which also supplies the
    /// rejection reason that accompanies it, so nothing reports it yet.
    /// </summary>
    ApplicationRejected = 4,

    /// <summary>The membership was revoked; the account held one and no longer does.</summary>
    MembershipRevoked = 5,

    /// <summary>
    /// The membership is current but its business has been deactivated, so no endpoint will serve
    /// the account's data until the business is active again.
    /// </summary>
    BusinessDeactivated = 6,

    /// <summary>
    /// More than one active membership, which is undecidable rather than a working account.
    /// Only legacy data can be in this state: since issue #522 the database refuses to create it,
    /// and its migration refuses to run while any identity holds two active memberships.
    /// </summary>
    MembershipAmbiguous = 7,
}
