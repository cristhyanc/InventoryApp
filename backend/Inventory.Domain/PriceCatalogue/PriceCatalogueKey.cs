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
/// <para><strong>The separator is escaped rather than assumed absent.</strong> A part's own
/// <see cref="Separator"/> is doubled, so no two different sets of parts can produce one key: without
/// that, a product named <c>"Cola|375"</c> with no unit label and a product named <c>"Cola"</c> with
/// unit label <c>"375"</c> would be indistinguishable, and a price would be collected for the wrong
/// thing. Nothing ever decodes a key - it is only ever compared - so the escape exists purely to keep
/// the encoding injective.</para>
/// </summary>
public static class PriceCatalogueKey
{
    /// <summary>
    /// The character between two parts of a key. A printable one, so a key is readable in the
    /// database and in a test failure, and one that does not occur in a normalised part unescaped.
    /// </summary>
    public const char Separator = '|';

    /// <summary>
    /// The key of <paramref name="parts"/>: each part upper-cased invariantly with its whitespace
    /// removed and its own separators doubled, joined by <see cref="Separator"/>.
    ///
    /// <para>A <see langword="null"/> or blank part contributes an empty part rather than being
    /// skipped, because "no unit label" is a value the key has to distinguish from a unit label that
    /// happens to be the next part's text.</para>
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

            AppendNormalised(key, parts[index]);
        }

        return key.ToString();
    }

    /// <summary>
    /// The longest key <see cref="Of"/> can produce from parts of the given maximum lengths: every
    /// character of every part escaped, plus one separator between each pair.
    ///
    /// <para>It bounds the column rather than the rule, and it is computed from the part limits
    /// rather than guessed, so widening one of those limits cannot leave a key column too narrow for
    /// the keys it has to hold.</para>
    /// </summary>
    public static int MaxLengthOfParts(params int[] maxPartLengths)
    {
        ArgumentNullException.ThrowIfNull(maxPartLengths);

        return maxPartLengths.Sum(length => 2 * length) + Math.Max(maxPartLengths.Length - 1, 0);
    }

    private static void AppendNormalised(StringBuilder key, string? part)
    {
        foreach (var character in part ?? string.Empty)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            if (character == Separator)
            {
                key.Append(Separator);
            }

            key.Append(char.ToUpperInvariant(character));
        }
    }
}
