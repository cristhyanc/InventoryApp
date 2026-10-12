using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Tenancy;

/// <summary>
/// The deterministic rules of applying for a business (issue #541, one slice of #507 under #328):
/// what a submitted application must be, when a second submission is the same application rather
/// than a new one, and which sentence a caller who may not apply is refused with.
///
/// <para>Like <see cref="MemberInviteRedemptionPolicy"/> it holds no clock, no database and no
/// configuration - every answer is a function of the values handed in. Whether self-service
/// onboarding is switched on at all is deployment configuration and is deliberately not here: a
/// flag is not a rule about an application.</para>
///
/// <para><strong>It decides nothing about eligibility.</strong> Whether an identity may gain an
/// active membership - which is what an application does, since the applicant becomes the new
/// business's Owner - is <see cref="MembershipEligibility"/>'s single rule, re-checked inside
/// <c>MembershipWriteGuard</c>'s write transaction. The states the issue lists as eligible
/// (<see cref="AccountState.NoMembership"/>, <see cref="AccountState.MembershipRevoked"/> and
/// <see cref="AccountState.ApplicationRejected"/>) are exactly the states with no active
/// membership, so there is no second eligibility rule here to drift from it. All this policy does
/// with an <see cref="AccountState"/> is choose the <em>wording</em> of a refusal that rule already
/// decided on.</para>
///
/// <para><strong>Validation is <see cref="BusinessSettingsPolicy"/>, unchanged.</strong> An
/// application collects exactly the five settings an Owner can later edit, under exactly the same
/// rules - a required name, a required well-formed contact address, an optional checksum-validated
/// ABN, a GST registration that must be stated rather than defaulted, and a required IANA time zone
/// whose resolvability is decided centrally on the way into the database. Writing a second
/// validation here would be two answers to one question, and the two would eventually disagree
/// about what a business may record about itself.</para>
/// </summary>
public static class BusinessApplicationPolicy
{
    /// <summary>
    /// The refusal for a caller who already has an application waiting and has now submitted
    /// different details.
    ///
    /// Resubmitting the same form is how a person recovers from a lost response or a double
    /// submission, and that is answered with the application they already have (see
    /// <see cref="IsResubmission"/>). Submitting *different* details is a different request -
    /// "change my pending application" - and this slice does not implement it: nothing may silently
    /// overwrite details an operator is in the middle of reviewing, and nothing may create a second
    /// application for one identity. So it is refused, and the sentence says what to do about it.
    /// </summary>
    public const string PendingApplicationDiffersMessage =
        "You already have a business application waiting for a decision, and it was submitted with "
            + "different details. Wait for the decision rather than applying again.";

    /// <summary>
    /// Reads a submitted application and yields the settings to store, trimmed and normalised.
    ///
    /// It is <see cref="BusinessSettingsPolicy.Validate"/> and nothing more. It exists as a named
    /// entry point so the application path reads as its own rule at the call site and so this
    /// delegation is a decision somebody made rather than an import that happens to be convenient.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The submission breaks one of those rules. Nothing is normalised and the caller stores
    /// nothing.
    /// </exception>
    public static BusinessSettings Validate(
        string? name,
        string? contactEmail,
        string? abn,
        bool? gstRegistered,
        string? timeZoneId) =>
        BusinessSettingsPolicy.Validate(name, contactEmail, abn, gstRegistered, timeZoneId);

    /// <summary>
    /// Whether <paramref name="submitted"/> is the application <paramref name="pending"/> already
    /// records, rather than a different one.
    ///
    /// It is answered by <see cref="BusinessSettingsPolicy.ChangedFields"/>, the same comparison the
    /// settings audit trail reports a change set from, so "the same application" and "nothing
    /// changed" are one definition. That means the comparison is over the <em>validated</em> values:
    /// re-submitting <c>" Coastal Vending "</c> for a stored <c>Coastal Vending</c>, or
    /// <c>51 824 753 556</c> for a stored <c>51824753556</c>, is the same application, because what
    /// a person typed around the value is not part of it. A different capitalisation <em>is</em> a
    /// different application: the stored name is the one they typed, and correcting its case is a
    /// change to what the business would publish about itself.
    /// </summary>
    public static bool IsResubmission(BusinessSettings pending, BusinessSettings submitted) =>
        BusinessSettingsPolicy.ChangedFields(pending, submitted).Count == 0;

    /// <summary>
    /// The sentence a caller who may not apply is refused with, chosen by the state their own
    /// account is in.
    ///
    /// Deliberately the same two sentences, from the same two constants, that an invite redemption
    /// refuses with (<see cref="MemberInviteRedemptionPolicy.IneligibleMessage"/>): the situation is
    /// identical - this identity already holds an active membership somewhere - and a second
    /// wording for it would be a second answer to one question. In particular
    /// <see cref="AccountState.BusinessDeactivated"/> keeps its own sentence, because the standing
    /// one would send that person to an Owner who cannot be reached; only the platform
    /// administrator can release them. Neither sentence names a business, a role or another member.
    ///
    /// <para>It is total rather than partial - it answers for the eligible states too - because a
    /// refusal happens only because <see cref="MembershipEligibility"/> refused, never because of
    /// the state. <see cref="AccountState.ApplicationPending"/> reaches it only when the caller
    /// submitted details that differ from the application they already have; an identical
    /// resubmission is not a refusal at all.</para>
    /// </summary>
    public static string IneligibleMessage(AccountState state) =>
        state == AccountState.ApplicationPending
            ? PendingApplicationDiffersMessage
            : MemberInviteRedemptionPolicy.IneligibleMessage(state);
}
