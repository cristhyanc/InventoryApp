namespace Inventory.Domain.Tenancy;

/// <summary>
/// The deterministic rules of redeeming a member invite (issue #532, one slice of #504 under #328):
/// which invite may be used at all, and which sentence a caller who may not use one is refused with.
///
/// <para>Like <see cref="MemberInvitePolicy"/> it holds no clock, no database and no configuration -
/// every answer is a function of the values handed in. That is what lets the redemption's
/// authoritative in-transaction read and the conditional <c>UPDATE</c> that marks the invite used be
/// judged by one rule rather than by two that could drift apart.</para>
///
/// <para><strong>It decides nothing about the caller's eligibility.</strong> Whether a person may
/// gain an active membership is <see cref="MembershipEligibility"/>'s single rule, re-checked inside
/// the write transaction by <c>MembershipWriteGuard</c>. All this policy does with an
/// <see cref="AccountState"/> is choose the <em>wording</em> of a refusal that has already been
/// decided, which is why <see cref="IneligibleMessage"/> answers for every state including the ones
/// that may redeem.</para>
/// </summary>
public static class MemberInviteRedemptionPolicy
{
    /// <summary>
    /// The one response an unusable invite gets: an expired, used, cancelled or unknown token, an
    /// invite of a business that is not <see cref="BusinessStatus.Active"/>, and an invite granting
    /// a role this deployment does not declare, all alike.
    ///
    /// <para>One sentence for all of them is the whole point. An invite token is a secret somebody
    /// may be guessing at, and a response that told "no such invite" apart from "that invite was
    /// used" would confirm which tokens exist and, worse, that some business out there issued one.
    /// It therefore names no business, no role and no reason, and is deliberately phrased as
    /// possibilities rather than as the fact that applies.</para>
    /// </summary>
    public const string InvalidInviteMessage =
        "This invite link cannot be used. It may already have been used, it may have been withdrawn, "
            + "or it may have expired. Ask the person who invited you for a new link.";

    /// <summary>
    /// The refusal for a caller whose own membership belongs to a business that has been
    /// deactivated.
    ///
    /// It is its own sentence because the standing one would send them nowhere: releasing a
    /// membership is normally an Owner's doing, and a deactivated business has no reachable Owner,
    /// so the only person who can free this account is the platform administrator. It still names
    /// no business - not a name, not an id, not whether it still exists - because a refusal is not
    /// a reason to publish the business the caller belongs to.
    /// </summary>
    public const string ReleaseRequiredMessage =
        "Your account is still attached to a deactivated organisation, so it must first be released "
            + "by the platform administrator before you can join anywhere else.";

    /// <summary>
    /// Whether an invite may be redeemed at <paramref name="nowUtc"/>.
    /// </summary>
    /// <param name="storedStatus">The invite's status exactly as persistence read it.</param>
    /// <param name="expiresAtUtc">The invite's own expiry instant.</param>
    /// <param name="businessStatus">
    /// The inviting business's lifecycle status, exactly as persistence read it. Written as "is
    /// <see cref="BusinessStatus.Active"/>" rather than as a list of the statuses that are not, so a
    /// status a newer deployment wrote refuses the redemption instead of admitting somebody to a
    /// business no endpoint will serve.
    /// </param>
    /// <param name="role">
    /// The role the invite grants, as stored. An undeclared value is refused rather than honoured:
    /// <see cref="BusinessMembershipResolutionPolicy"/> denies a membership carrying one, so
    /// redeeming it would let a person in and lock them out in the same operation.
    /// </param>
    /// <param name="nowUtc">The instant to judge it at.</param>
    public static bool IsRedeemable(
        MemberInviteStatus storedStatus,
        DateTime expiresAtUtc,
        BusinessStatus businessStatus,
        BusinessRole role,
        DateTime nowUtc) =>
        MemberInvitePolicy.IsLive(storedStatus, expiresAtUtc, nowUtc)
        && businessStatus == BusinessStatus.Active
        && BusinessRoles.IsSupported(role);

    /// <summary>
    /// The sentence a caller who may not gain an active membership is refused with, chosen by the
    /// state their own account is in.
    ///
    /// <see cref="AccountState.BusinessDeactivated"/> gets <see cref="ReleaseRequiredMessage"/>;
    /// every other state - including the three that may redeem, which is why this function is total
    /// rather than partial - gets <see cref="MembershipEligibility.AlreadyAMemberMessage"/>. The
    /// state picks the wording and nothing else: a refusal happens only because
    /// <see cref="MembershipEligibility"/> refused.
    /// </summary>
    public static string IneligibleMessage(AccountState state) =>
        state == AccountState.BusinessDeactivated
            ? ReleaseRequiredMessage
            : MembershipEligibility.AlreadyAMemberMessage;
}
