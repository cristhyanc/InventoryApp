using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using InventoryApi.Tests.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// The claims about the uploaded reimbursement report import that only a real database can settle
/// (issue #1149): that each file's rows, its coverage and its outcome commit <strong>together</strong>,
/// that a rolled-back file leaves neither, that a restart changes nothing about what already
/// committed, and that the counts come out of the outcome rows.
///
/// <para>Every fixture figure and account id is made up; see
/// <see cref="ReimbursementArchiveFixtures"/>.</para>
/// </summary>
public class ReimbursementImportJobProcessingTests
{
    private static readonly DateTime FirstReportDay = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The real nested archive, imported end to end: every report XML becomes reimbursement rows,
    /// one coverage record per declared period, and one <c>Imported</c> outcome.
    /// </summary>
    [Fact]
    public async Task The_real_nested_archive_imports_every_report_with_its_coverage_and_outcome()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var archive = ReimbursementArchiveFixtures.NestedArchive(
            ("10-2026", [FirstReportDay, FirstReportDay.AddDays(7)]),
            ("11-2026", [FirstReportDay.AddDays(28)]));
        var jobId = await fixture.QueueJobAsync(archive);

        ReimbursementImportJobRunResult result;
        await using (var db = fixture.Context())
        {
            result = await fixture.Process(db).Handle(jobId, CancellationToken.None);
        }

        Assert.Equal(ReimbursementImportJobRunOutcome.Finished, result.Outcome);
        Assert.Equal(new ReimbursementImportFileCounts(3, 0, 0), result.Counts);

        var job = await fixture.JobRowAsync(jobId);
        Assert.Equal(ReimbursementImportJobState.Completed, job.State);
        Assert.Equal(ReimbursementImportJobFixture.RunAt, job.FinishedAtUtc);

        // The uploaded bytes are deleted the moment the job is final.
        Assert.Null(job.Archive);

        await using var verify = fixture.Unrestricted();
        Assert.Equal(3, await verify.ImportedFiles.CountAsync());
        Assert.Equal(3, await verify.ImportedReimbursements.CountAsync());
        Assert.Equal(3, await verify.ProviderDataCoverages.CountAsync());

        // Every row belongs to the business whose job it was, stamped centrally.
        Assert.All(
            await verify.ImportedFiles.ToListAsync(),
            file => Assert.Equal(ReimbursementImportJobFixture.BusinessA, file.BusinessId));

        var outcomes = await fixture.FileRowsAsync(jobId);
        Assert.All(outcomes, outcome =>
            Assert.Equal(ReimbursementImportFileOutcome.Imported, outcome.Outcome));
        Assert.All(outcomes, outcome =>
            Assert.Equal(ReimbursementImportJobFixture.BusinessA, outcome.BusinessId));
        Assert.Equal(
            [
                "10-2026/Reimbursments.zip/report_9900001_20261005.xml",
                "10-2026/Reimbursments.zip/report_9900001_20261012.xml",
                "11-2026/Reimbursments.zip/report_9900001_20261102.xml",
            ],
            outcomes.Select(outcome => outcome.EntryPath));
    }

    /// <summary>
    /// Re-uploading the same archive imports nothing again: every file comes out <c>Skipped</c>,
    /// no reimbursement row is added, and no second coverage record is written.
    /// </summary>
    [Fact]
    public async Task Re_uploading_the_same_archive_skips_every_file_and_writes_nothing_again()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var archive = ReimbursementArchiveFixtures.NestedArchive(
            ("10-2026", [FirstReportDay, FirstReportDay.AddDays(7)]));

        var firstJob = await fixture.QueueJobAsync(archive);
        await using (var db = fixture.Context())
        {
            await fixture.Process(db).Handle(firstJob, CancellationToken.None);
        }

        var secondJob = await fixture.QueueJobAsync(archive);
        ReimbursementImportJobRunResult result;
        await using (var db = fixture.Context())
        {
            result = await fixture.Process(db).Handle(secondJob, CancellationToken.None);
        }

        Assert.Equal(new ReimbursementImportFileCounts(0, 2, 0), result.Counts);
        Assert.Equal(
            ReimbursementImportJobState.Completed, (await fixture.JobRowAsync(secondJob)).State);

        await using var verify = fixture.Unrestricted();
        Assert.Equal(2, await verify.ImportedFiles.CountAsync());
        Assert.Equal(2, await verify.ImportedReimbursements.CountAsync());
        Assert.Equal(2, await verify.ProviderDataCoverages.CountAsync());

        Assert.All(
            await fixture.FileRowsAsync(secondJob),
            outcome => Assert.Equal(ReimbursementImportFileOutcome.Skipped, outcome.Outcome));
    }

    /// <summary>
    /// One invalid XML among valid ones imports the rest and records that one file's own failure,
    /// with a safe reason; the job is completed-with-failures.
    /// </summary>
    [Fact]
    public async Task One_invalid_xml_among_valid_ones_leaves_the_rest_imported()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var archive = ReimbursementArchiveFixtures.FlatArchive(
            ("report_9900001_20261005.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)),
            ("report_9900001_20261012.xml", "<row unterminated=\"yes\""),
            ("report_9900001_20261019.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay.AddDays(14), FirstReportDay.AddDays(20), 419m)));
        var jobId = await fixture.QueueJobAsync(archive);

        await using (var db = fixture.Context())
        {
            await fixture.Process(db).Handle(jobId, CancellationToken.None);
        }

        Assert.Equal(
            ReimbursementImportJobState.CompletedWithFailures,
            (await fixture.JobRowAsync(jobId)).State);

        var outcomes = await fixture.FileRowsAsync(jobId);
        Assert.Equal(
            [
                ReimbursementImportFileOutcome.Imported,
                ReimbursementImportFileOutcome.Failed,
                ReimbursementImportFileOutcome.Imported,
            ],
            outcomes.Select(outcome => outcome.Outcome));
        Assert.Equal(
            ProcessReimbursementImportJob.UnreadableFileReason,
            outcomes[1].Reason);

        await using var verify = fixture.Unrestricted();
        Assert.Equal(2, await verify.ImportedFiles.CountAsync());
    }

    /// <summary>
    /// <strong>A failure inside a file's transaction leaves neither rows nor an outcome.</strong>
    /// The first file committed; the second staged its reimbursement rows and then failed, and
    /// nothing of it survives - no imported file, no reimbursement row, no coverage record and no
    /// outcome row. The job is queued again for its next attempt.
    /// </summary>
    [Fact]
    public async Task A_failure_inside_a_files_transaction_leaves_neither_rows_nor_an_outcome()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var archive = ReimbursementArchiveFixtures.FlatArchive(
            ("report_9900001_20261005.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)),
            ("report_9900001_20261012.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay.AddDays(7), FirstReportDay.AddDays(13), 412m)));
        var jobId = await fixture.QueueJobAsync(archive);

        var secondHash = HashOf(archive, index: 1);

        ReimbursementImportJobRunResult result;
        await using (var db = fixture.Context())
        {
            result = await fixture
                .Process(db, new FailingImportedReimbursementStore(db, secondHash))
                .Handle(jobId, CancellationToken.None);
        }

        Assert.Equal(ReimbursementImportJobRunOutcome.Requeued, result.Outcome);

        var job = await fixture.JobRowAsync(jobId);
        Assert.Equal(ReimbursementImportJobState.Queued, job.State);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(ReimbursementImportJobFixture.RunAt.AddMinutes(1), job.NextAttemptAtUtc);

        // The archive stays, because the next attempt imports from it.
        Assert.NotNull(job.Archive);

        // Exactly the first file landed: one imported file, one coverage record, one outcome.
        await using var verify = fixture.Unrestricted();
        Assert.Equal(1, await verify.ImportedFiles.CountAsync());
        Assert.Equal(1, await verify.ProviderDataCoverages.CountAsync());

        var outcome = Assert.Single(await fixture.FileRowsAsync(jobId));
        Assert.Equal(0, outcome.EntryIndex);
        Assert.Equal(ReimbursementImportFileOutcome.Imported, outcome.Outcome);
    }

    /// <summary>
    /// <strong>A restart exactly after one file's transaction commits changes nothing about it.</strong>
    /// The second attempt processes only the file with no outcome, the first still reports
    /// <c>Imported</c>, the counts are unchanged, and nothing is imported twice.
    /// </summary>
    [Fact]
    public async Task A_restart_after_one_file_commits_leaves_that_file_imported_and_imports_nothing_twice()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var archive = ReimbursementArchiveFixtures.FlatArchive(
            ("report_9900001_20261005.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)),
            ("report_9900001_20261012.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay.AddDays(7), FirstReportDay.AddDays(13), 412m)));
        var jobId = await fixture.QueueJobAsync(archive);
        var secondHash = HashOf(archive, index: 1);

        // The first attempt commits the first file and then the process dies: the job is left
        // Processing, which is exactly the state a restart finds.
        await using (var db = fixture.Context())
        {
            await fixture
                .Process(db, new FailingImportedReimbursementStore(db, secondHash))
                .Handle(jobId, CancellationToken.None);
        }

        await using (var crash = fixture.Unrestricted())
        {
            var row = await crash.ReimbursementImportJobs.SingleAsync(job => job.Id == jobId);
            row.State = ReimbursementImportJobState.Processing;
            row.NextAttemptAtUtc = null;
            await crash.SaveChangesAsync();
        }

        var importedFileIdBefore = await ImportedFileIdAsync(fixture);

        ReimbursementImportJobRunResult result;
        await using (var db = fixture.Context())
        {
            result = await fixture.Process(db).Handle(jobId, CancellationToken.None);
        }

        // Only the file with no outcome went through the pipeline.
        Assert.Equal(1, result.FilesProcessedNow);
        Assert.Equal(new ReimbursementImportFileCounts(2, 0, 0), result.Counts);
        Assert.Equal(
            ReimbursementImportJobState.Completed, (await fixture.JobRowAsync(jobId)).State);

        var outcomes = await fixture.FileRowsAsync(jobId);
        Assert.Equal(2, outcomes.Count);

        // The file committed before the restart still reports Imported - not Skipped - and its
        // stored row is the same one, so nothing was imported a second time.
        Assert.Equal(ReimbursementImportFileOutcome.Imported, outcomes[0].Outcome);
        Assert.Equal(ReimbursementImportFileOutcome.Imported, outcomes[1].Outcome);
        Assert.Equal(importedFileIdBefore, await ImportedFileIdAsync(fixture, 0));

        await using var verify = fixture.Unrestricted();
        Assert.Equal(2, await verify.ImportedFiles.CountAsync());
        Assert.Equal(2, await verify.ImportedReimbursements.CountAsync());
    }

    /// <summary>
    /// A file with no outcome whose hash is already stored is reported <c>Skipped</c>, which is
    /// correct rather than a guess: had this job imported it, the same transaction would have
    /// written its outcome.
    /// </summary>
    [Fact]
    public async Task A_file_with_no_outcome_whose_hash_exists_is_reported_skipped()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var report = ReimbursementArchiveFixtures.ReportXml(
            FirstReportDay, FirstReportDay.AddDays(6), 405m);

        // Another upload imported the same bytes first.
        var earlier = await fixture.QueueJobAsync(
            ReimbursementArchiveFixtures.FlatArchive(("report.xml", report)));
        await using (var db = fixture.Context())
        {
            await fixture.Process(db).Handle(earlier, CancellationToken.None);
        }

        var jobId = await fixture.QueueJobAsync(
            ReimbursementArchiveFixtures.FlatArchive(("report_9900001_20261005.xml", report)));
        await using (var db = fixture.Context())
        {
            await fixture.Process(db).Handle(jobId, CancellationToken.None);
        }

        Assert.Equal(
            ReimbursementImportFileOutcome.Skipped,
            Assert.Single(await fixture.FileRowsAsync(jobId)).Outcome);

        await using var verify = fixture.Unrestricted();
        Assert.Equal(1, await verify.ImportedFiles.CountAsync());
    }

    /// <summary>
    /// Two copies of the same report inside one archive: the first is imported and the second is a
    /// duplicate of it, because each file commits before the next one's hash is checked.
    /// </summary>
    [Fact]
    public async Task The_same_report_twice_in_one_archive_imports_once()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var report = ReimbursementArchiveFixtures.ReportXml(
            FirstReportDay, FirstReportDay.AddDays(6), 405m);
        var jobId = await fixture.QueueJobAsync(ReimbursementArchiveFixtures.FlatArchive(
            ("copy-one/report_9900001_20261005.xml", report),
            ("copy-two/report_9900001_20261005.xml", report)));

        await using (var db = fixture.Context())
        {
            await fixture.Process(db).Handle(jobId, CancellationToken.None);
        }

        Assert.Equal(
            [ReimbursementImportFileOutcome.Imported, ReimbursementImportFileOutcome.Skipped],
            (await fixture.FileRowsAsync(jobId)).Select(outcome => outcome.Outcome));

        await using var verify = fixture.Unrestricted();
        Assert.Equal(1, await verify.ImportedFiles.CountAsync());
    }

    /// <summary>
    /// The counts the status read reports are computed from the outcome rows, not from anything
    /// stored on the job - which is what keeps them true after a restart.
    /// </summary>
    [Fact]
    public async Task The_reported_counts_come_from_the_outcome_rows()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var archive = ReimbursementArchiveFixtures.FlatArchive(
            ("a.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)),
            ("b.xml", "not xml at all"));
        var jobId = await fixture.QueueJobAsync(archive);

        await using (var db = fixture.Context())
        {
            await fixture.Process(db).Handle(jobId, CancellationToken.None);
        }

        await using var read = fixture.Context();
        var detail = await ReimbursementImportJobFixture.Jobs(read)
            .FindAsync(jobId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(1, detail.Job.ImportedFiles);
        Assert.Equal(0, detail.Job.SkippedFiles);
        Assert.Equal(1, detail.Job.FailedFiles);
        Assert.Equal(2, detail.Files.Count);
        Assert.Equal(ReimbursementImportJobState.CompletedWithFailures, detail.Job.State);
    }

    /// <summary>
    /// A second outcome for one file is refused by the database, so the counts cannot be inflated by
    /// a second vote.
    /// </summary>
    [Fact]
    public async Task A_second_outcome_for_one_file_is_refused()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(ReimbursementArchiveFixtures.FlatArchive(
            ("report.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m))));

        await using var db = fixture.Context();
        var jobs = ReimbursementImportJobFixture.Jobs(db);

        await jobs.RecordFileOutcomeAsync(
            jobId,
            new ReimbursementImportFileResult(
                0, "report.xml", ReimbursementImportFileOutcome.Imported, null),
            ReimbursementImportJobFixture.RunAt,
            CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => jobs.RecordFileOutcomeAsync(
            jobId,
            new ReimbursementImportFileResult(
                0, "report.xml", ReimbursementImportFileOutcome.Failed, "a second vote"),
            ReimbursementImportJobFixture.RunAt,
            CancellationToken.None));
    }

    private static string HashOf(byte[] archive, int index) =>
        ReimbursementImportJobFixture.Reader().Read(archive).XmlFiles[index].Parsed!.ContentHash;

    private static async Task<int> ImportedFileIdAsync(
        ReimbursementImportJobFixture fixture, int skip = 0)
    {
        await using var db = fixture.Unrestricted();

        return await db.ImportedFiles
            .AsNoTracking()
            .OrderBy(file => file.Id)
            .Skip(skip)
            .Select(file => file.Id)
            .FirstAsync();
    }
}
