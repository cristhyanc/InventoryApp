using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Suppliers;

/// <summary>
/// The rules of one platform supplier preset (issue #848, child B of #835): what its name must be
/// and how two names are compared.
///
/// <para>The optional website is deliberately <em>not</em> here. It is the same value a business's
/// own supplier carries, and issue #847 (merged as PR #858) owns that one rule in
/// <c>Inventory.Application.Suppliers.SupplierUrlPolicy</c>; the preset use cases run it before
/// anything is stored. A second copy of it beside this policy would be the duplication #848's
/// fourth criterion forbids - and a value one of the two accepted and the other refused would be a
/// copy that fails when issue #835 child C starts copying presets into businesses.</para>
///
/// <para>A preset is platform configuration, not business data: it belongs to no business, and
/// issue #835 child C is what copies it into a business that is approved later. This policy
/// therefore decides nothing about a <em>supplier</em>, and nothing here reads or writes one.</para>
///
/// <para><strong>Names are compared with case ignored, and the comparison is a stored
/// value.</strong> "Coles" and "COLES" are the same suggestion, so accepting both would offer a
/// Super Admin a list with one supplier in it twice. The comparison is
/// <see cref="NormalisedName"/> - the trimmed name upper-cased with
/// <see cref="string.ToUpperInvariant"/> - which is persisted beside the display name and uniquely
/// indexed, so the rule is the database's and not a query's: SQLite's <c>NOCASE</c> collation folds
/// ASCII only, and a <c>WHERE</c> clause checked before an insert is a check two concurrent inserts
/// can both pass. The display name keeps the capitalisation that was typed, because that is what a
/// person reads.</para>
/// </summary>
public static class SupplierPresetPolicy
{
    /// <summary>The longest accepted preset name, measured on the trimmed value.</summary>
    public const int MaxNameLength = 200;

    /// <summary>What a caller who submitted no name, or only whitespace, is told.</summary>
    public const string NameRequiredMessage = "A supplier preset needs a name.";

    /// <summary>What a caller who submitted a name longer than <see cref="MaxNameLength"/> is told.</summary>
    public const string NameTooLongMessage =
        "A supplier preset name must be 200 characters or fewer.";

    /// <summary>
    /// What a caller is told when another preset already holds this name, ignoring case. It names
    /// neither the other row's key nor its capitalisation: the caller already knows the name they
    /// submitted, and "which existing row" is not a fact this refusal has to disclose.
    /// </summary>
    public const string DuplicateNameMessage =
        "A supplier preset with this name already exists.";

    /// <summary>
    /// The name as it is to be displayed and stored: trimmed, and never empty.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The name is blank, or longer than <see cref="MaxNameLength"/> once trimmed.
    /// </exception>
    public static string NormaliseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException(NameRequiredMessage);
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException(NameTooLongMessage);
        }

        return trimmed;
    }

    /// <summary>
    /// The value two names are compared by, and the value the unique index is over:
    /// <c>name.Trim().ToUpperInvariant()</c>.
    ///
    /// <para>It takes the raw name rather than an already-trimmed one so a caller cannot derive the
    /// key from a value this policy never saw - the trim is part of the comparison, not a step
    /// before it. It is invariant rather than current-culture on purpose: the stored key must mean
    /// the same thing on every machine that writes a row, and a Turkish-locale server upper-casing
    /// <c>i</c> to <c>İ</c> would index a name differently from the one that indexed it first.</para>
    /// </summary>
    public static string NormalisedName(string? name) =>
        (name ?? string.Empty).Trim().ToUpperInvariant();
}
