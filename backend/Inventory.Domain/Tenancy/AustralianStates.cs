namespace Inventory.Domain.Tenancy;

/// <summary>
/// The Australian state or territory a business trades in (issue #933, one slice of #817), and the
/// closed vocabulary that decides whether a submitted value is one of them.
///
/// <para>It is a <em>vocabulary</em>, not a lookup: the eight values are written out here, in one
/// place, because a state is an administrative fact about the country rather than configuration a
/// deployment chooses, and because what reads it - the supplier records that serve only some states
/// (#817) - must never be able to be pointed at a ninth value somebody typed. A submitted value
/// that is not one of the eight is refused rather than repaired or stored as somebody's guess.</para>
///
/// <para><strong>It is never inferred.</strong> Not from the business's time zone - which cannot
/// tell New South Wales from the Australian Capital Territory, the reason this column exists - not
/// from an address, not from an ABN, and not from a phone number. The only way a business gets a
/// state is that somebody recorded one: the application form (issue #543) collects it, and the
/// Owner changes it in Business settings (issue #548).</para>
///
/// <para><strong>The state is optional in the stored shape.</strong> Every business that existed
/// before this column did has none, nothing backfills them, and <see langword="null"/> is the single
/// representation of "not recorded" - a blank or whitespace value is refused rather than stored as a
/// second kind of absence. A <em>submission</em> is narrower: both write paths require a state, so a
/// record becomes steadily more complete and never less (see
/// <see cref="BusinessSettingsPolicy.Validate"/>).</para>
/// </summary>
public static class AustralianStates
{
    /// <summary>
    /// The longest code here (<c>QLD</c>, <c>TAS</c>, <c>VIC</c>, <c>ACT</c>). It bounds the stored
    /// column; the vocabulary below, not the length, is what decides validity.
    /// </summary>
    public const int MaxLength = 3;

    /// <summary>
    /// The message a missing or unrecognised state is refused with. One sentence for both, because
    /// "you did not choose one" and "that is not one of them" have the same remedy, and it names
    /// the eight values rather than echoing what was submitted.
    /// </summary>
    public const string InvalidMessage =
        "A business state or territory is required, and must be one of NSW, VIC, QLD, SA, WA, TAS, ACT or NT.";

    /// <summary>
    /// The eight Australian states and territories, as their standard abbreviations and in the
    /// order a form offers them: the five states by population, then Tasmania, then the two
    /// territories. It is the stored form too - the code, never a long name - so what is stored,
    /// what an API publishes and what a form offers are one list.
    /// </summary>
    public static readonly IReadOnlyList<string> All = ["NSW", "VIC", "QLD", "SA", "WA", "TAS", "ACT", "NT"];

    /// <summary>
    /// Whether <paramref name="state"/> is the canonical stored form of one of <see cref="All"/>:
    /// the abbreviation exactly as this vocabulary spells it, with no surrounding whitespace and no
    /// other casing. <see langword="null"/> is <em>not</em> valid here - an unrecorded state is the
    /// caller's own case to decide, and treating it as valid would let a null slip through a check
    /// that is meant to be about a value.
    /// </summary>
    public static bool IsValid(string? state) => state is not null && All.Contains(state, StringComparer.Ordinal);

    /// <summary>
    /// Reads a submitted state, which a person may legitimately type in lower case or with
    /// surrounding whitespace, and yields the canonical abbreviation to store.
    ///
    /// Only the surrounding whitespace and the casing are normalised. A long name
    /// ("New South Wales"), an abbreviation of another country's subdivision, or anything else is
    /// refused rather than interpreted: guessing which state somebody meant is how a business comes
    /// to be recorded as trading somewhere it does not.
    /// </summary>
    public static bool TryNormalize(string? submitted, out string? normalized)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(submitted))
        {
            return false;
        }

        var candidate = submitted.Trim().ToUpperInvariant();
        if (!All.Contains(candidate, StringComparer.Ordinal))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
