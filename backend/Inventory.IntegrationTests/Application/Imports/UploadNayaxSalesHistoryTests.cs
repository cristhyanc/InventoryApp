using System.Text;
using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// What the sales history upload accepts and refuses (issue #1304), over relational SQLite with the
/// real streaming reader, the real job store and the real tenant ownership stamp.
///
/// <para>Every refusal here stores <strong>nothing</strong>: not the bytes, not a job, not a sale.
/// That is asserted case by case rather than assumed, because a partially accepted history would
/// leave an operator unable to say which half of their year landed.</para>
/// </summary>
public class UploadNayaxSalesHistoryTests
{
    [Fact]
    public async Task An_accepted_history_is_stored_as_a_queued_job_with_its_row_count()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var csv = NayaxSalesHistoryImportFixture.Csv(4);

        await using var db = fixture.Context();
        var result = await fixture.Upload(db).Handle(
            "C:\\exports\\..\\Sales history 2024-2026.csv", Encoding.UTF8.GetBytes(csv), CancellationToken.None);

        Assert.True(result.IsAccepted);
        Assert.Null(result.Rejection);

        var job = await fixture.JobRowAsync(result.JobId!.Value);
        Assert.Equal(NayaxSalesHistoryImportJobState.Queued, job.State);
        Assert.Equal(NayaxSalesHistoryImportFixture.BusinessA, job.BusinessId);
        Assert.Equal(4, job.TotalRows);
        Assert.Equal(0, job.RowsRead);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.FinishedAtUtc);
        Assert.Null(job.LastError);

        // The bytes are stored exactly as they arrived, because the job reads from them.
        Assert.Equal(Encoding.UTF8.GetBytes(csv), job.File);

        // The recorded name is a leaf label: no directory a client claimed, no traversal segment.
        Assert.Equal("Sales history 2024-2026.csv", job.FileName);

        // The uploader is the validated Entra pair, and nothing else about them.
        Assert.Equal(
            NayaxSalesHistoryImportFixture.Actor().DirectoryTenantId, job.UploadedByDirectoryTenantId);
        Assert.Equal(NayaxSalesHistoryImportFixture.Actor().ObjectId, job.UploadedByObjectId);

        // And nothing has been imported: that is the background job's work.
        Assert.Empty(await fixture.SalesAsync());
    }

    [Theory]
    [InlineData("history.xlsx")]
    [InlineData("history.xls")]
    [InlineData("history.txt")]
    public async Task A_file_that_is_not_a_csv_is_refused(string fileName)
    {
        var rejection = await RefusalAsync(
            fileName, Encoding.UTF8.GetBytes(NayaxSalesHistoryImportFixture.Csv(2)));

        Assert.Equal(NayaxSalesHistoryPolicy.UnsupportedFormatMessage, rejection);
    }

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        Assert.Equal(NayaxSalesHistoryPolicy.EmptyUploadMessage, await RefusalAsync("history.csv", []));
    }

    [Fact]
    public async Task A_file_over_the_byte_limit_is_refused()
    {
        var oversized = new byte[NayaxSalesHistoryPolicy.MaxUploadBytes + 1];

        Assert.Equal(NayaxSalesHistoryPolicy.TooLargeMessage, await RefusalAsync("history.csv", oversized));
    }

    [Fact]
    public async Task A_file_that_is_not_a_sales_export_is_refused()
    {
        var rejection = await RefusalAsync(
            "history.csv", Encoding.UTF8.GetBytes("Date,Amount\n2026-06-01,3.50\n"));

        Assert.Equal(NayaxSalesHistoryPolicy.UnreadableMessage, rejection);
    }

    [Fact]
    public async Task A_file_with_no_sales_rows_is_refused()
    {
        var rejection = await RefusalAsync(
            "history.csv", Encoding.UTF8.GetBytes(NayaxSalesHistoryImportFixture.HeaderOnly()));

        Assert.Equal(NayaxSalesHistoryPolicy.NoRowsMessage, rejection);
    }

    /// <summary>
    /// A header row that names one column twice is refused with nothing stored, rather than
    /// imported under a reading of its own: the uploaded import's reader fails outright on the same
    /// file, and one file may not mean two things depending on which upload it was sent to.
    /// </summary>
    [Fact]
    public async Task A_file_naming_one_column_twice_is_refused()
    {
        var rejection = await RefusalAsync(
            "history.csv",
            Encoding.UTF8.GetBytes(
                "TransactionID,TransactionStatusId,MachineID,SettlementValue,SettlementValue\n"
                + "9000001,12,942488501,3.50,9.99\n"));

        Assert.Equal(NayaxSalesHistoryPolicy.DuplicateColumnMessage, rejection);
    }

    /// <summary>
    /// The row ceiling, with the reader asked for the policy's own number: a file one row past it is
    /// refused with the sentence that says what to do about it.
    /// </summary>
    [Fact]
    public async Task A_file_past_the_row_ceiling_is_refused()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();

        await using var db = fixture.Context();
        var upload = new UploadNayaxSalesHistory(
            new FixedCsvReader(NayaxSalesHistoryFileCheck.TooManyRows),
            NayaxSalesHistoryImportFixture.Jobs(db),
            InventoryApi.Tests.Application.Tenancy.FakeAuthenticatedActorAccessor.Identified(
                NayaxSalesHistoryImportFixture.Actor()),
            fixture.Clock);

        var result = await upload.Handle(
            "history.csv", Encoding.UTF8.GetBytes("TransactionID\n1\n"), CancellationToken.None);

        Assert.Equal(NayaxSalesHistoryPolicy.TooManyRowsMessage, result.Rejection);
        Assert.Null(result.JobId);
        Assert.Equal(0, await db.NayaxSalesHistoryImportJobs.CountAsync());
    }

    /// <summary>
    /// One history imports per business at a time: a second upload while one is queued or processing
    /// is refused with the sentence that says to wait, and nothing of it is stored.
    /// </summary>
    [Fact]
    public async Task A_second_upload_while_one_is_running_is_refused()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var first = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(2));

        await using (var db = fixture.Context())
        {
            var result = await fixture.Upload(db).Handle(
                "history.csv",
                Encoding.UTF8.GetBytes(NayaxSalesHistoryImportFixture.Csv(2, firstTransactionId: 8_000_001)),
                CancellationToken.None);

            Assert.Equal(NayaxSalesHistoryPolicy.AlreadyRunningMessage, result.Rejection);
            Assert.Null(result.JobId);
        }

        await using (var db = fixture.Unrestricted())
            Assert.Equal(1, await db.NayaxSalesHistoryImportJobs.IgnoreQueryFilters().CountAsync());

        // Once the first job has finished, the next upload is accepted.
        await using (var db = fixture.Context())
            await fixture.Process(db).Handle(first, CancellationToken.None);

        await using (var db = fixture.Context())
        {
            var result = await fixture.Upload(db).Handle(
                "history.csv",
                Encoding.UTF8.GetBytes(NayaxSalesHistoryImportFixture.Csv(2, firstTransactionId: 8_000_001)),
                CancellationToken.None);

            Assert.True(result.IsAccepted);
        }
    }

    /// <summary>
    /// The one-at-a-time rule is per business, as every other rule here is: another business's
    /// running job neither blocks this one nor is visible to it.
    /// </summary>
    [Fact]
    public async Task Another_businesss_running_job_does_not_block_an_upload()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(2), NayaxSalesHistoryImportFixture.BusinessB);

        await using var db = fixture.Context(NayaxSalesHistoryImportFixture.BusinessA);
        var result = await fixture.Upload(db).Handle(
            "history.csv",
            Encoding.UTF8.GetBytes(NayaxSalesHistoryImportFixture.Csv(2)),
            CancellationToken.None);

        Assert.True(result.IsAccepted);

        var job = await fixture.JobRowAsync(result.JobId!.Value);
        Assert.Equal(NayaxSalesHistoryImportFixture.BusinessA, job.BusinessId);
    }

    /// <summary>The refusal one upload answers with, having stored nothing.</summary>
    private static async Task<string?> RefusalAsync(string fileName, byte[] content)
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();

        await using var db = fixture.Context();
        var result = await fixture.Upload(db).Handle(fileName, content, CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.Equal(0, await db.NayaxSalesHistoryImportJobs.CountAsync());

        return result.Rejection;
    }

    /// <summary>
    /// A reader that answers one fixed check, for the row-ceiling case: building a 250,001-row file
    /// to prove the use case reports the policy's refusal would spend twenty megabytes and several
    /// seconds proving something the reader's own case already proves.
    /// </summary>
    private sealed class FixedCsvReader : INayaxSalesHistoryCsvReader
    {
        private readonly NayaxSalesHistoryFileCheck _check;

        public FixedCsvReader(NayaxSalesHistoryFileCheck check) => _check = check;

        public NayaxSalesHistoryFileCheck Check(Stream content, int maxDataRows)
        {
            Assert.Equal(NayaxSalesHistoryPolicy.MaxRows, maxDataRows);
            return _check;
        }

        public IEnumerable<NayaxSalesImportRow> ReadRows(Stream content, int skipDataRows) => [];
    }
}
