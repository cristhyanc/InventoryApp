using System.Text;
using Inventory.Infrastructure.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.ReceiptScanning;

/// <summary>
/// Counting a PDF's pages before anything is uploaded (issue #1163).
///
/// <para>The documents here are minimal, hand-written PDFs - enough structure for the count and
/// nothing else. A real receipt is never committed as a fixture (issue #1163's known
/// constraints).</para>
/// </summary>
public class PdfPageCounterTests
{
    private readonly PdfPageCounter _counter = new();

    /// <summary>The page tree's own count is the answer when one is written, which is the usual case.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(120)]
    public void The_page_tree_count_is_read(int pages) =>
        Assert.Equal(pages, Count(PdfWithPageTree(pages)));

    /// <summary>
    /// A linearised file repeats smaller counts on sub-nodes; the root's is the largest, and it is
    /// the document's page count.
    /// </summary>
    [Fact]
    public void The_largest_page_tree_count_is_the_documents_own() =>
        Assert.Equal(
            9,
            Count(
                "%PDF-1.7\n"
                + "1 0 obj << /Type /Pages /Count 9 /Kids [2 0 R 3 0 R] >> endobj\n"
                + "2 0 obj << /Type /Pages /Count 4 /Kids [] >> endobj\n"
                + "3 0 obj << /Type /Pages /Count 5 /Kids [] >> endobj\n"
                + "%%EOF"));

    /// <summary>
    /// With no readable page tree, the leaves are counted instead - and <c>/Pages</c> is not mistaken
    /// for <c>/Page</c>, which a prefix match would do and would count every tree node as a page.
    /// </summary>
    [Fact]
    public void The_page_leaves_are_counted_when_no_page_tree_count_is_readable() =>
        Assert.Equal(
            2,
            Count(
                "%PDF-1.4\n"
                + "1 0 obj << /Type /Pages /Kids [2 0 R 3 0 R] >> endobj\n"
                + "2 0 obj << /Type /Page /Parent 1 0 R >> endobj\n"
                + "3 0 obj << /Type /Page /Parent 1 0 R >> endobj\n"
                + "%%EOF"));

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
        Assert.Null(Count(content));

    /// <summary>
    /// A PDF with no readable page structure - an encrypted one, or one that keeps its objects in a
    /// compressed object stream - counts as nothing too. That is the conservative direction: a
    /// refusal the operator can work around, not a page limit silently bypassed.
    /// </summary>
    [Fact]
    public void A_pdf_with_no_readable_page_structure_counts_as_nothing() =>
        Assert.Null(Count("%PDF-1.7\n1 0 obj << /Encrypt 2 0 R >> endobj\n%%EOF"));

    /// <summary>
    /// A <c>/Count</c> that is not a number is no count, and falls through to the leaves rather than
    /// producing a page count of zero.
    /// </summary>
    [Fact]
    public void A_page_tree_count_that_is_not_a_number_is_no_count() =>
        Assert.Equal(
            1,
            Count(
                "%PDF-1.4\n"
                + "1 0 obj << /Type /Pages /Count (many) /Kids [2 0 R] >> endobj\n"
                + "2 0 obj << /Type /Page /Parent 1 0 R >> endobj\n"
                + "%%EOF"));

    private int? Count(string content) => _counter.Count(Encoding.UTF8.GetBytes(content));

    private static string PdfWithPageTree(int pages)
    {
        var text = new StringBuilder("%PDF-1.7\n");
        text.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        text.Append(
            System.Globalization.CultureInfo.InvariantCulture,
            $"2 0 obj << /Type /Pages /Count {pages} /Kids [3 0 R] >> endobj\n");
        text.Append("3 0 obj << /Type /Page /Parent 2 0 R >> endobj\n");
        text.Append("%%EOF");

        return text.ToString();
    }
}
