using Inventory.Application.ReceiptScanning;

namespace Inventory.Infrastructure.ReceiptScanning;

/// <summary>
/// Verifies how many pages a PDF really has (issue #1163), so a document over the reader's page limit
/// is refused before anything is uploaded.
///
/// <para><strong>It counts the pages the document's current version can actually reach, and trusts
/// nothing the document declares.</strong> The count is the number of <c>/Type /Page</c> leaves
/// reachable from the catalogue the active trailer's <c>/Root</c> names, through the <c>/Kids</c> of
/// each <c>/Type /Pages</c> node. A <c>/Count</c> written on a page-tree node is <strong>not read at
/// all</strong>: it is the document's own claim about itself (the first repair of 10 November
/// 2026).</para>
///
/// <para><strong>Which definition of an object is current is the cross-reference table's answer, never
/// the bytes'.</strong> The parser starts at the file's last <c>startxref</c>, reads that
/// cross-reference section, and follows its <c>/Prev</c> chain backwards, the newest entry for an
/// object number winning - which is exactly the object a conforming reader resolves. Every reference is
/// resolved through that table alone, and an entry must point at the object header it claims (number
/// and generation) or it resolves to nothing. Scanning the bytes for object headers instead would let an
/// incremental update replace the page tree somewhere the scan cannot see - in a compressed object
/// stream named by a cross-reference stream - while the scan walks the stale, smaller tree (the second
/// repair of 10 November 2026).</para>
///
/// <para><strong>One format is supported: a classic cross-reference table.</strong> A cross-reference
/// stream (PDF 1.5's compressed form), a hybrid-reference file (<c>/XRefStm</c>) and an encrypted
/// document (<c>/Encrypt</c>) are refused, because their active objects cannot be verified without
/// decompressing or decrypting, which this parser deliberately does not do.</para>
///
/// <para><strong>It is a bounded structural parser, not a PDF library.</strong> Every step has a
/// ceiling - cross-reference entries, revisions, subsections, the bytes of one dictionary, the tree's
/// depth and width, the nodes visited and the pages counted - so a hostile file cannot turn the count
/// into unbounded work or memory. A document past any ceiling is refused, never truncated into a
/// smaller count.</para>
///
/// <para><strong>Anything it cannot verify counts as nothing, and
/// <c>Inventory.Domain.ReceiptScanning.ReceiptScanFilePolicy.CheckPdfPages</c> refuses it</strong> with
/// "try a photo of the receipt instead" - a refusal the operator can work around, never a page limit
/// silently bypassed.</para>
/// </summary>
public sealed class PdfPageCounter : IPdfPageCounter
{
    /// <summary>
    /// How many cross-reference entries this parser will read across every revision. Far above a
    /// receipt PDF, and small enough that a hostile file cannot make the table itself expensive.
    /// </summary>
    private const int MaxEntries = 20_000;

    /// <summary>How many cross-reference sections (revisions) the <c>/Prev</c> chain may hold.</summary>
    private const int MaxRevisions = 32;

    /// <summary>How many subsections one cross-reference section may hold.</summary>
    private const int MaxSubsections = 1_024;

    /// <summary>
    /// How far past an object's <c>obj</c> keyword its dictionary is read, and how far past
    /// <c>trailer</c> the trailer dictionary is. Generous for a dictionary, including a page-tree node
    /// listing thousands of children, and bounded so no scan becomes quadratic.
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

    /// <summary>How far past <c>startxref</c> its offset is looked for.</summary>
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

        var table = CrossReferenceTable.Read(content);
        if (table is null || !TryPageTreeRoot(content, table, out var root))
        {
            return null;
        }

        var pages = ReachablePages(content, table, root);

        return pages > 0 ? pages : null;
    }

    /// <summary>
    /// The page tree's root, taken from the catalogue the active trailer's <c>/Root</c> names and
    /// resolved through the cross-reference table.
    /// </summary>
    private static bool TryPageTreeRoot(ReadOnlySpan<byte> content, CrossReferenceTable table, out ObjectReference root)
    {
        root = default;

        return table.TryDictionary(content, table.Root, out var dictionary)
            && HasTypeName(dictionary, "Catalog"u8)
            && TryReference(dictionary, "/Pages"u8, out root);
    }

    /// <summary>
    /// How many <c>/Type /Page</c> leaves <paramref name="root"/> reaches, or <see langword="null"/>
    /// when the tree cannot be walked to the end within this parser's bounds.
    /// </summary>
    private static int? ReachablePages(ReadOnlySpan<byte> content, CrossReferenceTable table, ObjectReference root)
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
            if (node.Depth > MaxDepth || visited.Count >= MaxNodes || !visited.Add(node.Reference.Number))
            {
                return null;
            }

            if (!table.TryDictionary(content, node.Reference, out var dictionary))
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
        var at = FindKey(dictionary, "/Kids"u8);
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

            if (kids >= MaxKids || !TryReferenceAt(dictionary, ref index, out var reference))
            {
                return false;
            }

            pending.Push(new PageNode(reference, depth));
            kids++;
        }
    }

    /// <summary>
    /// The reference beside <paramref name="key"/>, or <see langword="false"/> when there is no such
    /// key or its value is not a reference.
    /// </summary>
    private static bool TryReference(ReadOnlySpan<byte> window, ReadOnlySpan<byte> key, out ObjectReference reference)
    {
        reference = default;
        var at = FindKey(window, key);
        if (at < 0)
        {
            return false;
        }

        var index = at + key.Length;
        SkipWhitespace(window, ref index);

        return TryReferenceAt(window, ref index, out reference);
    }

    /// <summary>
    /// The non-negative integer beside <paramref name="key"/>, or <see langword="false"/> when there
    /// is no such key or its value is not one.
    /// </summary>
    private static bool TryIntegerValue(ReadOnlySpan<byte> window, ReadOnlySpan<byte> key, out int value)
    {
        value = 0;
        var at = FindKey(window, key);
        if (at < 0)
        {
            return false;
        }

        var index = at + key.Length;
        SkipWhitespace(window, ref index);

        return TryInteger(window, ref index, out value);
    }

    /// <summary>
    /// Where the name <paramref name="key"/> first appears in <paramref name="window"/> as a whole
    /// name - so <c>/Prev</c> is not found inside <c>/PrevX</c> - or -1.
    /// </summary>
    private static int FindKey(ReadOnlySpan<byte> window, ReadOnlySpan<byte> key)
    {
        var offset = 0;

        while (offset < window.Length)
        {
            var at = window[offset..].IndexOf(key);
            if (at < 0)
            {
                return -1;
            }

            var position = offset + at;
            if (IsDelimited(window, position + key.Length))
            {
                return position;
            }

            offset = position + key.Length;
        }

        return -1;
    }

    /// <summary>
    /// Reads the reference - <c>N G R</c> - at <paramref name="index"/> and leaves
    /// <paramref name="index"/> just past it.
    /// </summary>
    private static bool TryReferenceAt(ReadOnlySpan<byte> window, ref int index, out ObjectReference reference)
    {
        reference = default;

        if (!TryInteger(window, ref index, out var number)
            || !SkipWhitespace(window, ref index)
            || !TryInteger(window, ref index, out var generation)
            || !SkipWhitespace(window, ref index)
            || index >= window.Length
            || window[index] != (byte)'R'
            || !IsDelimited(window, index + 1))
        {
            return false;
        }

        index++;
        reference = new ObjectReference(number, generation);

        return true;
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
        return IsDelimited(window, index + expected.Length);
    }

    /// <summary>At most <paramref name="length"/> bytes of <paramref name="content"/> from <paramref name="start"/>.</summary>
    private static ReadOnlySpan<byte> Window(ReadOnlySpan<byte> content, int start, int length) =>
        content.Slice(start, Math.Min(length, content.Length - start));

    /// <summary>Whether the byte at <paramref name="index"/> ends the token before it.</summary>
    private static bool IsDelimited(ReadOnlySpan<byte> content, int index) =>
        index >= content.Length || IsNameBoundary(content[index]);

    /// <summary>
    /// Reads the non-negative integer at <paramref name="index"/>, leaves <paramref name="index"/>
    /// just past it, and refuses one with more than <paramref name="maxDigits"/> digits.
    /// </summary>
    private static bool TryInteger(ReadOnlySpan<byte> window, ref int index, out int value, int maxDigits = 9)
    {
        value = 0;
        var digits = 0;
        long total = 0;

        while (index < window.Length && IsDigit(window[index]))
        {
            if (++digits > maxDigits)
            {
                return false;
            }

            total = (total * 10) + (window[index] - (byte)'0');
            index++;
        }

        if (digits is 0 || total > int.MaxValue || !IsDelimited(window, index))
        {
            return false;
        }

        value = (int)total;

        return true;
    }

    /// <summary>Whether any whitespace was skipped.</summary>
    private static bool SkipWhitespace(ReadOnlySpan<byte> window, ref int index)
    {
        var start = index;
        while (index < window.Length && IsWhitespace(window[index]))
        {
            index++;
        }

        return index > start;
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

    /// <summary>An indirect reference: <c>N G R</c>.</summary>
    private readonly record struct ObjectReference(int Number, int Generation);

    /// <summary>One page-tree node still to visit, and how deep it sits.</summary>
    private readonly record struct PageNode(ObjectReference Reference, int Depth);

    /// <summary>One cross-reference entry: where an in-use object starts, or that it is free.</summary>
    private readonly record struct Entry(int Offset, int Generation, bool InUse);

    /// <summary>
    /// The document's active cross-reference information: for each object number, the newest entry
    /// any revision gives it, and the catalogue the newest trailer names.
    /// </summary>
    private sealed class CrossReferenceTable
    {
        private readonly Dictionary<int, Entry> _entries = new();
        private int _entriesRead;

        private CrossReferenceTable()
        {
        }

        /// <summary>The catalogue the active (newest) trailer names.</summary>
        public ObjectReference Root { get; private set; }

        /// <summary>
        /// Reads the cross-reference sections from the last <c>startxref</c> back along the
        /// <c>/Prev</c> chain, or answers <see langword="null"/> when any of them is not a classic
        /// table this parser can verify.
        /// </summary>
        public static CrossReferenceTable? Read(ReadOnlySpan<byte> content)
        {
            if (!TryLastStartXref(content, out var section))
            {
                return null;
            }

            var table = new CrossReferenceTable();
            var visited = new HashSet<int>();
            int? next = section;

            while (next is { } offset)
            {
                // A chain that loops, or runs longer than any real document's, is refused.
                if (offset >= content.Length
                    || visited.Count >= MaxRevisions
                    || !visited.Add(offset)
                    || !table.TryReadSection(content, offset, out var trailer))
                {
                    return null;
                }

                // A hybrid file keeps part of its active graph in a cross-reference stream, and an
                // encrypted one cannot be read without decrypting it: neither can be verified here.
                if (FindKey(trailer, "/XRefStm"u8) >= 0 || FindKey(trailer, "/Encrypt"u8) >= 0)
                {
                    return null;
                }

                if (visited.Count == 1)
                {
                    // Only the active trailer names the catalogue; an older one is history.
                    if (!TryReference(trailer, "/Root"u8, out var root))
                    {
                        return null;
                    }

                    table.Root = root;
                }

                next = null;
                if (FindKey(trailer, "/Prev"u8) >= 0)
                {
                    if (!TryIntegerValue(trailer, "/Prev"u8, out var previous))
                    {
                        return null;
                    }

                    next = previous;
                }
            }

            return table;
        }

        /// <summary>
        /// The dictionary of the object <paramref name="reference"/> names, resolved through this
        /// table: its entry must be in use, inside the file, of the same generation, and point at the
        /// header <c>N G obj</c> of that very object. Anything else resolves to nothing.
        /// </summary>
        public bool TryDictionary(ReadOnlySpan<byte> content, ObjectReference reference, out ReadOnlySpan<byte> dictionary)
        {
            dictionary = default;

            if (!_entries.TryGetValue(reference.Number, out var entry)
                || !entry.InUse
                || entry.Generation != reference.Generation
                || entry.Offset >= content.Length)
            {
                return false;
            }

            var index = entry.Offset;
            if (!TryInteger(content, ref index, out var number)
                || number != reference.Number
                || !SkipWhitespace(content, ref index)
                || !TryInteger(content, ref index, out var generation)
                || generation != reference.Generation
                || !SkipWhitespace(content, ref index)
                || !content[index..].StartsWith("obj"u8)
                || !IsDelimited(content, index + 3))
            {
                return false;
            }

            var start = index + 3;
            dictionary = content.Slice(start, DictionaryLength(content, start));

            return true;
        }

        /// <summary>The offset the file's last <c>startxref</c> gives, when it is inside the file.</summary>
        private static bool TryLastStartXref(ReadOnlySpan<byte> content, out int offset)
        {
            offset = 0;
            var at = content.LastIndexOf("startxref"u8);
            if (at < 0)
            {
                return false;
            }

            var window = Window(content, at + 9, MaxReferenceBytes);
            var index = 0;

            return SkipWhitespace(window, ref index)
                && TryInteger(window, ref index, out offset)
                && offset < content.Length;
        }

        /// <summary>
        /// Reads the classic cross-reference section at <paramref name="offset"/> - the <c>xref</c>
        /// keyword, its subsections and the trailer after them - recording each entry unless a newer
        /// revision already gave that object number one. A cross-reference stream (an object where
        /// <c>xref</c> should be) is not read and answers <see langword="false"/>.
        /// </summary>
        private bool TryReadSection(ReadOnlySpan<byte> content, int offset, out ReadOnlySpan<byte> trailer)
        {
            trailer = default;
            var index = offset;
            SkipWhitespace(content, ref index);

            if (!content[index..].StartsWith("xref"u8) || !IsDelimited(content, index + 4))
            {
                return false;
            }

            index += 4;

            for (var subsections = 0; ; subsections++)
            {
                SkipWhitespace(content, ref index);
                if (content[index..].StartsWith("trailer"u8))
                {
                    index += 7;
                    break;
                }

                if (subsections >= MaxSubsections || !TryReadSubsection(content, ref index))
                {
                    return false;
                }
            }

            SkipWhitespace(content, ref index);
            if (!content[index..].StartsWith("<<"u8))
            {
                return false;
            }

            // A trailer is followed by the startxref of its own revision, which bounds it.
            var window = Window(content, index, MaxDictionaryBytes);
            var end = window.IndexOf("startxref"u8);
            if (end < 0)
            {
                return false;
            }

            trailer = window[..end];

            return true;
        }

        /// <summary>
        /// Reads one subsection - <c>first count</c>, then <c>count</c> entries of
        /// <c>offset generation n|f</c> - leaving <paramref name="index"/> just past it.
        /// </summary>
        private bool TryReadSubsection(ReadOnlySpan<byte> content, ref int index)
        {
            if (!TryInteger(content, ref index, out var first)
                || !SkipWhitespace(content, ref index)
                || !TryInteger(content, ref index, out var count)
                || count > MaxEntries - _entriesRead)
            {
                return false;
            }

            for (var position = 0; position < count; position++)
            {
                SkipWhitespace(content, ref index);
                if (!TryInteger(content, ref index, out var objectOffset, maxDigits: 10)
                    || !SkipWhitespace(content, ref index)
                    || !TryInteger(content, ref index, out var generation, maxDigits: 5)
                    || !SkipWhitespace(content, ref index)
                    || index >= content.Length
                    || (content[index] != (byte)'n' && content[index] != (byte)'f')
                    || !IsDelimited(content, index + 1))
                {
                    return false;
                }

                var inUse = content[index] == (byte)'n';
                index++;
                _entriesRead++;

                // Newer revisions are read first, so an entry already recorded is the current one.
                _entries.TryAdd(first + position, new Entry(objectOffset, generation, inUse));
            }

            return true;
        }
    }
}
