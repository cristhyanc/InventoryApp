using System.Globalization;
using System.Text;
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
    /// A PDF with no readable page structure - an encrypted one, or one that keeps its catalogue and
    /// page tree in a compressed object stream - counts as nothing. That is the conservative
    /// direction: a refusal the operator can work around, not a page limit silently bypassed.
    /// </summary>
    [Fact]
    public void A_pdf_with_no_readable_page_structure_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Raw("%PDF-1.7\n1 0 obj << /Encrypt 2 0 R >> endobj\n%%EOF")));

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
    /// Trailers that name different catalogues are an incremental update this counter cannot order,
    /// so it refuses rather than picking one - which is also what stops a planted second catalogue
    /// being counted in place of the document's own.
    /// </summary>
    [Fact]
    public void Trailers_that_name_different_catalogues_count_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Raw(
            "%PDF-1.7\n"
            + "1 0 obj << /Type /Catalog /Pages 3 0 R >> endobj\n"
            + "2 0 obj << /Type /Catalog /Pages 3 0 R >> endobj\n"
            + "3 0 obj << /Type /Pages /Count 1 /Kids [4 0 R] >> endobj\n"
            + "4 0 obj << /Type /Page /Parent 3 0 R >> endobj\n"
            + "trailer << /Root 1 0 R >>\n"
            + "trailer << /Root 2 0 R >>\n"
            + "%%EOF")));

    /// <summary>
    /// A catalogue or a page-tree node the file does not hold in plain bytes - the object-stream
    /// case - cannot be walked, so the document counts as nothing.
    /// </summary>
    [Fact]
    public void A_page_tree_the_file_does_not_hold_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Malformed(
            root: 1,
            "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n")));

    /// <summary>
    /// An object the file defines twice is one whose current definition only the cross-reference
    /// table knows, and an incremental update may replace a page tree node. It counts as nothing
    /// rather than being counted from the wrong generation.
    /// </summary>
    [Fact]
    public void An_object_the_file_defines_twice_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Malformed(
            root: 1,
            "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n"
            + "2 0 obj << /Type /Pages /Count 1 /Kids [3 0 R] >> endobj\n"
            + "2 0 obj << /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >> endobj\n"
            + "3 0 obj << /Type /Page /Parent 2 0 R >> endobj\n"
            + "4 0 obj << /Type /Page /Parent 2 0 R >> endobj\n")));

    /// <summary>A page tree that revisits a node is not a tree, and counts as nothing.</summary>
    [Fact]
    public void A_page_tree_that_revisits_a_node_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Malformed(
            root: 1,
            "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n"
            + "2 0 obj << /Type /Pages /Count 1 /Kids [3 0 R] >> endobj\n"
            + "3 0 obj << /Type /Pages /Count 1 /Kids [2 0 R] >> endobj\n")));

    /// <summary>
    /// A node that declares no <c>/Type</c> is neither a page nor a page tree as far as anything can
    /// prove, so the document counts as nothing.
    /// </summary>
    [Fact]
    public void A_page_tree_node_with_no_type_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Malformed(
            root: 1,
            "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n"
            + "2 0 obj << /Type /Pages /Count 1 /Kids [3 0 R] >> endobj\n"
            + "3 0 obj << /Parent 2 0 R /MediaBox [0 0 595 842] >> endobj\n")));

    /// <summary>A <c>/Kids</c> entry that is not a reference stops the count rather than being skipped.</summary>
    [Fact]
    public void A_kid_that_is_not_a_reference_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Malformed(
            root: 1,
            "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n"
            + "2 0 obj << /Type /Pages /Count 2 /Kids [3 0 R 4] >> endobj\n"
            + "3 0 obj << /Type /Page /Parent 2 0 R >> endobj\n")));

    /// <summary>
    /// A trailer whose <c>/Root</c> does not name a catalogue at all counts as nothing: the one
    /// object allowed to say where the pages are is the catalogue.
    /// </summary>
    [Fact]
    public void A_root_that_is_not_a_catalogue_counts_as_nothing() =>
        Assert.Null(_counter.Count(PdfFixture.Malformed(
            root: 2,
            "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n"
            + "2 0 obj << /Type /Pages /Count 1 /Kids [3 0 R] >> endobj\n"
            + "3 0 obj << /Type /Page /Parent 2 0 R >> endobj\n")));
}
