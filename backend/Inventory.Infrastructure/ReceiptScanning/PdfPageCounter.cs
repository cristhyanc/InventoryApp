using Inventory.Application.ReceiptScanning;

namespace Inventory.Infrastructure.ReceiptScanning;

/// <summary>
/// Verifies how many pages a PDF really has (issue #1163), so a document over the reader's page limit
/// is refused before anything is uploaded.
///
/// <para><strong>It counts the pages the document can actually reach, and trusts nothing the document
/// declares.</strong> The count is the number of <c>/Type /Page</c> leaves reachable from the
/// catalogue the trailer's <c>/Root</c> names, through the <c>/Kids</c> of each <c>/Type /Pages</c>
/// node. A <c>/Count</c> written on a page-tree node is <strong>not read at all</strong>: it is the
/// document's own claim about itself, and a file that declares <c>/Count 1</c> while reaching two
/// hundred pages would otherwise be forwarded to the reader whole - the page limit bypassed by a
/// number anybody can type (the repair of 10 November 2026).</para>
///
/// <para><strong>It is a bounded structural parser, not a PDF library.</strong> This application has
/// no PDF library and does not need one: the only question is "is this a PDF, and how many pages can
/// it reach?". Every step has a ceiling - how many objects are indexed, how far into one object its
/// dictionary is read, how deep and how wide the page tree may be, and how many pages may be counted -
/// so a hostile file cannot turn the count into unbounded work or unbounded memory. A tree that
/// exceeds any of those ceilings is refused, never truncated into a smaller count.</para>
///
/// <para><strong>Anything it cannot verify counts as nothing, and
/// <c>Inventory.Domain.ReceiptScanning.ReceiptScanFilePolicy.CheckPdfPages</c> refuses it.</strong>
/// That covers a file that is not a PDF, one with no trailer or no readable catalogue, one that keeps
/// its catalogue or page tree in a compressed object stream, one whose page tree reaches an object the
/// file does not define exactly once, a page-tree node with no <c>/Type</c> or no readable
/// <c>/Kids</c>, and a tree that revisits a node. A file named <c>.pdf</c> that is not one must not be
/// sent to the reader whole, and a PDF nobody can count must not be assumed to be one page. The
/// refusal says "try a photo of the receipt instead", which is a refusal the operator can work
/// around - the conservative direction, never a page limit silently bypassed.</para>
/// </summary>
public sealed class PdfPageCounter : IPdfPageCounter
{
    /// <summary>
    /// How many numbered objects this parser will index. Far above a receipt PDF - a hundred-page
    /// document runs to a few thousand objects - and small enough that a hostile file cannot make the
    /// index itself expensive. A file with more is refused rather than indexed in part, so the answer
    /// never depends on where the index stopped.
    /// </summary>
    private const int MaxObjects = 20_000;

    /// <summary>
    /// How far past an object's <c>obj</c> keyword its dictionary is read. Generous for a dictionary,
    /// including a page-tree node whose <c>/Kids</c> array lists thousands of children, and bounded
    /// so the scan cannot become quadratic on a file full of <c>obj</c> tokens.
    /// </summary>
    private const int MaxDictionaryBytes = 64 * 1024;

    /// <summary>How many page-tree nodes may be visited, and how many may wait to be visited.</summary>
    private const int MaxNodes = 8_192;

    /// <summary>How deep the page tree may nest.</summary>
    private const int MaxDepth = 64;

    /// <summary>How many children one page-tree node may list.</summary>
    private const int MaxKids = 4_096;

    /// <summary>
    /// The most pages this parser will count. Far above the reader's documented hundred-page ceiling
    /// (<c>ReceiptScanningConfiguration.DefaultMaxPdfPages</c>); a document with more is refused as
    /// uncountable, which is also the right answer for a page limit configured above this.
    /// </summary>
    private const int MaxPages = 4_096;

    /// <summary>How far past a key a reference - <c>N G R</c> - is looked for.</summary>
    private const int MaxReferenceBytes = 64;

    /// <inheritdoc />
    public int? Count(ReadOnlySpan<byte> content)
    {
        // The bytes every PDF starts with. Checked rather than trusted from the extension, because
        // the extension is the one thing about an uploaded file that the client chose.
        if (!content.StartsWith("%PDF-"u8))
        {
            return null;
        }

        var objects = Index(content);
        if (objects is null || !TryPageTreeRoot(content, objects, out var root))
        {
            return null;
        }

        var pages = ReachablePages(content, objects, root);

        return pages > 0 ? pages : null;
    }

    /// <summary>
    /// Every object the file defines in plain bytes, as the byte range of its dictionary, or
    /// <see langword="null"/> when it defines more than <see cref="MaxObjects"/>.
    /// </summary>
    private static ObjectIndex? Index(ReadOnlySpan<byte> content)
    {
        var objects = new ObjectIndex();
        var offset = 0;

        while (offset < content.Length)
        {
            var at = content[offset..].IndexOf("obj"u8);
            if (at < 0)
            {
                break;
            }

            var keyword = offset + at;
            offset = keyword + 3;

            // "endobj" also contains the keyword; reading the object and generation numbers
            // backwards is what tells the two apart, because "end" is neither.
            if (!TryObjectNumber(content, keyword, out var number) || !IsDelimited(content, offset))
            {
                continue;
            }

            if (objects.Count >= MaxObjects)
            {
                return null;
            }

            objects.Add(number, offset, DictionaryLength(content, offset));
        }

        return objects;
    }

    /// <summary>
    /// The page tree's root object, taken from the catalogue the trailer's <c>/Root</c> names.
    ///
    /// <para>The trailer is what decides which object is the catalogue, so a file that plants a
    /// second, smaller <c>/Type /Catalog</c> object cannot have it counted instead. Trailers that
    /// name different catalogues - an incremental update this parser cannot order - are refused.</para>
    /// </summary>
    private static bool TryPageTreeRoot(ReadOnlySpan<byte> content, ObjectIndex objects, out int root)
    {
        root = 0;

        return TryTrailerRoot(content, out var catalogue)
            && objects.TryDictionary(content, catalogue, out var dictionary)
            && HasTypeName(dictionary, "Catalog"u8)
            && TryReference(dictionary, "/Pages"u8, out root);
    }

    /// <summary>
    /// The one object number every <c>/Root</c> in the file points at, or <see langword="false"/>
    /// when there is none or they disagree.
    /// </summary>
    private static bool TryTrailerRoot(ReadOnlySpan<byte> content, out int catalogue)
    {
        catalogue = 0;
        var found = false;
        var offset = 0;

        while (offset < content.Length)
        {
            var at = content[offset..].IndexOf("/Root"u8);
            if (at < 0)
            {
                break;
            }

            var position = offset + at;
            offset = position + 5;

            // A "/Root" that is not followed by a reference is not a trailer's: compressed stream
            // bytes can spell anything, and skipping it only ever leaves the real trailer to decide.
            if (!TryReference(Window(content, position), "/Root"u8, out var number))
            {
                continue;
            }

            if (found && number != catalogue)
            {
                return false;
            }

            catalogue = number;
            found = true;
        }

        return found;
    }

    /// <summary>
    /// How many <c>/Type /Page</c> leaves <paramref name="root"/> reaches, or <see langword="null"/>
    /// when the tree cannot be walked to the end within this parser's bounds.
    /// </summary>
    private static int? ReachablePages(ReadOnlySpan<byte> content, ObjectIndex objects, int root)
    {
        var pages = 0;
        var visited = new HashSet<int>();
        var pending = new Stack<PageNode>();
        pending.Push(new PageNode(root, 0));

        while (pending.Count > 0)
        {
            var node = pending.Pop();

            // A node visited twice is a cycle or a shared page; either way the tree is not one this
            // parser can count, so it is refused rather than counted once or counted for ever.
            if (node.Depth > MaxDepth || visited.Count >= MaxNodes || !visited.Add(node.Number))
            {
                return null;
            }

            if (!objects.TryDictionary(content, node.Number, out var dictionary))
            {
                return null;
            }

            if (HasTypeName(dictionary, "Page"u8))
            {
                pages++;
                if (pages > MaxPages)
                {
                    return null;
                }

                continue;
            }

            if (!HasTypeName(dictionary, "Pages"u8)
                || !TryPushKids(dictionary, pending, node.Depth + 1)
                || pending.Count > MaxNodes)
            {
                return null;
            }
        }

        return pages;
    }

    /// <summary>
    /// Pushes every child <paramref name="dictionary"/>'s <c>/Kids</c> array names, or
    /// <see langword="false"/> when the array is missing, unterminated, over
    /// <see cref="MaxKids"/> or holds anything that is not a reference.
    /// </summary>
    private static bool TryPushKids(ReadOnlySpan<byte> dictionary, Stack<PageNode> pending, int depth)
    {
        var at = dictionary.IndexOf("/Kids"u8);
        if (at < 0)
        {
            return false;
        }

        var index = at + 5;
        SkipWhitespace(dictionary, ref index);
        if (index >= dictionary.Length || dictionary[index] != (byte)'[')
        {
            return false;
        }

        index++;
        var kids = 0;

        while (true)
        {
            SkipWhitespace(dictionary, ref index);
            if (index >= dictionary.Length)
            {
                return false;
            }

            if (dictionary[index] == (byte)']')
            {
                return true;
            }

            if (kids >= MaxKids || !TryReferenceAt(dictionary, ref index, out var number))
            {
                return false;
            }

            pending.Push(new PageNode(number, depth));
            kids++;
        }
    }

    /// <summary>
    /// The object number the reference beside <paramref name="key"/> names, or
    /// <see langword="false"/> when there is no such key or its value is not a reference.
    /// </summary>
    private static bool TryReference(ReadOnlySpan<byte> window, ReadOnlySpan<byte> key, out int number)
    {
        number = 0;
        var at = window.IndexOf(key);
        if (at < 0)
        {
            return false;
        }

        var index = at + key.Length;
        if (!SkipWhitespace(window, ref index))
        {
            return false;
        }

        return TryReferenceAt(window, ref index, out number);
    }

    /// <summary>
    /// Reads the reference - <c>N G R</c> - at <paramref name="index"/> and leaves
    /// <paramref name="index"/> just past it.
    /// </summary>
    private static bool TryReferenceAt(ReadOnlySpan<byte> window, ref int index, out int number)
    {
        if (!TryInteger(window, ref index, out number)
            || !SkipWhitespace(window, ref index)
            || !TryInteger(window, ref index, out _)
            || !SkipWhitespace(window, ref index)
            || index >= window.Length
            || window[index] != (byte)'R')
        {
            return false;
        }

        index++;

        return true;
    }

    /// <summary>
    /// The object number written before the <c>obj</c> keyword at <paramref name="keyword"/>, read
    /// backwards through the generation number, or <see langword="false"/> when the bytes before it
    /// are not <c>N G</c>.
    /// </summary>
    private static bool TryObjectNumber(ReadOnlySpan<byte> content, int keyword, out int number)
    {
        number = 0;
        var index = keyword - 1;

        return SkipWhitespaceBackwards(content, ref index)
            && TryIntegerBackwards(content, ref index, out _)
            && SkipWhitespaceBackwards(content, ref index)
            && TryIntegerBackwards(content, ref index, out number);
    }

    /// <summary>
    /// How much of the object starting at <paramref name="start"/> is its dictionary: up to its
    /// <c>endobj</c>, up to the <c>stream</c> keyword that ends a stream object's dictionary, and in
    /// any case no further than <see cref="MaxDictionaryBytes"/>.
    /// </summary>
    private static int DictionaryLength(ReadOnlySpan<byte> content, int start)
    {
        var window = Window(content, start, MaxDictionaryBytes);
        var length = window.Length;

        var end = window.IndexOf("endobj"u8);
        if (end >= 0)
        {
            length = end;
        }

        var stream = window[..length].IndexOf("stream"u8);

        return stream >= 0 ? stream : length;
    }

    /// <summary>
    /// Whether <paramref name="dictionary"/> declares <c>/Type</c> <paramref name="expected"/>, on
    /// any of its <c>/Type</c> keys.
    /// </summary>
    private static bool HasTypeName(ReadOnlySpan<byte> dictionary, ReadOnlySpan<byte> expected)
    {
        var offset = 0;

        while (offset < dictionary.Length)
        {
            var at = dictionary[offset..].IndexOf("/Type"u8);
            if (at < 0)
            {
                return false;
            }

            var position = offset + at;
            offset = position + 5;

            if (IsName(dictionary, offset, expected))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the value at <paramref name="index"/> is the name <paramref name="expected"/> - and
    /// exactly it, so <c>/Pages</c> is not read as <c>/Page</c>.
    /// </summary>
    private static bool IsName(ReadOnlySpan<byte> window, int index, ReadOnlySpan<byte> expected)
    {
        SkipWhitespace(window, ref index);
        if (index >= window.Length || window[index] != (byte)'/')
        {
            return false;
        }

        index++;
        if (!window[index..].StartsWith(expected))
        {
            return false;
        }

        // A PDF name ends at whitespace or a delimiter. Without this, /Page matches /Pages.
        var after = index + expected.Length;

        return after >= window.Length || IsNameBoundary(window[after]);
    }

    /// <summary>At most <paramref name="length"/> bytes of <paramref name="content"/> from <paramref name="start"/>.</summary>
    private static ReadOnlySpan<byte> Window(
        ReadOnlySpan<byte> content,
        int start,
        int length = MaxReferenceBytes) =>
        content.Slice(start, Math.Min(length, content.Length - start));

    /// <summary>Whether the byte at <paramref name="index"/> ends the token before it.</summary>
    private static bool IsDelimited(ReadOnlySpan<byte> content, int index) =>
        index >= content.Length || IsNameBoundary(content[index]);

    /// <summary>
    /// Reads the non-negative integer at <paramref name="index"/>, leaves <paramref name="index"/>
    /// just past it, and refuses one too long to be an object number.
    /// </summary>
    private static bool TryInteger(ReadOnlySpan<byte> window, ref int index, out int value)
    {
        value = 0;
        var digits = 0;

        while (index < window.Length && IsDigit(window[index]) && digits < 9)
        {
            value = (value * 10) + (window[index] - (byte)'0');
            index++;
            digits++;
        }

        return digits > 0 && IsDelimited(window, index);
    }

    /// <summary>
    /// Reads the non-negative integer ending at <paramref name="index"/> and leaves
    /// <paramref name="index"/> just before it.
    /// </summary>
    private static bool TryIntegerBackwards(ReadOnlySpan<byte> content, ref int index, out int value)
    {
        value = 0;
        var last = index;

        while (index >= 0 && IsDigit(content[index]) && last - index < 9)
        {
            index--;
        }

        var digits = last - index;
        if (digits is 0)
        {
            return false;
        }

        var at = 0;

        return TryInteger(content.Slice(index + 1, digits), ref at, out value);
    }

    /// <summary>Whether any whitespace was skipped, forwards.</summary>
    private static bool SkipWhitespace(ReadOnlySpan<byte> window, ref int index)
    {
        var start = index;
        while (index < window.Length && IsWhitespace(window[index]))
        {
            index++;
        }

        return index > start;
    }

    /// <summary>Whether any whitespace was skipped, backwards.</summary>
    private static bool SkipWhitespaceBackwards(ReadOnlySpan<byte> content, ref int index)
    {
        var start = index;
        while (index >= 0 && IsWhitespace(content[index]))
        {
            index--;
        }

        return index < start;
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0 or 12;

    /// <summary>
    /// Whether <paramref name="value"/> ends a PDF name or number: whitespace, or one of the
    /// delimiters a name may not contain.
    /// </summary>
    private static bool IsNameBoundary(byte value) =>
        IsWhitespace(value)
        || value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>One page-tree node still to visit, and how deep it sits.</summary>
    private readonly record struct PageNode(int Number, int Depth);

    /// <summary>
    /// Where each numbered object's dictionary is, and which numbers the file defines more than once.
    ///
    /// <para>A number defined twice is an object this parser cannot resolve: which definition is
    /// current is the cross-reference table's answer, and an incremental update can replace a page
    /// tree node. It is treated as undefined, so the page tree that reaches it is refused rather than
    /// counted from the wrong generation.</para>
    /// </summary>
    private sealed class ObjectIndex
    {
        private readonly Dictionary<int, (int Start, int Length)> _dictionaries = new();
        private readonly HashSet<int> _definedTwice = new();

        /// <summary>How many distinct object numbers have been indexed.</summary>
        public int Count => _dictionaries.Count;

        /// <summary>Records object <paramref name="number"/>'s dictionary range.</summary>
        public void Add(int number, int start, int length)
        {
            if (!_dictionaries.TryAdd(number, (start, length)))
            {
                _definedTwice.Add(number);
            }
        }

        /// <summary>
        /// The dictionary of object <paramref name="number"/>, or <see langword="false"/> when the
        /// file does not define it exactly once.
        /// </summary>
        public bool TryDictionary(ReadOnlySpan<byte> content, int number, out ReadOnlySpan<byte> dictionary)
        {
            dictionary = default;

            if (_definedTwice.Contains(number) || !_dictionaries.TryGetValue(number, out var range))
            {
                return false;
            }

            dictionary = content.Slice(range.Start, range.Length);

            return true;
        }
    }
}
