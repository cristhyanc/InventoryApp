using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace InventoryApi.Tests.ReceiptScanning;

/// <summary>
/// Builds the PDF documents the page-count tests need (issue #1163).
///
/// <para><strong>They are real PDF files, not fragments.</strong> <see cref="Flat"/> and
/// <see cref="Nested"/> write a header, numbered objects, a cross-reference table with byte-accurate
/// offsets, a trailer naming the catalogue and <c>startxref</c>/<c>%%EOF</c> - a document a PDF reader
/// opens and whose pages it finds - which is what makes "it declares one page and really has a
/// hundred and one" a test of the counter rather than a test of a string. <see cref="Malformed"/>
/// writes the deliberately broken ones, where a cross-reference table would prove nothing.</para>
///
/// <para>Every document here is invented and carries no receipt content at all: a real receipt is
/// never committed as a fixture (issue #1163's known constraints).</para>
/// </summary>
internal static class PdfFixture
{
    /// <summary>One empty page, parented to the flat page tree at object 2.</summary>
    private const string PageObject =
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> >>";

    /// <summary>
    /// A document whose catalogue (object 1) points at one page-tree node (object 2) listing
    /// <paramref name="pages"/> page leaves.
    /// </summary>
    /// <param name="pages">How many page leaves the tree really reaches.</param>
    /// <param name="declaredCount">
    /// What the node's <c>/Count</c> claims, when that is deliberately not the truth. The count a
    /// conforming writer would write by default.
    /// </param>
    public static byte[] Flat(int pages, int? declaredCount = null)
    {
        var kids = Kids(Enumerable.Range(3, pages));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            Invariant($"<< /Type /Pages /Count {declaredCount ?? pages} /Kids [{kids}] >>"),
        };

        objects.AddRange(Enumerable.Repeat(PageObject, pages));

        return Assemble(1, objects);
    }

    /// <summary>
    /// A document whose page tree nests: the root node lists <paramref name="subTrees"/> child nodes,
    /// each listing <paramref name="pagesPerSubTree"/> page leaves. Every node declares
    /// <paramref name="declaredCount"/> when one is given, which is how a linearised file's repeated
    /// counts are imitated.
    /// </summary>
    public static byte[] Nested(int subTrees, int pagesPerSubTree, int? declaredCount = null)
    {
        var pages = subTrees * pagesPerSubTree;
        var firstSubTree = 3;
        var firstPage = firstSubTree + subTrees;

        var rootKids = Kids(Enumerable.Range(firstSubTree, subTrees));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            Invariant($"<< /Type /Pages /Count {declaredCount ?? pages} /Kids [{rootKids}] >>"),
        };

        for (var subTree = 0; subTree < subTrees; subTree++)
        {
            var leaves = Enumerable.Range(firstPage + (subTree * pagesPerSubTree), pagesPerSubTree);
            objects.Add(Invariant(
                $"<< /Type /Pages /Count {declaredCount ?? pagesPerSubTree} /Kids [{Kids(leaves)}] >>"));
        }

        objects.AddRange(Enumerable.Repeat(PageObject, pages));

        return Assemble(1, objects);
    }

    /// <summary>
    /// A complete document - header, the objects numbered from 1 in order, a cross-reference table
    /// with real offsets and a trailer naming <paramref name="root"/> - built from the objects given,
    /// for the structurally broken page trees whose cross-reference information is itself sound.
    /// </summary>
    /// <param name="root">The object number the trailer's <c>/Root</c> names.</param>
    /// <param name="objects">The objects' bodies, numbered from 1.</param>
    public static byte[] Document(int root, params string[] objects) => Assemble(root, objects);

    /// <summary>
    /// A complete document whose trailer carries <paramref name="trailerEntries"/> beside
    /// <c>/Size</c> and <c>/Root</c> - how a hybrid-reference (<c>/XRefStm</c>) or encrypted
    /// (<c>/Encrypt</c>) file is imitated.
    /// </summary>
    public static byte[] WithTrailerEntries(int pages, string trailerEntries)
    {
        var kids = Kids(Enumerable.Range(3, pages));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            Invariant($"<< /Type /Pages /Count {pages} /Kids [{kids}] >>"),
        };

        objects.AddRange(Enumerable.Repeat(PageObject, pages));

        return Assemble(1, objects, trailerEntries);
    }

    /// <summary>
    /// <paramref name="original"/> with an ordinary incremental update appended: the objects given,
    /// a classic cross-reference section listing them, and a trailer whose <c>/Prev</c> names the
    /// original's section - what a conforming writer produces when it saves a change.
    /// </summary>
    /// <param name="original">The document being updated.</param>
    /// <param name="objects">Each new or replaced object, by number.</param>
    public static byte[] IncrementalUpdate(byte[] original, IReadOnlyDictionary<int, string> objects)
    {
        var text = new StringBuilder(Encoding.ASCII.GetString(original));
        var previous = StartXref(original);
        var offsets = new SortedDictionary<int, int>();

        foreach (var (number, body) in objects)
        {
            offsets[number] = text.Length;
            text.Append(CultureInfo.InvariantCulture, $"{number} 0 obj\n{body}\nendobj\n");
        }

        var startxref = text.Length;
        text.Append("xref\n");

        foreach (var (number, offset) in offsets)
        {
            text.Append(CultureInfo.InvariantCulture, $"{number} 1\n{offset:D10} 00000 n \n");
        }

        var size = Math.Max(offsets.Keys.Max(), SizeOf(original) - 1) + 1;
        text.Append(
            CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {size} /Root 1 0 R /Prev {previous} >>\nstartxref\n{startxref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>
    /// <see cref="Flat"/>'s <paramref name="original"/>-page document with an ordinary
    /// incremental update that replaces the page tree (object 2) with one reaching
    /// <paramref name="active"/> pages, adding any new page leaves after the original ones.
    /// </summary>
    public static byte[] Updated(int original, int active)
    {
        var firstNew = 3 + original;
        var leaves = Enumerable.Range(3, Math.Min(original, active))
            .Concat(Enumerable.Range(firstNew, Math.Max(0, active - original)))
            .ToList();
        var objects = new Dictionary<int, string>
        {
            [2] = Invariant($"<< /Type /Pages /Count {active} /Kids [{string.Join(' ', leaves.Select(n => Invariant($"{n} 0 R")))}] >>"),
        };

        foreach (var number in Enumerable.Range(firstNew, Math.Max(0, active - original)))
        {
            objects[number] = PageObject;
        }

        return IncrementalUpdate(Flat(original), objects);
    }

    /// <summary>
    /// The page-tree replacement a raw-byte scan cannot see: <see cref="Flat"/>'s
    /// <paramref name="originalPages"/>-page document with an incremental update appended that
    /// replaces the page tree (object 2) through a real <strong>cross-reference stream</strong>, the
    /// replacement node kept inside a Flate-compressed <strong>object stream</strong> and listing
    /// <paramref name="activePages"/> pages. The new page leaves are plain objects.
    ///
    /// <para>Object 2 is therefore defined exactly once in the plain bytes - as the original, smaller
    /// tree - and both trailers name the same catalogue, while a conforming reader resolves the
    /// cross-reference stream and sees the larger tree.</para>
    /// </summary>
    public static byte[] CrossReferenceStreamUpdate(int originalPages, int activePages)
    {
        var original = Flat(originalPages);
        var previous = StartXref(original);
        var file = new MemoryStream();
        file.Write(original);

        var firstNew = 3 + originalPages;
        var newPages = Enumerable.Range(firstNew, activePages - originalPages).ToList();
        var pageOffsets = new List<int>();

        foreach (var number in newPages)
        {
            pageOffsets.Add((int)file.Length);
            WriteAscii(file, Invariant($"{number} 0 obj\n{PageObject}\nendobj\n"));
        }

        // The object stream holding the replacement page tree: a header of "number offset" pairs,
        // then the object itself, compressed.
        var objectStream = newPages.Count == 0 ? firstNew : newPages[^1] + 1;
        var tree = Invariant(
            $"<< /Type /Pages /Count {activePages} /Kids [{Kids(Enumerable.Range(3, originalPages).Concat(newPages))}] >>");
        var header = "2 0 ";
        var packed = Deflate(Encoding.ASCII.GetBytes(header + tree));
        var objectStreamOffset = (int)file.Length;
        WriteAscii(file, Invariant(
            $"{objectStream} 0 obj\n<< /Type /ObjStm /N 1 /First {header.Length} /Filter /FlateDecode /Length {packed.Length} >>\nstream\n"));
        file.Write(packed);
        WriteAscii(file, "\nendstream\nendobj\n");

        // The cross-reference stream: /W [1 4 2]. Object 2 is a type-2 entry (in object stream
        // objectStream, at index 0) - the entry a classic table cannot express - and every new plain
        // object, the object stream and this stream itself are type-1 entries at their offsets.
        var xrefStream = objectStream + 1;
        var xrefOffset = (int)file.Length;
        var rows = new MemoryStream();
        WriteRow(rows, 2, objectStream, 0);

        foreach (var offset in pageOffsets)
        {
            WriteRow(rows, 1, offset, 0);
        }

        WriteRow(rows, 1, objectStreamOffset, 0);
        WriteRow(rows, 1, xrefOffset, 0);

        var entries = Deflate(rows.ToArray());
        WriteAscii(file, Invariant(
            $"{xrefStream} 0 obj\n<< /Type /XRef /Size {xrefStream + 1} /Root 1 0 R /Prev {previous} /W [1 4 2] /Index [2 1 {firstNew} {xrefStream - firstNew + 1}] /Filter /FlateDecode /Length {entries.Length} >>\nstream\n"));
        file.Write(entries);
        WriteAscii(file, Invariant($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n"));

        return file.ToArray();
    }

    /// <summary>The offset the document's last <c>startxref</c> gives.</summary>
    public static int StartXref(byte[] document)
    {
        var text = Encoding.Latin1.GetString(document);
        var at = text.LastIndexOf("startxref\n", StringComparison.Ordinal);

        return int.Parse(text[(at + 10)..].Split('\n')[0], CultureInfo.InvariantCulture);
    }

    /// <summary>Zlib-wrapped Flate, which is what a PDF's <c>/FlateDecode</c> filter reads.</summary>
    public static byte[] Inflate(byte[] packed)
    {
        using var input = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);

        return output.ToArray();
    }

    /// <summary>
    /// A document whose objects are written out verbatim, with a trailer naming
    /// <paramref name="root"/> as the catalogue and no cross-reference table. For the documents a
    /// reader is expected to refuse, where the point is the broken structure itself.
    /// </summary>
    /// <param name="root">The object number the trailer's <c>/Root</c> names.</param>
    /// <param name="objects">The objects, written as the file holds them.</param>
    public static byte[] Malformed(int root, string objects) =>
        Encoding.ASCII.GetBytes(
            Invariant($"%PDF-1.7\n{objects}trailer\n<< /Root {root} 0 R >>\n%%EOF\n"));

    /// <summary>The same bytes a hand-written document is given to the counter as.</summary>
    public static byte[] Raw(string content) => Encoding.ASCII.GetBytes(content);

    /// <summary>A <c>/Kids</c> array's references, in order.</summary>
    private static string Kids(IEnumerable<int> numbers) =>
        string.Join(' ', numbers.Select(number => Invariant($"{number} 0 R")));

    /// <summary>
    /// The complete file: the objects in order from number 1, a cross-reference table with each
    /// object's real byte offset, and a trailer naming <paramref name="root"/>.
    /// </summary>
    private static byte[] Assemble(int root, IReadOnlyList<string> objects, string trailerEntries = "")
    {
        // Every byte written here is ASCII, so the text's length is the file's offset.
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>(objects.Count);

        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(text.Length);
            text.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var startxref = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n");
        text.Append("0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        text.Append(
            CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root {root} 0 R {trailerEntries}>>\nstartxref\n{startxref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static int SizeOf(byte[] document)
    {
        var text = Encoding.ASCII.GetString(document);
        var at = text.LastIndexOf("/Size ", StringComparison.Ordinal);

        return int.Parse(text[(at + 6)..].Split(' ')[0], CultureInfo.InvariantCulture);
    }

    private static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(bytes);
        }

        return output.ToArray();
    }

    private static void WriteRow(MemoryStream rows, byte type, int field2, int field3)
    {
        rows.WriteByte(type);
        rows.WriteByte((byte)(field2 >> 24));
        rows.WriteByte((byte)(field2 >> 16));
        rows.WriteByte((byte)(field2 >> 8));
        rows.WriteByte((byte)field2);
        rows.WriteByte((byte)(field3 >> 8));
        rows.WriteByte((byte)field3);
    }

    private static void WriteAscii(MemoryStream file, string text) => file.Write(Encoding.ASCII.GetBytes(text));

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
