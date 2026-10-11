namespace Inventory.Domain.Tenancy;

/// <summary>
/// The Australian Business Number a business may record on its profile (issue #540), and the
/// checksum that decides whether a submitted one is an ABN at all.
///
/// An ABN is 11 digits carrying a modulus-89 check, so a mistyped or invented number is
/// detectable without asking the register: subtract 1 from the first digit, multiply the digits by
/// the fixed weights <c>10, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19</c>, and a real ABN's weighted sum
/// is divisible by 89. That is the whole rule, it is deterministic, and it belongs in the Domain
/// rather than in a form validator or a database constraint: the number appears on tax invoices
/// and in the GST accounting aid, so storing a number that cannot be an ABN is storing a wrong
/// fact about the business.
///
/// <para>The check is necessary, not sufficient: it proves the digits are well-formed, never that
/// the ABN is registered, current, or this business's. Nothing here contacts the register, and
/// nothing may infer GST registration from an ABN - <c>Business.GstRegistered</c> is a separate
/// recorded fact, because an entity can hold an ABN without being registered for GST.</para>
///
/// <para>The ABN is optional. "Not recorded" is <see langword="null"/>, and a blank or whitespace
/// value is not a third state: it is refused, so the absence of an ABN has exactly one
/// representation.</para>
/// </summary>
public static class AustralianBusinessNumber
{
    /// <summary>The number of digits an ABN has.</summary>
    public const int DigitCount = 11;

    /// <summary>
    /// The message an unusable ABN is refused with. It names no internal detail and is written for
    /// the person who typed it.
    /// </summary>
    public const string InvalidMessage =
        "An ABN must be 11 digits and pass the ABN check-digit test.";

    /// <summary>
    /// The published ABN weights, in digit order. The first digit's weight of <c>10</c> applies
    /// after 1 has been subtracted from it.
    /// </summary>
    private static readonly int[] Weights = [10, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19];

    /// <summary>
    /// Whether <paramref name="abn"/> is the canonical stored form of a well-formed ABN: exactly
    /// <see cref="DigitCount"/> ASCII digits that pass the check-digit test, with no spaces and no
    /// other character. <see langword="null"/> is <em>not</em> valid here - an absent ABN is the
    /// caller's own case to decide, and treating it as valid would let a null slip through a
    /// check that is meant to be about digits.
    /// </summary>
    public static bool IsValid(string? abn) => TryNormalize(abn, out var normalized) && normalized == abn;

    /// <summary>
    /// Reads a submitted ABN, which a person may legitimately type with spaces ("51 824 753 556",
    /// the form the register itself prints), and yields the canonical digits-only value to store.
    ///
    /// Only spaces are removed. A number carrying any other separator, a letter, fewer or more
    /// than <see cref="DigitCount"/> digits, or a failing check digit is refused rather than
    /// repaired: guessing what somebody meant by a malformed tax identifier is how a wrong one
    /// gets stored.
    /// </summary>
    public static bool TryNormalize(string? submitted, out string? normalized)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(submitted))
        {
            return false;
        }

        var digits = submitted.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (digits.Length != DigitCount || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (!HasValidCheckDigits(digits))
        {
            return false;
        }

        normalized = digits;
        return true;
    }

    private static bool HasValidCheckDigits(string digits)
    {
        var weighted = 0;

        for (var position = 0; position < DigitCount; position++)
        {
            var digit = digits[position] - '0';

            // 1 is subtracted from the leading digit before weighting, which is what makes the
            // test reject an otherwise plausible number whose first digit is off by one.
            if (position == 0)
            {
                digit -= 1;
            }

            weighted += digit * Weights[position];
        }

        return weighted % 89 == 0;
    }
}
