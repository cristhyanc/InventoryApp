using Inventory.Application.ReceiptScanning;

namespace Inventory.Infrastructure.ReceiptScanning;

/// <summary>
/// Counts a PDF's pages (issue #1163), so a document over the reader's page limit is refused before
/// anything is uploaded.
///
/// <para><strong>It is a count, not a parser, and it is deliberately conservative.</strong> This
/// application has no PDF library and does not need one: the only question is "is this a PDF, and does
/// it have more than N pages?". The answer is read from the page-tree node counts a conforming PDF
/// writes - <c>/Type /Pages</c> objects carry a <c>/Count</c> - and falls back to counting
/// <c>/Type /Page</c> objects when none is readable. A file that is not a PDF, or one whose structure
/// cannot be read at all, counts as nothing, and
/// <c>Inventory.Domain.ReceiptScanning.ReceiptScanFilePolicy.CheckPdfPages</c> refuses it: a file
/// named <c>.pdf</c> that is not one must not be sent to the reader whole, and a PDF nobody can count
/// must not be assumed to be one page.</para>
///
/// <para><strong>An encrypted or object-stream PDF counts as nothing too</strong>, and is refused
/// with the message that suggests photographing the receipt instead. That is the conservative
/// direction: the failure is a refusal the operator can work around, not a page limit silently
/// bypassed.</para>
/// </summary>
public sealed class PdfPageCounter : IPdfPageCounter
{
    /// <summary>
    /// The bytes every PDF starts with. Checked rather than trusted from the extension, because the
    /// extension is the one thing about an uploaded file that the client chose.
    /// </summary>
    private static readonly byte[] Header = "%PDF-"u8.ToArray();

    private static readonly byte[] PagesType = "/Type"u8.ToArray();

    /// <inheritdoc />
    public int? Count(ReadOnlySpan<byte> content)
    {
        if (!content.StartsWith(Header))
        {
            return null;
        }

        // The largest /Count on a /Pages node is the page tree's root count in every PDF this will
        // see; a linearised file repeats it on sub-nodes with smaller values.
        var byCount = LargestPageTreeCount(content);
        if (byCount is > 0)
        {
            return byCount;
        }

        var byLeaves = PageLeafCount(content);

        return byLeaves > 0 ? byLeaves : null;
    }

    /// <summary>
    /// The largest <c>/Count</c> that belongs to a <c>/Type /Pages</c> object, or <c>0</c> when none
    /// is readable.
    /// </summary>
    private static int LargestPageTreeCount(ReadOnlySpan<byte> content)
    {
        var largest = 0;

        foreach (var position in Occurrences(content, PagesType))
        {
            // The object's dictionary, from its /Type onwards, bounded so a malformed file cannot
            // make this scan the whole document for every match.
            var window = content[position..];
            if (window.Length > DictionaryWindow)
            {
                window = window[..DictionaryWindow];
            }

            if (!IsTypeValue(window, "Pages"u8))
            {
                continue;
            }

            var count = CountValue(window);
            if (count > largest)
            {
                largest = count;
            }
        }

        return largest;
    }

    /// <summary>How many <c>/Type /Page</c> objects the file holds.</summary>
    private static int PageLeafCount(ReadOnlySpan<byte> content)
    {
        var leaves = 0;

        foreach (var position in Occurrences(content, PagesType))
        {
            var window = content[position..];
            if (window.Length > DictionaryWindow)
            {
                window = window[..DictionaryWindow];
            }

            if (IsTypeValue(window, "Page"u8))
            {
                leaves++;
            }
        }

        return leaves;
    }

    /// <summary>
    /// How far past a <c>/Type</c> this counter is willing to read for the value and the
    /// <c>/Count</c> beside it. Generous for a dictionary and small enough that a hostile file full
    /// of the token <c>/Type</c> cannot turn this into a quadratic scan.
    /// </summary>
    private const int DictionaryWindow = 512;

    /// <summary>
    /// Whether the <c>/Type</c> at the start of <paramref name="window"/> names
    /// <paramref name="expected"/> - and names exactly it, so <c>/Pages</c> is not read as
    /// <c>/Page</c>.
    /// </summary>
    private static bool IsTypeValue(ReadOnlySpan<byte> window, ReadOnlySpan<byte> expected)
    {
        var index = PagesType.Length;
        while (index < window.Length && IsWhitespace(window[index]))
        {
            index++;
        }

        if (index >= window.Length || window[index] != (byte)'/')
        {
            return false;
        }

        index++;
        var name = window[index..];
        if (!name.StartsWith(expected))
        {
            return false;
        }

        // A PDF name ends at whitespace or a delimiter. Without this, /Pages matches /Page.
        var after = index + expected.Length;

        return after >= window.Length || IsNameBoundary(window[after]);
    }

    /// <summary>
    /// The <c>/Count</c> value inside <paramref name="window"/>, or <c>0</c> when there is none that
    /// reads as a non-negative integer.
    /// </summary>
    private static int CountValue(ReadOnlySpan<byte> window)
    {
        var marker = "/Count"u8;
        var at = window.IndexOf(marker);
        if (at < 0)
        {
            return 0;
        }

        var index = at + marker.Length;
        while (index < window.Length && IsWhitespace(window[index]))
        {
            index++;
        }

        var value = 0;
        var digits = 0;
        while (index < window.Length && window[index] is >= (byte)'0' and <= (byte)'9' && digits < 9)
        {
            value = (value * 10) + (window[index] - (byte)'0');
            index++;
            digits++;
        }

        return digits == 0 ? 0 : value;
    }

    /// <summary>Every position of <paramref name="token"/> in <paramref name="content"/>.</summary>
    private static IEnumerable<int> Occurrences(ReadOnlySpan<byte> content, byte[] token)
    {
        // Materialised rather than yielded: a ref struct cannot be captured by an iterator, and a
        // receipt PDF is small enough that the positions of one short token are nothing.
        var positions = new List<int>();
        var offset = 0;

        while (offset < content.Length)
        {
            var at = content[offset..].IndexOf(new ReadOnlySpan<byte>(token));
            if (at < 0)
            {
                break;
            }

            positions.Add(offset + at);
            offset += at + 1;
        }

        return positions;
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0 or 12;

    /// <summary>
    /// Whether <paramref name="value"/> ends a PDF name: whitespace, or one of the delimiters a name
    /// may not contain.
    /// </summary>
    private static bool IsNameBoundary(byte value) =>
        IsWhitespace(value)
        || value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
