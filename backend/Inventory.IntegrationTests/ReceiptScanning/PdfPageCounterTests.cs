using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Inventory.Infrastructure.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.ReceiptScanning;

/// <summary>
/// Counting a PDF's pages before anything is uploaded (issue #1163).
///
/// <para><strong>The counter answers what the document can reach, not what it declares.</strong> The
/// first two tests here are the repair of 10 November 2026: a document that writes <c>/Count 1</c> on
/// its page tree while really reaching a hundred and one pages is counted as a hundred and one, and a
/// document that overstates its count is counted as what it holds. The declared count is read
/// nowhere.</para>
///
/// <para>The valid documents are complete PDF files - header, numbered objects, a cross-reference
/// table with real offsets, a trailer naming the catalogue - built by <see cref="PdfFixture"/>. The
/// refused ones are deliberately broken. Every one of them is invented: a real receipt is never
/// committed as a fixture (issue #1163's known constraints).</para>
/// </summary>
public class PdfPageCounterTests
{
    private readonly PdfPageCounter _counter = new();

    /// <summary>
    /// The fixture is a complete PDF file rather than a fragment: it starts with the header, ends
    /// with <c>%%EOF</c>, names its catalogue in a trailer, and every cross-reference offset points
    /// at the object header it claims. That is what makes "the document declares one page and really
    /// reaches a hundred and one" a statement about a file a PDF reader opens, and so a statement
    /// about what the reader at the other end would have been sent.
    /// </summary>
    [Fact]
    public void The_fixture_is_a_complete_pdf_with_real_cross_reference_offsets()
    {
        var text = Encoding.ASCII.GetString(PdfFixture.Flat(3));

        Assert.StartsWith("%PDF-", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains("/Root 1 0 R", text, StringComparison.Ordinal);

        var startxref = text.LastIndexOf("startxref\n", StringComparison.Ordinal);
        Assert.True(startxref > 0, "the file records where its cross-reference table is");

        var table = int.Parse(text[(startxref + 10)..].Split('\n')[0], CultureInfo.InvariantCulture);
        var entries = text[table..].Split('\n');

        Assert.Equal("xref", entries[0]);

        // The catalogue, the page tree and three pages, after the free entry every table starts with.
        Assert.Equal("0 6", entries[1]);

        for (var number = 1; number <= 5; number++)
        {
            // Entry 0 is the free one the table opens with, so object N's entry is the line after it.
            var offset = int.Parse(entries[number + 2][..10], CultureInfo.InvariantCulture);

            Assert.StartsWith(
                FormattableString.Invariant($"{number} 0 obj"), text[offset..], StringComparison.Ordinal);
        }
    }

    /// <summary>The pages the document's own page tree reaches are the answer.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(120)]
    public void The_pages_the_document_reaches_are_counted(int pages) =>
        Assert.Equal(pages, _counter.Count(PdfFixture.Flat(pages)));

    /// <summary>
    /// The security regression: a page count the document declares is not evidence of anything. A
    /// receipt PDF that says it has one page and reaches a hundred and one is a hundred and one -
    /// over the configured limit, so <c>ReceiptScanFilePolicy.CheckPdfPages</c> refuses it and the
    /// whole document is never forwarded to the reader.
    /// </summary>
    [Theory]
    [InlineData(101, 1)]
    [InlineData(2, 500)]
    [InlineData(4, 0)]
    public void A_count_the_document_declares_is_not_the_answer(int pages, int declared) =>
        Assert.Equal(pages, _counter.Count(PdfFixture.Flat(pages, declaredCount: declared)));

    /// <summary>
    /// A nested page tree is walked to its leaves, and the counts its nodes repeat - what a
    /// linearised file writes - change nothing.
    /// </summary>
    [Fact]
    public void A_nested_page_tree_is_walked_to_its_leaves() =>
        Assert.Equal(
            12,
            _counter.Count(PdfFixture.Nested(subTrees: 3, pagesPerSubTree: 4, declaredCount: 1)));

    /// <summary>
    /// The counter's own ceiling, both sides of it: a document is counted up to four thousand and
    /// ninety-six pages, and one with more is refused as uncountable rather than counted in part.
    /// Both answers are far above the reader's documented hundred-page limit.
    /// </summary>
    [Fact]
    public void A_document_with_more_pages_than_the_counter_will_verify_counts_as_nothing()
    {
        Assert.Equal(4096, _counter.Count(PdfFixture.Flat(4096)));
        Assert.Null(_counter.Count(PdfFixture.Flat(4097, declaredCount: 1)));
    }

    /// <summary>
    /// A file that is not a PDF counts as nothing, whatever it is named - which the file policy then
    /// refuses, rather than uploading it whole in the hope the reader manages.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not a pdf at all")]
    [InlineData("\u0089PNG\r\n\u001a\n")]
    [InlineData("<html><body>receipt</body></html>")]
    public void A_file_that_is_not_a_pdf_counts_as_nothing(string content) =>
        Assert.Null(_counter.Count(PdfFixture.Raw(content)));

    /// <summary>
    /// An encrypted PDF counts as nothing: its objects cannot be verified without decrypting them.
    /// That is the conservative direction: a refusal the operator can work around, not a page limit
    /// silently bypassed.
    /// </summary>
    [Fact]
    public void An_encrypted_pdf_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.WithTrailerEntries(3, "/Encrypt 9 0 R ")));

    /// <summary>
    /// A hybrid-reference file keeps part of its active graph in a cross-reference stream the trailer's
    /// <c>/XRefStm</c> names, so its active objects cannot be verified from the classic table alone.
    /// </summary>
    [Fact]
    public void A_hybrid_reference_pdf_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.WithTrailerEntries(3, "/XRefStm 9999 ")));

    /// <summary>
    /// The second repair of 10 November 2026: an incremental update is resolved to its active page
    /// tree, in both directions - a two-page document whose update makes it a hundred and one is a
    /// hundred and one, and a three-page one whose update drops a page is two. Neither the stale tree
    /// nor the newest-looking bytes decide anything; the cross-reference table does.
    /// </summary>
    [Theory]
    [InlineData(2, 101)]
    [InlineData(3, 2)]
    public void An_incremental_update_is_counted_from_its_active_page_tree(int original, int active) =>
        Assert.Equal(active, _counter.Count(PdfFixture.Updated(original, active)));

    /// <summary>
    /// The finding itself: the page tree replaced through a cross-reference stream, the replacement
    /// node inside a compressed object stream. The one supported format is a classic cross-reference
    /// table, so this counts as nothing - refused at <c>startxref</c>, before any object is read -
    /// rather than counted from the stale tree the plain bytes still hold.
    /// </summary>
    [Fact]
    public void A_page_tree_replaced_through_a_cross_reference_stream_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.CrossReferenceStreamUpdate(originalPages: 2, activePages: 101)));

    /// <summary>
    /// The fixture really is the finding's file, which is what makes the test above a regression: the
    /// last <c>startxref</c> names a cross-reference stream that chains back through <c>/Prev</c>,
    /// the object stream really holds a 101-kid page tree for object 2, and object 2 is defined
    /// exactly once in the plain bytes - as the two-page tree, with both trailers naming the same
    /// catalogue. A counter that scans plain bytes for objects answers 2 for a document a reader sees
    /// as 101 pages.
    /// </summary>
    [Fact]
    public void The_cross_reference_stream_fixture_really_does_replace_the_page_tree()
    {
        var bytes = PdfFixture.CrossReferenceStreamUpdate(originalPages: 2, activePages: 101);
        var text = Encoding.Latin1.GetString(bytes);

        var xref = PdfFixture.StartXref(bytes);
        var xrefObject = text[xref..text.IndexOf("stream", xref, StringComparison.Ordinal)];
        Assert.Contains("/Type /XRef", xrefObject, StringComparison.Ordinal);
        Assert.Contains("/Prev ", xrefObject, StringComparison.Ordinal);
        Assert.Contains("/Root 1 0 R", xrefObject, StringComparison.Ordinal);

        var plainDefinitions = Regex.Matches(text, @"(?m)^2 0 obj\n(.*)$");
        var plain = Assert.Single(plainDefinitions);
        Assert.Equal(2, Regex.Matches(plain.Groups[1].Value, @"\d+ 0 R").Count);
        Assert.Equal(2, Regex.Matches(text, "/Root 1 0 R").Count);
        Assert.Equal(2, Regex.Matches(text, "/Root ").Count);

        var objectStream = text.IndexOf("/Type /ObjStm", StringComparison.Ordinal);
        var length = int.Parse(
            Regex.Match(text[objectStream..], @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var start = text.IndexOf("stream\n", objectStream, StringComparison.Ordinal) + 7;
        var unpacked = Encoding.ASCII.GetString(PdfFixture.Inflate(bytes[start..(start + length)]));

        Assert.StartsWith("2 0 ", unpacked, StringComparison.Ordinal);
        Assert.Equal(101, Regex.Matches(unpacked, @"\d+ 0 R").Count);
    }

    /// <summary>
    /// A cross-reference entry must point at the object header it claims. An entry for a page that
    /// points at the catalogue instead resolves to nothing, so the document counts as nothing.
    /// </summary>
    [Fact]
    public void An_entry_that_points_at_another_object_counts_as_nothing()
    {
        var bytes = PdfFixture.Flat(3);
        var catalogue = OffsetOf(bytes, 1);

        Assert.Null(_counter.Count(ReplaceEntry(bytes, 3, Invariant($"{catalogue:D10} 00000 n "))));
    }

    /// <summary>An entry the table marks free is not an object, and a page tree reaching it counts as nothing.</summary>
    [Fact]
    public void A_free_entry_on_the_page_trees_path_counts_as_nothing()
    {
        var bytes = PdfFixture.Flat(3);

        Assert.Null(_counter.Count(ReplaceEntry(bytes, 4, "0000000000 00001 f ")));
    }

    /// <summary>
    /// Without a <c>startxref</c> that points at a classic cross-reference table inside the file,
    /// there is no active graph to resolve, so the document counts as nothing.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("past the end")]
    [InlineData("at an object")]
    public void A_startxref_that_does_not_name_a_cross_reference_table_counts_as_nothing(string how)
    {
        var bytes = PdfFixture.Flat(3);
        var text = Encoding.ASCII.GetString(bytes);
        var real = Invariant($"startxref\n{PdfFixture.StartXref(bytes)}\n");
        var replacement = how switch
        {
            "missing" => string.Empty,
            "past the end" => Invariant($"startxref\n{bytes.Length + 10}\n"),
            _ => Invariant($"startxref\n{OffsetOf(bytes, 1)}\n"),
        };

        Assert.Null(_counter.Count(Encoding.ASCII.GetBytes(text.Replace(real, replacement, StringComparison.Ordinal))));
    }

    /// <summary>A <c>/Prev</c> chain that loops back on itself counts as nothing rather than being followed for ever.</summary>
    [Fact]
    public void A_prev_chain_that_loops_counts_as_nothing()
    {
        var bytes = PdfFixture.Flat(3);
        var text = Encoding.ASCII.GetString(bytes);
        var own = PdfFixture.StartXref(bytes);

        Assert.Null(_counter.Count(Encoding.ASCII.GetBytes(
            text.Replace("/Root 1 0 R ", Invariant($"/Root 1 0 R /Prev {own} "), StringComparison.Ordinal))));
    }

    /// <summary>
    /// Only the active trailer names the catalogue. A trailer planted after the last
    /// <c>%%EOF</c>, naming an object the file does not define, is not read as one: the document's
    /// real three pages are the answer.
    /// </summary>
    [Fact]
    public void The_catalogue_is_the_one_the_active_trailer_names()
    {
        var planted = PdfFixture.Flat(3).Concat(PdfFixture.Raw("trailer\n<< /Root 9 0 R >>\n")).ToArray();

        Assert.Equal(3, _counter.Count(planted));
    }

    /// <summary>
    /// With no trailer there is nothing to say which object is the catalogue, so there is nothing to
    /// count from: a page tree lying around in the bytes is not a document.
    /// </summary>
    [Fact]
    public void A_pdf_with_no_trailer_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Raw(
            "%PDF-1.7\n"
            + "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n"
            + "2 0 obj << /Type /Pages /Count 1 /Kids [3 0 R] >> endobj\n"
            + "3 0 obj << /Type /Page /Parent 2 0 R >> endobj\n"
            + "%%EOF")));

    /// <summary>
    /// With no cross-reference table nothing says which definition of an object is current, so a
    /// file that defines one twice - or any file without a table - counts as nothing.
    /// </summary>
    [Fact]
    public void An_object_defined_twice_with_no_cross_reference_table_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Malformed(
            root: 1,
            "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n"
            + "2 0 obj << /Type /Pages /Count 1 /Kids [3 0 R] >> endobj\n"
            + "2 0 obj << /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >> endobj\n"
            + "3 0 obj << /Type /Page /Parent 2 0 R >> endobj\n"
            + "4 0 obj << /Type /Page /Parent 2 0 R >> endobj\n")));

    /// <summary>A page tree the table does not hold cannot be walked, so the document counts as nothing.</summary>
    [Fact]
    public void A_page_tree_the_file_does_not_hold_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Document(root: 1, "<< /Type /Catalog /Pages 2 0 R >>")));

    /// <summary>A page tree that revisits a node is not a tree, and counts as nothing.</summary>
    [Fact]
    public void A_page_tree_that_revisits_a_node_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Document(
            root: 1,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Pages /Count 1 /Kids [2 0 R] >>")));

    /// <summary>
    /// A node that declares no <c>/Type</c> is neither a page nor a page tree as far as anything can
    /// prove, so the document counts as nothing.
    /// </summary>
    [Fact]
    public void A_page_tree_node_with_no_type_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Document(
            root: 1,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Parent 2 0 R /MediaBox [0 0 595 842] >>")));

    /// <summary>A <c>/Kids</c> entry that is not a reference stops the count rather than being skipped.</summary>
    [Fact]
    public void A_kid_that_is_not_a_reference_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Document(
            root: 1,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 2 /Kids [3 0 R 4] >>",
            "<< /Type /Page /Parent 2 0 R >>")));

    /// <summary>
    /// A trailer whose <c>/Root</c> does not name a catalogue at all counts as nothing: the one
    /// object allowed to say where the pages are is the catalogue.
    /// </summary>
    [Fact]
    public void A_root_that_is_not_a_catalogue_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Document(
            root: 2,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R >>")));

    /// <summary>Where object <paramref name="number"/>'s header is in <paramref name="bytes"/>.</summary>
    private static int OffsetOf(byte[] bytes, int number) =>
        Encoding.ASCII.GetString(bytes).IndexOf(Invariant($"{number} 0 obj"), StringComparison.Ordinal);

    /// <summary><paramref name="bytes"/> with object <paramref name="number"/>'s cross-reference entry replaced.</summary>
    private static byte[] ReplaceEntry(byte[] bytes, int number, string entry)
    {
        var text = Encoding.ASCII.GetString(bytes);
        var table = PdfFixture.StartXref(bytes);
        var lines = text[table..].Split('\n');

        // "xref", the subsection header, then the free entry for object 0.
        lines[number + 2] = entry;

        return Encoding.ASCII.GetBytes(text[..table] + string.Join('\n', lines));
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
