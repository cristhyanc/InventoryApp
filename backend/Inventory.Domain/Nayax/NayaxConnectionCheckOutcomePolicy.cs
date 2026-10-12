namespace Inventory.Domain.Nayax;

/// <summary>
/// What one completed run of the connection checks concludes about a credential pair (issue #537,
/// a slice of #506 under #328).
///
/// It is deliberately not <see cref="NayaxConnectionStatus"/> itself. A run can conclude *nothing*
/// - Nayax never answered the sign-in - and that is a fourth answer rather than a fourth status:
/// the stored status stays exactly as it was, because nothing was learned about the credentials.
/// Collapsing it into a status would force this rule to invent one.
/// </summary>
public enum NayaxConnectionCheckOutcome
{
    /// <summary>
    /// Every check that ran passed. A machine-scoped line that was never checked does not hold this
    /// back: the business has no machines yet, which is not a fault of the credentials.
    /// </summary>
    Ready = 1,

    /// <summary>
    /// The token signed in, and at least one other check that ran failed. The credentials are real
    /// and usable; one or more permissions are missing.
    /// </summary>
    PendingPermissions = 2,

    /// <summary>
    /// Nayax answered the sign-in with a <c>401</c>: the token is invalid or has expired. This is
    /// the one answer that is a verdict on the credentials themselves.
    /// </summary>
    TokenRejected = 3,

    /// <summary>
    /// Nothing was concluded. The sign-in did not pass and did not produce a verdict on the token
    /// either: Nayax was unavailable, answered something nobody has interpreted, refused the
    /// sign-in endpoint for lack of a permission, or the check never ran at all.
    /// </summary>
    Inconclusive = 4,
}

/// <summary>
/// The deterministic rule that turns one completed check run into what it concluded (issue #537).
///
/// <para>It lives in the Domain beside <see cref="NayaxConnectionCheckPolicy"/> and
/// <see cref="NayaxConnectionStatusGate"/>, and for the same reason: "this token works", "this token
/// is missing a permission" and "this token is dead" need completely different actions from an
/// operator, and the save endpoint, the re-test endpoint and the wizard that explains the result
/// must all read one table. A second opinion written inline in a use case is how a connection whose
/// token has expired keeps reporting itself as ready.</para>
///
/// <para>It takes the three facts it needs rather than the Application layer's check report, so the
/// Domain keeps no dependency on it. Which line is the sign-in, and whether any other line failed,
/// are projections of that report; what each answer <em>means</em> is the rule here.</para>
///
/// <para><strong>Only a <c>401</c> is a verdict on the token.</strong> That is Nayax's own
/// documented meaning - "the token is invalid or has expired" - while a <c>403</c> is documented as
/// "insufficient permissions to perform this action", a verdict on one endpoint's scope
/// (https://devzone.nayax.com/docs/manage-data-operations/lynx-api/app-tokens). Every other way a
/// sign-in can fail to pass therefore concludes <see cref="NayaxConnectionCheckOutcome.Inconclusive"/>:
/// an unreachable Nayax, an answer this application does not interpret, a sign-in endpoint refused
/// for lack of a permission, and a check that never ran all leave the credentials unjudged, and
/// concluding anything from them would either break a working connection or bless a broken one.
/// </para>
/// </summary>
public static class NayaxConnectionCheckOutcomePolicy
{
    /// <summary>
    /// What a run concluded.
    /// </summary>
    /// <param name="signIn">How the <see cref="NayaxConnectionCheck.SignIn"/> line turned out.</param>
    /// <param name="signInFailure">
    /// Why the sign-in failed, or <see langword="null"/> when it did not fail.
    /// </param>
    /// <param name="anyOtherCheckFailed">
    /// Whether any check other than the sign-in failed. A line that was never checked is not a
    /// failure.
    /// </param>
    public static NayaxConnectionCheckOutcome Classify(
        NayaxConnectionCheckStatus signIn,
        NayaxConnectionCheckFailure? signInFailure,
        bool anyOtherCheckFailed)
    {
        if (signIn == NayaxConnectionCheckStatus.Passed)
        {
            return anyOtherCheckFailed
                ? NayaxConnectionCheckOutcome.PendingPermissions
                : NayaxConnectionCheckOutcome.Ready;
        }

        // An allow-list of the one failure that judges the token, so a cause this rule has never
        // heard of concludes nothing rather than being read as a verdict.
        return signIn == NayaxConnectionCheckStatus.Failed
            && signInFailure == NayaxConnectionCheckFailure.InvalidOrExpiredToken
                ? NayaxConnectionCheckOutcome.TokenRejected
                : NayaxConnectionCheckOutcome.Inconclusive;
    }

    /// <summary>
    /// The status a run's outcome writes to the stored connection, or <see langword="null"/> when it
    /// writes none.
    ///
    /// <see cref="NayaxConnectionCheckOutcome.Inconclusive"/> is the <see langword="null"/>: the
    /// connection keeps the status it had, including its last-tested instant, because a run that
    /// learned nothing must not look like a run that did.
    /// </summary>
    public static NayaxConnectionStatus? ConcludedStatus(NayaxConnectionCheckOutcome outcome) =>
        outcome switch
        {
            NayaxConnectionCheckOutcome.Ready => NayaxConnectionStatus.Ready,
            NayaxConnectionCheckOutcome.PendingPermissions => NayaxConnectionStatus.PendingPermissions,
            NayaxConnectionCheckOutcome.TokenRejected => NayaxConnectionStatus.NeedsAttention,
            _ => null,
        };

    /// <summary>
    /// Whether a run's outcome may be accepted as a business's connection.
    ///
    /// Only a run whose sign-in passed may: a token that did not sign in has never been shown to
    /// work at all, so storing it would replace whatever the business has with something known or
    /// suspected not to authenticate. A failing run is never saved "so the status can record it" -
    /// the test result the Owner is looking at already records it.
    /// </summary>
    public static bool MayBeSaved(NayaxConnectionCheckOutcome outcome) =>
        outcome is NayaxConnectionCheckOutcome.Ready or NayaxConnectionCheckOutcome.PendingPermissions;
}
