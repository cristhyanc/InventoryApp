using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// The invite a presented token identifies, and the two facts deciding whether it may be redeemed
/// (issue #532).
///
/// <para>It carries the owning <see cref="BusinessId"/> because the redemption's every later
/// statement needs one and this row is the only honest source of it: the caller has no membership,
/// so nothing resolved a business for their request, and a business id a request supplied would be
/// a request choosing an owner (AGENTS.md § Tenant ownership). It carries no business name, no
/// note and no token - nothing that could reach a response or a log for an invite that turns out
/// to be unusable.</para>
/// </summary>
/// <param name="InviteId">The invite's own key within <paramref name="BusinessId"/>.</param>
/// <param name="BusinessId">The business the invite was created in, from the invite row itself.</param>
/// <param name="Role">The role it grants, exactly as stored - declared or not.</param>
/// <param name="StoredStatus">Its status exactly as stored, so the policy derives expiry itself.</param>
/// <param name="ExpiresAtUtc">When it stops being usable.</param>
/// <param name="BusinessStatus">
/// The inviting business's lifecycle status, exactly as stored, read in the same statement as the
/// invite so the two cannot be judged a moment apart.
/// </param>
public sealed record RedeemableMemberInvite(
    int InviteId,
    BusinessId BusinessId,
    BusinessRole Role,
    MemberInviteStatus StoredStatus,
    DateTime ExpiresAtUtc,
    BusinessStatus BusinessStatus);

/// <summary>
/// What the inviting business already records about the redeeming identity (issue #532): the
/// membership row it holds for them, if any, and whether that row is current.
/// </summary>
/// <param name="MembershipId">The membership's own persisted key.</param>
/// <param name="IsActive">Whether the approval is current.</param>
public readonly record struct RedeemingMemberMembership(int MembershipId, bool IsActive);

/// <summary>
/// The narrow persistence port the invite redemption runs on (issue #532). It touches three things
/// and nothing else: invites, memberships, and the inviting business's status.
///
/// <para><strong>Why it is a port of its own.</strong> The redemption is the one path into this
/// application reached by a caller with no business at all, so it cannot use
/// <see cref="IMemberInviteStore"/> or <see cref="IBusinessMemberStore"/>, whose every method takes
/// a business id tenancy resolution produced. Here the business comes from the invite row the
/// presented token identifies, and keeping that in one small port means the whole of what a
/// business-less caller can reach is one reviewable surface rather than extra methods spread over
/// the Owner-facing stores.</para>
///
/// <para><strong>Every method but the lookup takes the business id explicitly</strong>, and it is
/// only ever the one <see cref="RedeemableMemberInvite.BusinessId"/> reported - never a value from
/// the request. The lookup takes only the token's hash, because that is all the caller presents.</para>
///
/// <para><strong>Every call is made inside issue #522's shared write transaction.</strong> The
/// implementation writes through the request's own <c>AppDbContext</c> and saves its own changes, so
/// <c>MembershipWriteGuard</c>'s re-checks see them and a refusal rolls all of them back together -
/// the membership and the invite alike.</para>
/// </summary>
public interface IMemberInviteRedemptionStore
{
    /// <summary>
    /// The invite whose stored hash is <paramref name="tokenHash"/>, together with its business's
    /// status, or <see langword="null"/> when no invite answers to it.
    ///
    /// <para><see langword="null"/> is deliberately also the answer for a token that two invites
    /// somehow answer to, and for an invite whose business row is missing or whose owner column is
    /// not a usable key: an ambiguous or unanchored invite is refused rather than resolved by
    /// picking one, exactly as an ambiguous membership is. The caller reports every one of those as
    /// the same generic refusal.</para>
    /// </summary>
    Task<RedeemableMemberInvite?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// What <paramref name="businessId"/> already records for <paramref name="identity"/>, or
    /// <see langword="null"/> when it holds no row for them at all. It is how a redemption tells a
    /// first-time member from somebody whose membership that business revoked.
    /// </summary>
    Task<RedeemingMemberMembership?> FindMembershipAsync(
        BusinessId businessId,
        ActorIdentity identity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates one active membership for the redeeming identity in the invite's business, with the
    /// invite's role, and returns the key the database assigned.
    ///
    /// <para>The profile is the display-only name and email address the caller's own validated
    /// token reported (issue #524). Either half may be absent, and an absent one is stored as
    /// nothing rather than as a placeholder; neither value is read by any access decision.</para>
    ///
    /// <para>The instant is recorded as both the creation and the state-change instant, so the row
    /// never describes a state it did not reach then (issue #522).</para>
    /// </summary>
    Task<int> CreateActiveMembershipAsync(
        BusinessId businessId,
        ActorIdentity identity,
        MemberProfile profile,
        BusinessRole role,
        DateTime createdAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Brings the business's existing revoked row for this person back, with the invite's role, as
    /// one statement.
    ///
    /// It is deliberately a reactivation rather than a second row: the per-(actor, business) unique
    /// index allows only one, and the revoked row is the record that this person was once approved
    /// here. The invite's role replaces whatever the revoked row held, because the invite is the
    /// new decision about what they may do.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call reactivated it; <see langword="false"/> when it
    /// matched no revoked row of that business, in which case nothing was written.
    /// </returns>
    Task<bool> ReactivateMembershipAsync(
        BusinessId businessId,
        int membershipId,
        BusinessRole role,
        MemberProfile profile,
        DateTime reactivatedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks the invite used by <paramref name="membershipId"/>, but only while it is still live at
    /// <paramref name="usedAtUtc"/> - pending, not already used or cancelled, and not past its
    /// expiry - and only within <paramref name="businessId"/>.
    ///
    /// The liveness condition travels in the <c>WHERE</c> clause, so the decision and the write are
    /// one operation in the database rather than a read this caller then trusts. That is what makes
    /// "one invite, one membership" true under concurrency: a second redemption of the same invite
    /// matches nothing here and is refused, with its own membership write rolled back.
    ///
    /// <para><paramref name="usedAtUtc"/> must be read immediately before this call, inside the
    /// transaction (issue #608). It is both the expiry the condition is judged against and the
    /// instant the row records, so an instant carried over from before a lock wait would compare
    /// the invite to a time that has already passed - letting a link that expired during the wait
    /// be consumed - and would then record the redemption as having happened then.</para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call marked it used; <see langword="false"/> when the
    /// invite was no longer live, in which case nothing was written.
    /// </returns>
    Task<bool> MarkUsedAsync(
        BusinessId businessId,
        int inviteId,
        int membershipId,
        DateTime usedAtUtc,
        CancellationToken cancellationToken);
}
