namespace Inventory.Domain.Tenancy;

/// <summary>
/// Where a business is in its lifecycle (issue #540, one slice of #507 under #328), and the single
/// source of truth for whether its data may be reached at all.
///
/// It replaces the former <c>Business.IsActive</c> flag, which could only say "yes" or "no" and so
/// could not tell a business that has applied and is waiting from one that was refused or one that
/// was offboarded. Those are three different screens for the person signing in and three different
/// situations for an operator, and a boolean had to collapse them into one.
///
/// <para><strong>Only <see cref="Active"/> grants anything.</strong>
/// <see cref="BusinessMembershipResolutionPolicy"/> resolves a business only while its status is
/// <see cref="Active"/>, and every other value - including a value a newer deployment wrote that
/// this code does not declare - denies access with the existing
/// <see cref="BusinessAccessDenialReason.BusinessInactive"/> reason. The deny direction is
/// therefore the default rather than a case somebody has to remember to add, which is why no
/// status is <c>0</c>: <c>0</c> is what an unfilled integer column and a default-constructed enum
/// both hold, and it is deliberately not a declared status.</para>
///
/// <para>The values are spaced, like <see cref="BusinessRole"/>'s, so a further lifecycle state
/// can be declared between two of these without renumbering a value already persisted in a
/// business row.</para>
/// </summary>
public enum BusinessStatus
{
    /// <summary>
    /// The business has applied and is awaiting a decision. Its members hold real memberships -
    /// which is what makes the application theirs - but no endpoint serves its data, and
    /// <c>GET /api/me/account-state</c> reports <see cref="AccountState.ApplicationPending"/> to
    /// them instead of a bare refusal.
    /// </summary>
    Pending = 10,

    /// <summary>
    /// Approved and trading: the one status that resolves. It is the status the migration gave
    /// every business that was active before this slice, and the status <c>bootstrap-business</c>
    /// creates.
    /// </summary>
    Active = 20,

    /// <summary>
    /// The application was refused. The decision instant and the operator's reason are stored on
    /// the business, and the reason is the one piece of it reported back - to the applicant's own
    /// identity alone, through <see cref="AccountState.ApplicationRejected"/>.
    /// </summary>
    Rejected = 30,

    /// <summary>
    /// Offboarded after having traded. Its records are kept rather than deleted and simply become
    /// unreachable, so an offboarded business fails closed instead of being dropped.
    /// </summary>
    Deactivated = 40,
}
