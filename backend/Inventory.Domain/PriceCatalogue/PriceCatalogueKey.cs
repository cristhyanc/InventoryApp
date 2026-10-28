using System.Globalization;
using System.Text;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// The one way a comparison key is built in the shared supplier price catalogue (issue #923, parent
/// #817): the parts of a description, folded so that two spellings of the same thing produce one
/// key.
///
/// <para><strong>Why there is one builder rather than one per table.</strong> Two keys exist in this
/// catalogue - a master product's <see cref="MasterProductPolicy.NormalisedKey"/> and a product's
/// <see cref="PriceCatalogueWaitingKey"/> - and both answer the same question: "are these two
/// descriptions the same description?". A second folding rule beside this one would be a second
/// answer, and the parent issue exists because the owner must not search for one product five times.
/// </para>
///
/// <para><strong>Case and spacing, and nothing else</strong> (issue #923's deduplication rule).
/// Every character is upper-cased with <see cref="char.ToUpperInvariant(char)"/> and every
/// whitespace character is dropped, so <c>"Coca-Cola Classic"</c>, <c>"coca-colaclassic"</c> and
/// <c>"COCA-COLA   CLASSIC"</c> are one key. Nothing else is folded: punctuation stays, because
/// <c>"Coca-Cola"</c> and <c>"Coca Cola"</c> differing by a hyphen is a real difference this rule was
/// not asked to erase, and erasing it would merge two descriptions on a guess.</para>
///
/// <para><strong>The fold is invariant, not current-culture.</strong> A stored key must mean the same
/// thing on every machine that writes one, and a Turkish-locale server upper-casing <c>i</c> to
/// <c>İ</c> would key a description differently from the server that keyed it first - the same reason
/// <c>SupplierPresetPolicy.NormalisedName</c> is invariant.</para>
///
/// <para><strong>Each part is length-prefixed, so a part's own punctuation can never be read as a
/// part boundary.</strong> A part is written as its normalised character count, a
/// <see cref="LengthSeparator"/>, then those characters, and the parts are joined by
/// <see cref="Separator"/>: <c>Of("Coca-Cola Classic", "375mL")</c> is
/// <c>"16:COCA-COLACLASSIC|5:375ML"</c>. Nothing is escaped, because nothing needs to be - the count
/// says exactly where a part ends, so the characters after it are read as content whatever they are.
/// This replaced an escaping rule (the in-part separator doubled, then parts joined by a single
/// separator) that was <strong>not</strong> injective at a part boundary: it folded
/// <c>Of("AB|", "CD")</c> and <c>Of("AB", "|CD")</c> both to <c>"AB|||CD"</c>, which would have let
/// one business's product be priced as another product entirely. Keys are only ever compared and
/// never decoded, but the encoding is unambiguous in the strict sense - the count, the separator and
/// the characters can be read back part for part - because "no two different sets of parts produce
/// one key" is what master product uniqueness and cross-business waiting-key deduplication both rest
/// on.</para>
/// </summary>
public static class PriceCatalogueKey
{
    /// <summary>
    /// The character between two parts of a key. A printable one, so a key is readable in the
    /// database and in a test failure. A part may contain it freely: the part's length prefix, not
    /// this character, is what says where the part ends.
    /// </summary>
    public const char Separator = '|';

    /// <summary>
    /// The character between a part's length prefix and the part itself. Printable for the same
    /// reason, and not a digit, so the end of the prefix is unambiguous.
    /// </summary>
    public const char LengthSeparator = ':';

    /// <summary>
    /// The key of <paramref name="parts"/>: each part upper-cased invariantly with its whitespace
    /// removed, written as its resulting length, <see cref="LengthSeparator"/> and those characters,
    /// and the parts joined by <see cref="Separator"/>.
    ///
    /// <para>A <see langword="null"/> or blank part contributes an empty part - written
    /// <c>"0:"</c> - rather than being skipped, because "no unit label" is a value the key has to
    /// distinguish from a unit label that happens to be the next part's text.</para>
    /// </summary>
    public static string Of(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var key = new StringBuilder();

        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                key.Append(Separator);
            }

            var normalised = Normalise(parts[index]);

            key.Append(normalised.Length.ToString(CultureInfo.InvariantCulture));
            key.Append(LengthSeparator);
            key.Append(normalised);
        }

        return key.ToString();
    }

    /// <summary>
    /// The longest key <see cref="Of"/> can produce from parts of the given maximum lengths: every
    /// part at its full length, with its length prefix, plus one separator between each pair.
    ///
    /// <para>It bounds the column rather than the rule, and it is computed from the part limits
    /// rather than guessed, so widening one of those limits cannot leave a key column too narrow for
    /// the keys it has to hold. Normalising never lengthens a part - whitespace is dropped and
    /// upper-casing is character for character - so a part's own limit bounds its normalised length,
    /// and the longest prefix that length can be written with bounds its prefix.</para>
    /// </summary>
    public static int MaxLengthOfParts(params int[] maxPartLengths)
    {
        ArgumentNullException.ThrowIfNull(maxPartLengths);

        return maxPartLengths.Sum(length => LengthPrefixLength(length) + length)
            + Math.Max(maxPartLengths.Length - 1, 0);
    }

    private static int LengthPrefixLength(int maxPartLength) =>
        maxPartLength.ToString(CultureInfo.InvariantCulture).Length + 1;

    private static string Normalise(string? part)
    {
        var normalised = new StringBuilder();

        foreach (var character in part ?? string.Empty)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            normalised.Append(char.ToUpperInvariant(character));
        }

        return normalised.ToString();
    }
}
