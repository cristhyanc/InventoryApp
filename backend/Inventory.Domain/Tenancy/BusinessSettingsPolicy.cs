using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Tenancy;

/// <summary>
/// What a submitted set of business settings must be, and what changed when one is saved (issue
/// #548). It is the one rule both halves of the Owner's business-settings endpoint read, and it is
/// deterministic: no clock, no host, no database and no configuration.
///
/// <para><strong>A save is the whole set.</strong> The endpoint replaces every setting rather than
/// patching individual ones, so <see cref="Validate"/> takes all six and refuses the submission as
/// a whole. That is why a missing contact address, GST registration or state is a refusal rather
/// than "no change": a business that has recorded none of them is the state issue #540's migration
/// left - and issue #933's, for the state - and a save is exactly the moment to stop it from being
/// permanent. The ABN stays optional, because an entity may legitimately have none, and
/// <see langword="null"/> is its single representation of "not recorded".</para>
///
/// <para><strong>The state is a closed vocabulary, checked here and never inferred.</strong>
/// <see cref="AustralianStates"/> holds the eight values and the normalisation; this policy refuses
/// anything else with that type's own sentence (issue #933). Nothing derives a state from the time
/// zone - the two answer different questions, and a zone cannot tell NSW from the ACT - or from an
/// address.</para>
///
/// <para><strong>The time zone is checked here only for presence.</strong> Whether an id is a zone
/// the host can actually keep a calendar in is not a deterministic rule - it is a question for the
/// platform's time-zone database, which this layer may not reach - so it is answered centrally on
/// the way into the database by <c>Inventory.Infrastructure.Data.BusinessTimeZoneEnforcer</c>, for
/// every write path alike, with the same <see cref="DomainValidationException"/> and so the same
/// <c>400</c>. Validating it twice in two places with two answers is the failure mode that rule
/// exists to avoid.</para>
///
/// <para><strong>A refusal is a <see cref="DomainValidationException"/>.</strong> Its message is
/// written for the person who typed the value, names only the rule that refused it, and never
/// echoes an address or an ABN back into a log, a problem detail or an exception message; the API
/// boundary publishes it verbatim as <c>400</c> (docs/architecture.md § Domain and application
/// error mapping).</para>
/// </summary>
public static class BusinessSettingsPolicy
{
    /// <summary>
    /// The longest display name a business may be given. It is a bound on a value a person types
    /// into a form, not a meaningful business limit: a name is for display, nothing resolves a
    /// business by it, and a value this long is a paste accident.
    /// </summary>
    public const int MaxNameLength = 200;

    /// <summary>The message a missing or over-long name is refused with.</summary>
    public const string NameRequiredMessage =
        "A business name is required, and must be no longer than 200 characters.";

    /// <summary>The message a missing GST registration is refused with.</summary>
    public const string GstRegistrationRequiredMessage =
        "State whether the business is registered for GST. It is never inferred from an ABN.";

    /// <summary>The message a missing time zone is refused with.</summary>
    public const string TimeZoneRequiredMessage =
        "A business time zone is required, as an IANA identifier such as 'Australia/Sydney'.";

    /// <summary>
    /// Reads a submitted set of settings and yields the values to store, with the name and the
    /// contact address trimmed, the ABN reduced to its canonical digits and the state reduced to
    /// its canonical abbreviation.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The name is missing or too long, the contact address is missing or cannot be an address, the
    /// GST registration is not stated, the ABN is present but is not a well-formed one, the time
    /// zone is missing, or the state is missing or is not one of
    /// <see cref="AustralianStates.All"/>. Nothing is normalised in that case and the caller stores
    /// nothing.
    /// </exception>
    public static BusinessSettings Validate(
        string? name,
        string? contactEmail,
        string? abn,
        bool? gstRegistered,
        string? timeZoneId,
        string? state)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            throw new DomainValidationException(NameRequiredMessage);
        }

        if (!BusinessContactEmail.TryNormalize(contactEmail, out var normalizedEmail))
        {
            throw new DomainValidationException(BusinessContactEmail.InvalidMessage);
        }

        if (gstRegistered is not { } registered)
        {
            throw new DomainValidationException(GstRegistrationRequiredMessage);
        }

        // Absent is the one state an ABN may be submitted in without digits. Anything else - a
        // blank string, a number of the wrong length, a failing check digit - is refused rather
        // than stored as a second kind of absence or as somebody's wrong ABN.
        string? normalizedAbn = null;
        if (abn is not null && !AustralianBusinessNumber.TryNormalize(abn, out normalizedAbn))
        {
            throw new DomainValidationException(AustralianBusinessNumber.InvalidMessage);
        }

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new DomainValidationException(TimeZoneRequiredMessage);
        }

        // Unlike the ABN, the state has no "absent" submission: a business trades somewhere, and
        // the form that collects this is where it is recorded. A value outside the eight is refused
        // with the same sentence as a missing one, because both have the same remedy.
        if (!AustralianStates.TryNormalize(state, out var normalizedState))
        {
            throw new DomainValidationException(AustralianStates.InvalidMessage);
        }

        return new BusinessSettings(
            name.Trim(),
            normalizedEmail,
            normalizedAbn,
            registered,
            timeZoneId.Trim(),
            normalizedState);
    }

    /// <summary>
    /// Which settings <paramref name="submitted"/> actually changes, compared with
    /// <paramref name="current"/>, in <see cref="BusinessSettingsField"/> declaration order.
    ///
    /// <para>It is what the audit event reports, so it answers "what changed" and not "what was
    /// sent": resubmitting the settings unchanged changes nothing and is audited as nothing.
    /// Recording a value that was not recorded before <em>is</em> a change - that is how a business
    /// that predates the application form gets its contact address, its ABN and its GST
    /// registration - and so is clearing the ABN back to "not recorded".</para>
    ///
    /// <para>Strings are compared ordinally and case-sensitively, because the stored value is the
    /// one a person typed: correcting the capitalisation of a business name or an email address is
    /// a change to what the business publishes about itself, and an audit trail that called it
    /// nothing would be wrong.</para>
    /// </summary>
    public static IReadOnlyList<BusinessSettingsField> ChangedFields(
        BusinessSettings current,
        BusinessSettings submitted)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(submitted);

        var changed = new List<BusinessSettingsField>(6);

        if (!string.Equals(current.Name, submitted.Name, StringComparison.Ordinal))
        {
            changed.Add(BusinessSettingsField.Name);
        }

        if (!string.Equals(current.ContactEmail, submitted.ContactEmail, StringComparison.Ordinal))
        {
            changed.Add(BusinessSettingsField.ContactEmail);
        }

        if (!string.Equals(current.Abn, submitted.Abn, StringComparison.Ordinal))
        {
            changed.Add(BusinessSettingsField.Abn);
        }

        if (current.GstRegistered != submitted.GstRegistered)
        {
            changed.Add(BusinessSettingsField.GstRegistered);
        }

        if (!string.Equals(current.TimeZoneId, submitted.TimeZoneId, StringComparison.Ordinal))
        {
            changed.Add(BusinessSettingsField.TimeZoneId);
        }

        if (!string.Equals(current.State, submitted.State, StringComparison.Ordinal))
        {
            changed.Add(BusinessSettingsField.State);
        }

        return changed;
    }
}
