namespace Inventory.Domain.Purchases;

/// <summary>
/// The deterministic rules about the supplier's receipt or invoice number recorded on a purchase
/// (issue #1162, sub-issue A of #975).
///
/// <para><strong>It is optional, and blank means none.</strong> A purchase is the record of what was
/// bought; the supplier's own document number is a reference to the paperwork it came from, so a
/// business that has none - or has not typed one - records a purchase exactly as before. "Entered
/// nothing", "entered spaces" and "cleared what was there" are therefore one stored state,
/// <c>null</c>, rather than three indistinguishable blanks a reader would have to guess at.</para>
///
/// <para><strong>It is stored trimmed.</strong> A number read off a receipt is routinely typed or
/// pasted with surrounding whitespace, and " INV-123 " and "INV-123" are the same invoice. Nothing
/// else about the text is changed: the case, the inner spacing and the punctuation a supplier uses
/// are the supplier's, so they are stored exactly as entered.</para>
///
/// <para><strong>It is at most <see cref="MaxLength"/> characters</strong>, measured after trimming,
/// which is also the stored column's length. A longer submission is refused by name rather than
/// silently truncated: a truncated invoice number is wrong rather than short, and it would be
/// reported back as though it were what the supplier printed.</para>
///
/// <para>This policy says nothing about <em>which</em> invoice numbers exist or whether two
/// purchases may share one: duplicate warnings and uniqueness rules are explicitly out of this
/// issue's scope, and so are searching and filtering by the number.</para>
/// </summary>
public static class PurchaseInvoiceNumberPolicy
{
    /// <summary>The longest invoice number a purchase may record, and the stored column's length.</summary>
    public const int MaxLength = 100;

    /// <summary>What a caller is told when the submitted invoice number is longer than that.</summary>
    public const string TooLongMessage = "Invoice number must be 100 characters or fewer.";

    /// <summary>
    /// The value to store for <paramref name="submitted"/>: the trimmed text, or <c>null</c> when
    /// nothing but whitespace was submitted.
    /// </summary>
    public static string? Normalize(string? submitted) =>
        string.IsNullOrWhiteSpace(submitted) ? null : submitted.Trim();

    /// <summary>
    /// Whether <paramref name="submitted"/> is short enough to store, judged on the value
    /// <see cref="Normalize"/> would produce - so trailing whitespace never makes an acceptable
    /// invoice number too long.
    /// </summary>
    public static bool IsWithinLimit(string? submitted) => (Normalize(submitted)?.Length ?? 0) <= MaxLength;
}
