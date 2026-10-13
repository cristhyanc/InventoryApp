using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// The dedicated narrow write path for the display-only member profile (issue #524).
///
/// It is deliberately not part of <see cref="IBusinessMembershipWriteStore"/>, and a refresh
/// deliberately does not run through <c>MembershipWriteGuard</c>. That guard exists for a change to
/// what a membership <em>means</em> - created, revoked, reactivated, re-roled - and serialises every
/// such change behind one <c>BEGIN IMMEDIATE</c> write lock so the one-active-membership and
/// last-Owner rules cannot be overtaken. A display name and an email address decide nothing, so
/// there is no rule to serialise, and taking the membership write lock on ordinary requests would
/// put a cosmetic write in the way of a real one on a single-writer SQLite file.
///
/// What keeps it narrow is this port's single method: it names the two display columns of one
/// identity's active membership and can touch nothing else - not the role, not
/// <c>IsActive</c>, not <c>StatusChangedAtUtc</c>, not another identity's row and not a revoked row.
/// </summary>
public interface IMemberProfileWriteStore
{
    /// <summary>
    /// Writes <paramref name="profile"/> onto the active membership of <paramref name="identity"/>,
    /// and only where it differs from what is already stored.
    /// </summary>
    /// <returns>
    /// The number of rows changed: <c>1</c> when the stored profile was out of date, and <c>0</c>
    /// when it was already current or the identity holds no active membership. Both zero answers
    /// are ordinary; neither is an error, and the row is not read to tell them apart.
    /// </returns>
    Task<int> RefreshActiveMembershipProfileAsync(
        ActorIdentity identity,
        MemberProfile profile,
        CancellationToken cancellationToken);
}
