using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using InventoryApi.Tests.Application.Providers;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// What one attempt on one uploaded reimbursement report job does (issue #1149): the per-file
/// outcomes, the resume rule, and the retry stop rule.
///
/// <para>The transaction is a port here, so these cases can state <strong>which files committed</strong>
/// without a database; the claims that only a real database can settle - that a rolled-back file
/// leaves neither rows nor an outcome, and that the counts come out of the rows - are in
/// <c>Inventory.IntegrationTests</c>.</para>
/// </summary>
public class ProcessReimbursementImportJobTests
{
    private static readonly DateTime RunAt = new(2026, 11, 9, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Every_file_is_imported_and_the_job_is_completed()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);
        var store = new FakeImportedReimbursementStore();
        var transactions = new FakeImportFileTransactions();

        var result = await Process(jobs, store, transactions, Entries("HASH-A", "HASH-B"))
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(ReimbursementImportJobRunOutcome.Finished, result.Outcome);
        Assert.Equal(new ReimbursementImportFileCounts(2, 0, 0), result.Counts);
        Assert.Equal(2, result.FilesProcessedNow);
        Assert.Equal(["HASH-A", "HASH-B"], store.Imported);
        Assert.Equal(ReimbursementImportJobState.Completed, jobs.Jobs[jobId].State);

        // Each file is its own transaction, and each one committed.
        Assert.Equal([true, true], transactions.Committed);
    }

    /// <summary>
    /// Re-uploading the same archive comes out entirely skipped - which is a completed job, not a
    /// failed one - and writes no reimbursement row at all.
    /// </summary>
    [Fact]
    public async Task A_re_uploaded_archive_comes_out_skipped_and_completed()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);
        var store = new FakeImportedReimbursementStore();
        store.ExistingHashes.Add("HASH-A");
        store.ExistingHashes.Add("HASH-B");

        var result = await Process(jobs, store, new FakeImportFileTransactions(), Entries("HASH-A", "HASH-B"))
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(new ReimbursementImportFileCounts(0, 2, 0), result.Counts);
        Assert.Empty(store.Imported);
        Assert.Equal(ReimbursementImportJobState.Completed, jobs.Jobs[jobId].State);
        Assert.All(jobs.Outcomes, outcome =>
            Assert.Equal(ReimbursementImportFileOutcome.Skipped, outcome.Outcome));
    }

    /// <summary>
    /// One unreadable XML among valid ones is that file's own recorded failure with a safe reason;
    /// the rest import, and the job is completed-with-failures rather than failed.
    /// </summary>
    [Fact]
    public async Task One_invalid_xml_among_valid_ones_fails_only_itself()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);
        var store = new FakeImportedReimbursementStore();

        var entries = new[]
        {
            Entry(0, ReimbursementImportFakes.File("HASH-A")),
            Entry(1, parsed: null),
            Entry(2, ReimbursementImportFakes.File("HASH-C")),
        };

        var result = await Process(jobs, store, new FakeImportFileTransactions(), entries)
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(new ReimbursementImportFileCounts(2, 0, 1), result.Counts);
        Assert.Equal(ReimbursementImportJobState.CompletedWithFailures, jobs.Jobs[jobId].State);
        Assert.Equal(["HASH-A", "HASH-C"], store.Imported);

        var failure = Assert.Single(
            jobs.Outcomes.Where(outcome => outcome.Outcome == ReimbursementImportFileOutcome.Failed));
        Assert.Equal(ProcessReimbursementImportJob.UnreadableFileReason, failure.Reason);

        // The reason says nothing about the file's contents.
        Assert.DoesNotContain("row", failure.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file that already has an outcome keeps it exactly as recorded and is not processed again,
    /// which is what makes a job interrupted by a restart safe to resume.
    /// </summary>
    [Fact]
    public async Task A_resumed_job_processes_only_the_files_with_no_outcome()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);
        var store = new FakeImportedReimbursementStore();

        // The first attempt committed the first file and then the process died.
        jobs.Outcomes.Add(new ReimbursementImportFileResult(
            0, "report-a.xml", ReimbursementImportFileOutcome.Imported, null));
        store.ExistingHashes.Add("HASH-A");

        var result = await Process(jobs, store, new FakeImportFileTransactions(), Entries("HASH-A", "HASH-B"))
            .Handle(jobId, CancellationToken.None);

        // Only the second file went through the pipeline, and the first still reports Imported.
        Assert.Equal(1, result.FilesProcessedNow);
        Assert.Equal(["HASH-B"], store.Imported);
        Assert.Equal(new ReimbursementImportFileCounts(2, 0, 0), result.Counts);
        Assert.Equal(
            ReimbursementImportFileOutcome.Imported,
            jobs.Outcomes.Single(outcome => outcome.Index == 0).Outcome);
    }

    /// <summary>
    /// A database failure inside a file's transaction requeues the job with the scheduled wait and
    /// leaves the files it had already committed alone.
    /// </summary>
    [Fact]
    public async Task A_database_failure_requeues_the_job_with_the_scheduled_wait()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);
        var store = new FakeImportedReimbursementStore();
        store.ImportThrowsFor.Add("HASH-B");
        var transactions = new FakeImportFileTransactions();

        var result = await Process(jobs, store, transactions, Entries("HASH-A", "HASH-B"))
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(ReimbursementImportJobRunOutcome.Requeued, result.Outcome);

        var job = jobs.Jobs[jobId];
        Assert.Equal(ReimbursementImportJobState.Queued, job.State);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(RunAt.AddMinutes(1), job.NextAttemptAtUtc);
        Assert.True(job.HasArchive);

        // The first file is committed and recorded; the second committed nothing.
        Assert.Equal(["HASH-A"], store.Imported);
        Assert.Equal([true, false], transactions.Committed);
        Assert.Equal(0, Assert.Single(jobs.Outcomes).Index);

        // And the recorded reason is safe: a failure type, never a database message.
        Assert.NotNull(job.LastError);
        Assert.DoesNotContain("busy", job.LastError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The fifth failed attempt stops the job with the documented sentence, and the outcomes it had
    /// already recorded stay - so re-uploading imports only what is missing.
    /// </summary>
    [Fact]
    public async Task The_fifth_failed_attempt_stops_the_job_and_keeps_its_outcomes()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);
        var store = new FakeImportedReimbursementStore();
        store.ImportThrowsFor.Add("HASH-A");

        var process = Process(jobs, store, new FakeImportFileTransactions(), Entries("HASH-A"));

        for (var attempt = 1; attempt <= ReimbursementImportRetryPolicy.MaxAttempts - 1; attempt++)
        {
            var requeued = await process.Handle(jobId, CancellationToken.None);

            Assert.Equal(ReimbursementImportJobRunOutcome.Requeued, requeued.Outcome);
            Assert.Equal(attempt, jobs.Jobs[jobId].Attempts);
        }

        var stopped = await process.Handle(jobId, CancellationToken.None);

        Assert.Equal(ReimbursementImportJobRunOutcome.Stopped, stopped.Outcome);

        var job = jobs.Jobs[jobId];
        Assert.Equal(ReimbursementImportJobState.Failed, job.State);
        Assert.Equal(ReimbursementImportRetryPolicy.MaxAttempts, job.Attempts);
        Assert.Equal(ReimbursementImportRetryPolicy.AttemptsExhaustedReason, job.LastError);
        Assert.Null(job.NextAttemptAtUtc);
        Assert.False(job.HasArchive);
    }

    /// <summary>
    /// A restart that finds a job <c>Processing</c> counts as an attempt - the attempt is counted
    /// when it starts, so a process that died mid-job has already spent one.
    /// </summary>
    [Fact]
    public async Task Resuming_a_processing_job_counts_as_an_attempt()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);
        jobs.Jobs[jobId].State = ReimbursementImportJobState.Processing;
        jobs.Jobs[jobId].Attempts = 2;

        await Process(
                jobs,
                new FakeImportedReimbursementStore(),
                new FakeImportFileTransactions(),
                Entries("HASH-A"))
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(3, jobs.Jobs[jobId].Attempts);
    }

    /// <summary>
    /// And a job that crashed its own host on its fifth attempt is stopped rather than resumed into
    /// a sixth: five attempts means five.
    /// </summary>
    [Fact]
    public async Task A_processing_job_with_no_attempts_left_is_stopped_rather_than_resumed()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);
        jobs.Jobs[jobId].State = ReimbursementImportJobState.Processing;
        jobs.Jobs[jobId].Attempts = ReimbursementImportRetryPolicy.MaxAttempts;

        var result = await Process(
                jobs,
                new FakeImportedReimbursementStore(),
                new FakeImportFileTransactions(),
                Entries("HASH-A"))
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(ReimbursementImportJobRunOutcome.Stopped, result.Outcome);
        Assert.Equal(ReimbursementImportJobState.Failed, jobs.Jobs[jobId].State);
        Assert.Equal(
            ReimbursementImportRetryPolicy.AttemptsExhaustedReason, jobs.Jobs[jobId].LastError);
    }

    /// <summary>
    /// A job whose stored archive is gone stops with a safe reason rather than retrying something it
    /// cannot read.
    /// </summary>
    [Fact]
    public async Task A_job_whose_archive_is_gone_stops_with_a_safe_reason()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);
        jobs.Jobs[jobId].HasArchive = false;

        var result = await Process(
                jobs,
                new FakeImportedReimbursementStore(),
                new FakeImportFileTransactions(),
                Entries("HASH-A"))
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(ReimbursementImportJobRunOutcome.Stopped, result.Outcome);
        Assert.Equal(
            ProcessReimbursementImportJob.ArchiveMissingReason, jobs.Jobs[jobId].LastError);
    }

    /// <summary>
    /// A stored archive that can no longer be read is the job's own stop, not an attempt: reading
    /// the same bytes again would fail in exactly the same way.
    /// </summary>
    [Fact]
    public async Task A_stored_archive_that_cannot_be_read_stops_the_job()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);

        var result = await new ProcessReimbursementImportJob(
                jobs,
                FakeArchiveReader.Refusing(ReimbursementArchivePolicy.UnreadableArchiveMessage),
                new ImportReimbursementXmlFile(
                    new FakeImportedReimbursementStore(),
                    new FakeClock(RunAt),
                    new FakeProviderDataCoverageStore()),
                new FakeImportFileTransactions(),
                new FakeClock(RunAt))
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(ReimbursementImportJobRunOutcome.Stopped, result.Outcome);
        Assert.Equal(ReimbursementImportJobState.Failed, jobs.Jobs[jobId].State);
        Assert.Equal(
            ProcessReimbursementImportJob.ArchiveUnreadableReason, jobs.Jobs[jobId].LastError);
    }

    /// <summary>A job that has already finished is nothing to do, not an error.</summary>
    [Fact]
    public async Task A_job_that_has_already_finished_is_nothing_to_do()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var jobId = await jobs.CreateAsync(NewJob(), CancellationToken.None);
        jobs.Jobs[jobId].State = ReimbursementImportJobState.Completed;

        var result = await Process(
                jobs,
                new FakeImportedReimbursementStore(),
                new FakeImportFileTransactions(),
                Entries("HASH-A"))
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(ReimbursementImportJobRunOutcome.NothingToDo, result.Outcome);
        Assert.Empty(jobs.Outcomes);
    }

    private static NewReimbursementImportJob NewJob() =>
        new("Reports_09-11-26.zip", ReimbursementImportFakes.Actor(), RunAt, [1, 2, 3]);

    private static ReimbursementArchiveXmlEntry Entry(
        int index, PendingReimbursementXmlFile? parsed) =>
        new(index, $"10-2026/Reimbursments.zip/report-{index}.xml", parsed);

    private static ReimbursementArchiveXmlEntry[] Entries(params string[] hashes) =>
        [.. hashes.Select((hash, index) => Entry(index, ReimbursementImportFakes.File(hash)))];

    private static ProcessReimbursementImportJob Process(
        IReimbursementImportJobStore jobs,
        FakeImportedReimbursementStore store,
        IReimbursementImportFileTransactions transactions,
        ReimbursementArchiveXmlEntry[] entries) =>
        new(
            jobs,
            FakeArchiveReader.Returning(entries),
            new ImportReimbursementXmlFile(
                store, new FakeClock(RunAt), new FakeProviderDataCoverageStore()),
            transactions,
            new FakeClock(RunAt));
}
