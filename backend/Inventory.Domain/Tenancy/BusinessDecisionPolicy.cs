using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Tenancy;

/// <summary>
/// The deterministic rules of deciding a business application (issue #544, one slice of #509 under
/// #328): which status may be decided at all, what a rejection reason may be, and which sentence a
/// refused decision carries.
///
/// <para>Like <see cref="BusinessApplicationPolicy"/> it holds no clock, no database and no
/// configuration - every answer is a function of the values handed in - and it decides nothing
/// about <em>who</em> may make the decision. That is the configured platform administrator and
/// nothing else, enforced at the HTTP boundary by <c>PlatformAdminPolicy</c> (issue #336); an
/// authority rule written here would be a second answer to a question that already has one.</para>
///
/// <para><strong>It decides nothing about eligibility either.</strong> Whether the applicant may
/// hold an active membership once their business is trading is
/// <see cref="MembershipEligibility"/>'s single rule, re-checked inside
/// <c>MembershipWriteGuard</c>'s write transaction with the applicant's own membership excluded.
/// There is no second eligibility rule here to drift from it.</para>
///
/// <para><strong>Only a <see cref="BusinessStatus.Pending"/> business may be decided.</strong>
/// Approving an already-active business, re-rejecting a rejected one or reviving a deactivated one
/// are each a different operation with different consequences, and two of the three are explicitly
/// somebody else's work (#545). An undeclared stored status is not decidable either: the deny
/// direction is the default, exactly as it is in
/// <see cref="BusinessMembershipResolutionPolicy"/>.</para>
/// </summary>
public static class BusinessDecisionPolicy
{
    /// <summary>
    /// The longest rejection reason that may be stored. The reason is an operator's note that the
    /// applicant reads back through <c>GET /api/me/account-state</c>, so it is bounded for the same
    /// reason every other stored free-text value is: an unbounded column written from a request is
    /// a way to make a row that nothing can render.
    /// </summary>
    public const int MaxRejectionReasonLength = 500;

    /// <summary>
    /// The refusal for a business that is not waiting for a decision. It names no status - neither
    /// the one that was stored nor the one that was asked for - because the caller is told that
    /// their view of this application is stale and to look again, which is the whole of what they
    /// need. It is deliberately the same sentence for approve and reject: the fact is one fact.
    /// </summary>
    public const string NotPendingMessage =
        "This business application is no longer waiting for a decision.";

    /// <summary>
    /// The refusal for a pending application whose applicant cannot be identified from its
    /// memberships, or whose membership in it is no longer active - somebody who was released and
    /// has since joined another business, for example.
    ///
    /// A decision that silently approved such a business would create a trading tenant with nobody
    /// who can reach it, or would confirm an active membership nothing checked, so it fails closed.
    /// The sentence describes the application rather than the person, and names no other business.
    /// </summary>
    public const string ApplicantUnavailableMessage =
        "This business application no longer has an applicant whose membership can be approved.";

    /// <summary>The refusal for a rejection reason longer than <see cref="MaxRejectionReasonLength"/>.</summary>
    public const string RejectionReasonTooLongMessage =
        "A rejection reason must be 500 characters or fewer.";

    /// <summary>
    /// Whether a business in <paramref name="status"/> may be approved or rejected.
    ///
    /// Only <see cref="BusinessStatus.Pending"/> may, including when the stored value is one this
    /// code does not declare: an undecidable status is the default rather than a case somebody has
    /// to remember to add.
    /// </summary>
    public static bool IsDecidable(BusinessStatus status) => status == BusinessStatus.Pending;

    /// <summary>
    /// Reads a submitted rejection reason and yields the value to store: trimmed, and
    /// <see langword="null"/> when the operator gave none.
    ///
    /// "No reason" has exactly one representation, <see langword="null"/>, so a blank string and an
    /// absent field are the same stored fact rather than two kinds of absence - the rule
    /// <see cref="BusinessSettingsPolicy"/> applies to every other optional business column. A
    /// rejection with no reason is still a rejection: the state never becomes something else
    /// because the text is missing.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The reason is longer than <see cref="MaxRejectionReasonLength"/> once trimmed. Nothing is
    /// normalised and the caller stores nothing.
    /// </exception>
    public static string? ValidateRejectionReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var trimmed = reason.Trim();

        return trimmed.Length > MaxRejectionReasonLength
            ? throw new DomainValidationException(RejectionReasonTooLongMessage)
            : trimmed;
    }
}
