using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// One membership of one business, as the Super Admin sees it while that business is deactivated
/// (issue #545): the key the release names, a name to recognise the row by, the role, and whether
/// the membership is current.
///
/// <para><strong>What it deliberately does not carry.</strong> The member's email address and their
/// Entra <c>(tid, oid)</c> pair. The pair is the identity every access decision is made from, and
/// publishing it would put the one value that matters into a response a client could try to send
/// back; the address is personal contact detail the platform administrator has no decision to make
/// about. The display name is here because the one thing this list is for is choosing which
/// membership to release, and a column of object ids cannot be chosen from - it is the same
/// display-only value an Owner's own members list reports (issue #524), and it is never an
/// identifier.</para>
/// </summary>
/// <param name="MembershipId">
/// The membership's own key, which the release endpoint names. It is paired with the business in
/// every statement, so another business's key is simply not found.
/// </param>
/// <param name="DisplayName">
/// The member's display name as their own validated sign-in token last reported it, or
/// <see langword="null"/> when no token has reported one. Never a stand-in value.
/// </param>
/// <param name="StoredRole">
/// The role exactly as persistence read it, including a value this code does not declare. It is not
/// normalised here because two different readers need it unchanged: the published projection reports
/// an undeclared value as unknown rather than guessing, and
/// <see cref="BusinessOwnerRetention"/> has to judge "is this an Owner?" on the stored value, where
/// a value nobody declared is deliberately not one.
/// </param>
/// <param name="IsActive">Whether the membership is current.</param>
public sealed record PlatformBusinessMember(
    int MembershipId,
    string? DisplayName,
    BusinessRole StoredRole,
    bool IsActive);

/// <summary>
/// The facts one lifecycle transition is measured against (issue #545), read authoritatively inside
/// the write transaction the transition is made in.
///
/// <para>It carries every membership of the business, active and inactive alike, because two rules
/// read them: a reactivation requires that the business still has an active Owner
/// (<see cref="BusinessOwnerRetention"/>), and a release has to find the membership it names within
/// the business that was named beside it.</para>
/// </summary>
/// <param name="BusinessId">The business the transition is about.</param>
/// <param name="Name">Its display name, so a transition can answer without a second read.</param>
/// <param name="Status">Its stored status, exactly as stored, including an undeclared value.</param>
/// <param name="Members">
/// Every membership recorded for it, in a deterministic order the caller does not have to re-sort.
/// </param>
public sealed record PlatformBusinessLifecycleTarget(
    int BusinessId,
    string Name,
    BusinessStatus Status,
    IReadOnlyList<PlatformBusinessMember> Members);

/// <summary>
/// A recorded deactivation, as the Super Admin is answered (issue #545).
/// </summary>
/// <param name="BusinessId">The business that was switched off.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Status">The status it now holds - always <see cref="BusinessStatus.Deactivated"/>.</param>
/// <param name="InvitesCancelled">
/// How many of its invites were still live and have now been cancelled permanently. Reported because
/// it is the one part of a deactivation that is not visible from the business row afterwards, and
/// because "no outstanding link survived this" is the fact an operator needs to be able to check.
/// </param>
public sealed record PlatformBusinessDeactivation(
    int BusinessId,
    string Name,
    BusinessStatus Status,
    int InvitesCancelled);

/// <summary>
/// A recorded reactivation, as the Super Admin is answered (issue #545). It carries no invite count
/// and no membership: a reactivation restores the <em>business</em>, and nothing else about it
/// changes.
/// </summary>
/// <param name="BusinessId">The business that was switched back on.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Status">The status it now holds - always <see cref="BusinessStatus.Active"/>.</param>
public sealed record PlatformBusinessReactivation(int BusinessId, string Name, BusinessStatus Status);
