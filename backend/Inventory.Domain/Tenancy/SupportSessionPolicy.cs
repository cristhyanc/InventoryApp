using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Tenancy;

/// <summary>
/// The deterministic rules of a support session (issue #550, a slice of #512 under #328): how long
/// one lasts, which business one may be started for, whether a stored one is still active at a given
/// instant, and what a submitted reason may be.
///
/// <para>No clock, no database and no configuration - every answer is a function of the values handed
/// in, which is what lets the same rule decide a start, the "do I have one?" read, an end, and
/// (since #551) whether a request may join a session at all.</para>
///
/// <para>It decides nothing about <em>who</em> may ask. That is the configured platform administrator
/// and nothing else, enforced at the HTTP boundary by <c>PlatformAdminPolicy</c> (issue #336); an
/// authority rule written here would be a second answer to a question that already has one.</para>
/// </summary>
public static class SupportSessionPolicy
{
    /// <summary>
    /// How long a support session lasts: 60 minutes from the instant it was started, which is both
    /// the default and the server maximum #512's contract fixes.
    ///
    /// <para>It is a constant rather than a setting, and the start request carries no duration at
    /// all, because a session's lifetime is a security property of somebody else's data being
    /// readable rather than something the reader chooses. A caller-supplied duration - even one
    /// capped at this value - would make "how long was this business readable?" a question about the
    /// request instead of a fact about the feature, and the only way to hold the session open longer
    /// is to start another one, which is audited like the first.</para>
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The longest reason a session may carry, matching the rejection reason's bound beside it. It
    /// is a bound on a value a person types into a form rather than a meaningful limit: the reason
    /// exists so the audit trail says why somebody read another business's data, and a value this
    /// long is a paste accident.
    /// </summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// The refusal for a start with no reason at all. A support session is read access to somebody
    /// else's business, so "why" is not optional - unlike an application rejection's reason, which
    /// the applicant reads and which a decision can stand without.
    /// </summary>
    public const string ReasonRequiredMessage =
        "A support session needs a reason, which is recorded in the audit trail.";

    /// <summary>The message an over-long reason is refused with. It never echoes the reason back.</summary>
    public const string ReasonTooLongMessage =
        "A support session reason must be no longer than 500 characters.";

    /// <summary>
    /// The refusal for a business that is not trading. A pending application, a refused one, a
    /// deactivated business and an undeclared stored value are each refused with this one sentence:
    /// a business whose data nobody inside it may reach is not a business a supporter may read
    /// either, and a state nobody declared is not a state anything may be decided from.
    ///
    /// <para>Like <see cref="BusinessLifecyclePolicy"/>'s refusals, and unlike
    /// <see cref="BusinessDecisionPolicy"/>'s, this sentence may name the business's own state: it
    /// reaches the platform administrator alone, whose directory already reports every business's
    /// status.</para>
    /// </summary>
    public const string BusinessNotActiveMessage =
        "This business is not active, so a support session cannot be started for it.";

    /// <summary>
    /// The refusal for a second concurrent session. One active session per administrator is #512's
    /// contract, and the reason is attribution: two live sessions make "which business was this
    /// administrator reading?" unanswerable for every request that carries neither business id, and
    /// they double the data readable at one moment for no case anybody asked for. Ending the first
    /// one is a single call, and it is audited.
    /// </summary>
    public const string AlreadyActiveMessage =
        "You already have an active support session. End it before starting another one.";

    /// <summary>
    /// When a session started at <paramref name="startedAtUtc"/> stops being usable.
    /// </summary>
    public static DateTime ExpiresAt(DateTime startedAtUtc) => startedAtUtc + Lifetime;

    /// <summary>
    /// Whether a support session may be started for - and, since issue #551, may still be
    /// <em>used</em> to read - a business in <paramref name="status"/>: only
    /// <see cref="BusinessStatus.Active"/> may.
    ///
    /// <para>The same rule answers both questions deliberately, rather than a second predicate
    /// saying the same thing: a business a session could not have been started for is not one it may
    /// go on reading, so a deactivation between two requests closes the view on the next one whether
    /// or not anything remembered to end the session row.</para>
    /// </summary>
    public static bool CanStartFor(BusinessStatus status) => status == BusinessStatus.Active;

    /// <summary>
    /// Whether a stored session is still active at <paramref name="nowUtc"/>: it has not been ended
    /// and it has not reached its expiry.
    ///
    /// <para>The expiry is deliberately derived rather than swept: a session stops being usable at
    /// the instant it expires, on every path at once, with no job to run and no row left claiming an
    /// expired session is still open. The <c>EndedAtUtc</c> column is therefore only ever written by
    /// something that <em>decided</em> to end a session - the administrator's own end call, or the
    /// deactivation of the business it was inside - and an expired session keeps a null
    /// <c>EndedAtUtc</c> for the rest of its row's life. Nothing reads that column to mean "still
    /// usable"; this rule is what everything reads.</para>
    ///
    /// <para>The boundary is inclusive of the expiry instant, exactly as an invite's is: a session is
    /// over <em>at</em> <paramref name="expiresAtUtc"/>, not one tick later. Sixty minutes of access
    /// is the promise, and a session that works on the first tick of the sixty-first minute is a
    /// session that outlived it.</para>
    /// </summary>
    /// <param name="expiresAtUtc">The session's own expiry instant.</param>
    /// <param name="endedAtUtc">When it was ended, or <see langword="null"/> while it was not.</param>
    /// <param name="nowUtc">The instant to judge it at.</param>
    public static bool IsActive(DateTime expiresAtUtc, DateTime? endedAtUtc, DateTime nowUtc) =>
        endedAtUtc is null && nowUtc < expiresAtUtc;

    /// <summary>
    /// Reads a submitted reason and yields the value to store: trimmed, and required.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The reason is missing, blank or longer than <see cref="MaxReasonLength"/> characters. Nothing
    /// is read, generated or written in either case, and neither message echoes the reason.
    /// </exception>
    public static string RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException(ReasonRequiredMessage);
        }

        var trimmed = reason.Trim();

        return trimmed.Length <= MaxReasonLength
            ? trimmed
            : throw new DomainValidationException(ReasonTooLongMessage);
    }
}
