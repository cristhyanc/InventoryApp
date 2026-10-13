using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// One invite as persistence holds it (issue #531): the stored status exactly as the row carries
/// it, and the expiry instant the effective status is derived from.
///
/// It is deliberately a different type from <see cref="MemberInvite"/> below. Deriving "expired"
/// belongs to <see cref="MemberInvitePolicy"/> and happens in one place, so a store cannot answer a
/// question about state and a use case cannot be handed a state somebody else decided - the row
/// reports what it holds, and the policy says what that means now.
/// </summary>
/// <param name="InviteId">The invite's own key within the owning business.</param>
/// <param name="Role">The role the invite grants, as stored - declared or not.</param>
/// <param name="Note">The Owner's own note, or <see langword="null"/>.</param>
/// <param name="StoredStatus">The status as stored: only ever written as Pending, Used or Cancelled.</param>
/// <param name="CreatedAtUtc">When the invite was created.</param>
/// <param name="ExpiresAtUtc">When it stops being usable, whatever its stored status says.</param>
/// <param name="UsedAtUtc">When it was redeemed, or <see langword="null"/>.</param>
/// <param name="CancelledAtUtc">When it was cancelled, or <see langword="null"/>.</param>
/// <param name="RedeemedByMembershipId">
/// The membership the redemption created, or <see langword="null"/> while the invite has not been
/// used. Nothing in this slice writes it; the redemption of #532 does.
/// </param>
public sealed record StoredMemberInvite(
    int InviteId,
    BusinessRole Role,
    string? Note,
    MemberInviteStatus StoredStatus,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? UsedAtUtc,
    DateTime? CancelledAtUtc,
    int? RedeemedByMembershipId);

/// <summary>
/// One invite as the Owner-facing API reports it (issue #531), with its state judged at the instant
/// it was read.
///
/// <para>What it deliberately does <em>not</em> carry is the token, in any form - neither the
/// plaintext, which exists for the creation response alone, nor its stored hash, which is the one
/// value that must stay in the database. It carries no business id either: the business is resolved
/// from the caller's own membership and no response has a use for its key. And it carries nothing
/// about the Owner who created it: within one business an invite list is read by the Owners who
/// create them, and publishing a creator's Entra <c>(tid, oid)</c> pair would put the value every
/// access decision is made from into a response body (see <see cref="BusinessMember"/>, which omits
/// it for the same reason).</para>
/// </summary>
/// <param name="InviteId">The invite's key within the caller's business, which is what the cancel endpoint takes.</param>
/// <param name="Role">
/// The role this invite grants, or <see langword="null"/> when the stored value is one this
/// deployment does not declare - which only a hand-written <c>UPDATE</c> or a newer deployment can
/// produce. It is reported as unknown rather than guessed at, for the reason
/// <see cref="BusinessMember.Role"/> is.
/// </param>
/// <param name="Note">The Owner's own note, or <see langword="null"/> when they wrote none.</param>
/// <param name="Status">
/// The state at the instant of the read, with an expired pending invite reported as
/// <see cref="MemberInviteStatus.Expired"/>, or <see langword="null"/> when the stored status is
/// undeclared - never <see cref="MemberInviteStatus.Pending"/>, which is the one state that says an
/// invite may still be used.
/// </param>
/// <param name="CreatedAtUtc">When the invite was created.</param>
/// <param name="ExpiresAtUtc">When it stops being usable.</param>
/// <param name="UsedAtUtc">When it was redeemed, or <see langword="null"/>.</param>
/// <param name="CancelledAtUtc">When it was cancelled, or <see langword="null"/>.</param>
/// <param name="RedeemedByMembershipId">
/// The membership the redemption created, or <see langword="null"/>. A membership key, within the
/// caller's own business, exactly as the members list already publishes.
/// </param>
public sealed record MemberInvite(
    int InviteId,
    BusinessRole? Role,
    string? Note,
    MemberInviteStatus? Status,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? UsedAtUtc,
    DateTime? CancelledAtUtc,
    int? RedeemedByMembershipId)
{
    /// <summary>
    /// Projects a stored row into the reported invite, judging its state at
    /// <paramref name="nowUtc"/> through <see cref="MemberInvitePolicy"/> - the one place that
    /// derivation happens.
    /// </summary>
    public static MemberInvite From(StoredMemberInvite stored, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return new MemberInvite(
            stored.InviteId,
            BusinessRoles.IsSupported(stored.Role) ? stored.Role : null,
            stored.Note,
            MemberInvitePolicy.EffectiveStatus(stored.StoredStatus, stored.ExpiresAtUtc, nowUtc),
            stored.CreatedAtUtc,
            stored.ExpiresAtUtc,
            stored.UsedAtUtc,
            stored.CancelledAtUtc,
            stored.RedeemedByMembershipId);
    }
}

/// <summary>
/// The row an invite creation asks persistence to write (issue #531). Every value is one the server
/// decided: the role and the note were validated by the Domain, the hash was computed from a token
/// the server generated, and the actor and the two instants came from the request's identity and
/// the application clock.
///
/// <para>It carries the token's <em>hash</em> and never the token, so the plaintext does not cross
/// the persistence port at all - a store, an adapter and a database that never receive it cannot
/// log, cache or persist it by mistake.</para>
/// </summary>
/// <param name="Role">The role the invite grants, already checked against the declared vocabulary.</param>
/// <param name="Note">The normalised note, or <see langword="null"/>.</param>
/// <param name="TokenHash">The token's SHA-256 digest from <see cref="MemberInviteToken.Hash"/>.</param>
/// <param name="CreatedBy">The Entra identity of the Owner who created it.</param>
/// <param name="CreatedAtUtc">When it was created, from the application clock.</param>
/// <param name="ExpiresAtUtc">When it stops being usable, from <see cref="MemberInvitePolicy.ExpiresAt"/>.</param>
public sealed record NewMemberInvite(
    BusinessRole Role,
    string? Note,
    string TokenHash,
    ActorIdentity CreatedBy,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

/// <summary>
/// What one completed invite creation produced (issue #531): the invite, and the token - once.
///
/// The token is in this result and nowhere else. It is not stored in plaintext, not logged, not
/// audited and not readable from any later request, so the Owner either copies the link out of this
/// one response or creates a new invite.
/// </summary>
/// <param name="Invite">The invite as it now stands.</param>
/// <param name="Token">The plaintext invite token, returned exactly once.</param>
public sealed record CreatedMemberInvite(MemberInvite Invite, string Token);
