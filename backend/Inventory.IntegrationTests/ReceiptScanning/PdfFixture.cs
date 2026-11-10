using System.Globalization;
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
    private static byte[] Assemble(int root, IReadOnlyList<string> objects)
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
            $"trailer\n<< /Size {objects.Count + 1} /Root {root} 0 R >>\nstartxref\n{startxref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
