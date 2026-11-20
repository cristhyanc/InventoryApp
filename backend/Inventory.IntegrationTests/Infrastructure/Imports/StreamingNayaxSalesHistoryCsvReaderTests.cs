using System.Text;
using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Imports;
using Xunit;
using Xunit.Abstractions;

namespace InventoryApi.Tests.Infrastructure.Imports;

/// <summary>
/// The streaming sales history CSV reader (issue #1304): what it refuses, what it reads, and - the
/// reason it exists - that what it costs does not grow with the length of the history.
/// </summary>
public class StreamingNayaxSalesHistoryCsvReaderTests
{
    private const string Header =
        "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,"
        + "ProductName,MachineName,AuthorizationDateTimeGMT";

    private readonly ITestOutputHelper _output;

    public StreamingNayaxSalesHistoryCsvReaderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void A_readable_export_is_accepted_and_its_data_rows_are_counted()
    {
        var check = Check(Csv(3));

        Assert.Equal(NayaxSalesHistoryFileState.Readable, check.State);
        Assert.Equal(3, check.DataRows);
    }

    [Fact]
    public void A_file_with_no_header_row_is_unreadable()
    {
        var check = Check(string.Empty);

        Assert.Equal(NayaxSalesHistoryFileState.Unreadable, check.State);
        Assert.Equal(0, check.DataRows);
    }

    /// <summary>
    /// A file whose header row does not name the transaction identifier is not a Nayax sales export:
    /// every row's identity starts with it, so such a file would import as nothing but skipped rows.
    /// </summary>
    [Fact]
    public void A_file_without_the_transaction_id_column_is_unreadable()
    {
        var check = Check("MachineID,SettlementValue\n942488501,3.50\n");

        Assert.Equal(NayaxSalesHistoryFileState.Unreadable, check.State);
    }

    [Fact]
    public void A_file_with_a_header_row_and_nothing_under_it_has_no_rows()
    {
        Assert.Equal(NayaxSalesHistoryFileState.NoRows, Check(Header + "\n").State);
    }

    /// <summary>
    /// <strong>The other parity case.</strong> A header row that names one column twice states
    /// nothing about which of the two the rows below it belong to, and the uploaded import's reader
    /// fails outright on such a file. So this reader refuses it instead of choosing an occurrence:
    /// the two imports must not read one file differently, which is exactly what reading the first
    /// column here while the normal upload refuses the file would mean.
    /// </summary>
    [Fact]
    public void A_file_naming_one_column_twice_is_refused_rather_than_read()
    {
        const string csv =
            "TransactionID,TransactionStatusId,MachineID,SettlementValue,SettlementValue\n"
            + "9000001,12,942488501,3.50,9.99\n";

        Assert.Equal(NayaxSalesHistoryFileState.DuplicateColumn, Check(csv).State);
        Assert.Equal(0, Check(csv).DataRows);

        // The same file through the uploaded import's own reader: it fails outright, which is the
        // behaviour this refusal keeps the two readers agreed on.
        using var workbookContent = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        Assert.ThrowsAny<Exception>(() => new ClosedXmlNayaxSalesWorkbookReader()
            .Read(workbookContent, "history.csv", NayaxSalesFileLimits.None));

        // And nothing reads such a file row by row either, however it was reached.
        Assert.Throws<InvalidOperationException>(() => Read(csv).ToList());
    }

    /// <summary>
    /// A heading repeated only in the letters and digits the reader normalises away - the spelling
    /// an export might vary - is the same duplicate, because that is how the columns are matched.
    /// </summary>
    [Fact]
    public void A_heading_repeated_in_another_spelling_is_the_same_duplicate()
    {
        Assert.Equal(
            NayaxSalesHistoryFileState.DuplicateColumn,
            Check("TransactionID,Transaction Id,MachineID\n9000001,9000001,942488501\n").State);
    }

    /// <summary>
    /// The row ceiling is enforced while the file is being counted, and the count stops at the first
    /// row past it - so a file far over the limit costs no more to refuse than one row over it.
    /// </summary>
    [Fact]
    public void A_file_past_the_row_ceiling_is_refused()
    {
        Assert.Equal(NayaxSalesHistoryFileState.TooManyRows, Check(Csv(4), maxDataRows: 3).State);
        Assert.Equal(NayaxSalesHistoryFileState.Readable, Check(Csv(3), maxDataRows: 3).State);
    }

    /// <summary>
    /// <strong>The parity case.</strong> The same CSV read by this reader and by the uploaded
    /// import's own workbook reader produces exactly the same rows - every value, every "what the
    /// file carried" flag and every row number - because both ask <c>NayaxSalesCells</c> for each
    /// cell. That is what makes "the history import applies the same row rules" start from the same
    /// facts.
    /// </summary>
    [Fact]
    public void It_reads_the_same_rows_as_the_uploaded_imports_own_reader()
    {
        // Deliberately awkward: a quoted field holding a comma, a blank status, an unreadable
        // amount, a whitespace-only product name, a missing trailing column and an export-format
        // instant.
        const string csv =
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,ProductName,MachineName,AuthorizationDateTimeGMT,Product Cost Price\n"
            + "9000001,12,942488501,7,3.50,Credit Card,\"Snack, large\",Machine A,2026-06-01T00:00:00Z,1.10\n"
            + "9000002,,942488501,7,$3.50,Cash,   ,Machine A,4/10/2026 11:30:00 PM,\n"
            + "9000003,62,942488501,,0,Card,Snack,Machine A,\n"
            + ",,,,,,,,\n";

        var streamed = Read(csv).ToList();

        using var workbookContent = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var workbook = new ClosedXmlNayaxSalesWorkbookReader()
            .Read(workbookContent, "history.csv", NayaxSalesFileLimits.None);

        Assert.False(workbook.ExceedsLimits);
        Assert.Equal(workbook.Rows, streamed);
    }

    /// <summary>
    /// A resumed attempt passes over the rows already committed and yields the rest, in file order.
    /// </summary>
    [Fact]
    public void Skipped_rows_are_the_ones_a_previous_attempt_committed()
    {
        var rows = Read(Csv(5), skipDataRows: 3).ToList();

        Assert.Equal([9000004L, 9000005L], rows.Select(row => row.TransactionId));

        // The row numbers are still the file's own, which is what makes a reported invalid row
        // findable whichever attempt read it.
        Assert.Equal([5, 6], rows.Select(row => row.RowNumber));
    }

    /// <summary>
    /// Nothing is read until the sequence is enumerated, and a caller that stops reading stops the
    /// reading - which is what "row by row" means for a 250,000-row file.
    /// </summary>
    [Fact]
    public void It_reads_nothing_until_it_is_enumerated_and_stops_when_the_caller_does()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(Csv(50_000)));

        var rows = new StreamingNayaxSalesHistoryCsvReader().ReadRows(content, 0);
        Assert.Equal(0, content.Position);

        var first = rows.Take(2).ToList();

        Assert.Equal(2, first.Count);
        Assert.True(
            content.Position < content.Length,
            "taking two rows should not have read the whole file");
    }

    /// <summary>
    /// <strong>The memory case the issue asks for.</strong> A 200,000-row synthetic history is read
    /// in full and the reader's own retained working set does not grow with the rows: the managed
    /// heap after the walk is within a few megabytes of what it was before it, and the measured
    /// figure is written to the test output - which is where the pull request's number comes from.
    ///
    /// <para>What is measured is the <em>reader</em>, deliberately, and not the import: the bytes of
    /// the uploaded file are held once (bounded by
    /// <see cref="NayaxSalesHistoryPolicy.MaxUploadBytes"/>, not by the row count) and each batch of
    /// <see cref="NayaxSalesHistoryPolicy.BatchRows"/> rows is held by the import while it applies
    /// it. What this case rules out is the thing the uploaded import does and this one must not:
    /// materialising the file's rows.</para>
    /// </summary>
    [Fact]
    public void Reading_two_hundred_thousand_rows_does_not_grow_with_the_rows()
    {
        const int rows = 200_000;
        const long budgetBytes = 8L * 1024 * 1024;

        using var content = new MemoryStream(Encoding.UTF8.GetBytes(Csv(rows)));

        var before = GC.GetTotalMemory(forceFullCollection: true);

        // Counted rather than collected, which is what a caller that streams does.
        var read = 0;
        long lastTransactionId = 0;
        foreach (var row in new StreamingNayaxSalesHistoryCsvReader().ReadRows(content, 0))
        {
            read++;
            lastTransactionId = row.TransactionId;
        }

        var after = GC.GetTotalMemory(forceFullCollection: true);
        var retained = after - before;

        _output.WriteLine(
            $"{rows} rows streamed: managed heap {before / 1024 / 1024} MB before, "
                + $"{after / 1024 / 1024} MB after, retained {retained / 1024} KB.");

        Assert.Equal(rows, read);
        Assert.Equal(9_000_000 + rows, lastTransactionId);
        Assert.InRange(retained, long.MinValue, budgetBytes);
    }

    private static NayaxSalesHistoryFileCheck Check(string csv, int? maxDataRows = null)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        return new StreamingNayaxSalesHistoryCsvReader()
            .Check(content, maxDataRows ?? NayaxSalesHistoryPolicy.MaxRows);
    }

    private static IEnumerable<NayaxSalesImportRow> Read(string csv, int skipDataRows = 0)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        // Materialised inside the helper, because the stream is this method's own: the laziness
        // itself is asserted by its own case above.
        return new StreamingNayaxSalesHistoryCsvReader().ReadRows(content, skipDataRows).ToList();
    }

    /// <summary>A synthetic export of <paramref name="rows"/> completed card sales.</summary>
    private static string Csv(int rows)
    {
        var csv = new StringBuilder(Header).Append('\n');

        for (var row = 1; row <= rows; row++)
        {
            csv.Append(9_000_000 + row)
                .Append(",12,942488501,7,3.50,Credit Card,Snack,Machine A,2026-06-01T00:00:00Z\n");
        }

        return csv.ToString();
    }
}
