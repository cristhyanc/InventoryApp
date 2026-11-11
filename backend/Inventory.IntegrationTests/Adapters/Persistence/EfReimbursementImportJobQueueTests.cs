using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using InventoryApi.Tests.Application.Imports;
using InventoryApi.Tests.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The one cross-business read the uploaded reimbursement report import makes (issue #1149, the
/// repository owner's approval of 9 November 2026), and the tenant boundary around everything else.
///
/// <para>It is the <strong>ninth</strong> reviewed <c>IgnoreQueryFilters</c> path and the
/// <strong>second</strong> that is not in a request at all (AGENTS.md § Tenant ownership and data
/// isolation), so what these cases pin is exactly how narrow it is: it answers a job key and a
/// business key, it is read-only, it takes no input a caller could widen it with, and every other
/// statement about a job is an ordinary filtered one that cannot see another business's work at
/// all.</para>
/// </summary>
public class EfReimbursementImportJobQueueTests
{
    private static readonly DateTime FirstReportDay = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The discovery read runs with <em>no</em> business resolved - which is why it has to ignore
    /// the filters - and finds a job of whichever business uploaded it.
    /// </summary>
    [Fact]
    public async Task The_discovery_read_finds_a_job_of_any_business_under_a_denied_scope()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(Archive(), ReimbursementImportJobFixture.BusinessB);

        await using var denied = fixture.DeniedContext();
        var due = await fixture.Queue(denied).FindNextDueAsync(CancellationToken.None);

        Assert.Equal(
            new DueReimbursementImportJob(jobId, ReimbursementImportJobFixture.BusinessB), due);
    }

    /// <summary>
    /// And nothing else about the job is reachable under that denied scope: the ordinary filtered
    /// reads see no job, no file outcome and no imported file at all.
    /// </summary>
    [Fact]
    public async Task Nothing_but_the_two_keys_is_readable_under_that_denied_scope()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(Archive(), ReimbursementImportJobFixture.BusinessB);

        await using var denied = fixture.DeniedContext();

        Assert.Empty(await denied.ReimbursementImportJobs.ToListAsync());
        Assert.Empty(await denied.ReimbursementImportJobFiles.ToListAsync());
        Assert.Empty(await denied.ImportedFiles.ToListAsync());

        // The store's own reads answer nothing either, because they carry no business predicate of
        // their own: the central filter is the whole boundary.
        var store = ReimbursementImportJobFixture.Jobs(denied);
        Assert.Empty(await store.ListRecentAsync(20, CancellationToken.None));
        Assert.Null(await store.FindAsync(jobId, CancellationToken.None));
    }

    /// <summary>The oldest upload is taken first, so a backlog is worked through in order.</summary>
    [Fact]
    public async Task The_oldest_due_job_is_taken_first_across_businesses()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();

        var newer = await fixture.QueueJobAsync(
            Archive(),
            ReimbursementImportJobFixture.BusinessA,
            ReimbursementImportJobFixture.UploadedAt.AddMinutes(10));
        var older = await fixture.QueueJobAsync(
            Archive(),
            ReimbursementImportJobFixture.BusinessB,
            ReimbursementImportJobFixture.UploadedAt);

        await using var denied = fixture.DeniedContext();
        var due = await fixture.Queue(denied).FindNextDueAsync(CancellationToken.None);

        Assert.Equal(older, due!.Value.JobId);
        Assert.NotEqual(newer, due.Value.JobId);
        Assert.Equal(ReimbursementImportJobFixture.BusinessB, due.Value.BusinessId);
    }

    /// <summary>
    /// <strong>A job waiting to retry never holds up another</strong>, of its own business or of any
    /// other: the read takes the oldest job that is <em>due</em>, so the one backing off is simply
    /// not offered until its not-before instant has passed.
    /// </summary>
    [Fact]
    public async Task A_job_waiting_to_retry_does_not_hold_up_another_businesss_job()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();

        var waiting = await fixture.QueueJobAsync(
            Archive(),
            ReimbursementImportJobFixture.BusinessA,
            ReimbursementImportJobFixture.UploadedAt);
        var behindIt = await fixture.QueueJobAsync(
            Archive(),
            ReimbursementImportJobFixture.BusinessB,
            ReimbursementImportJobFixture.UploadedAt.AddMinutes(1));

        await using (var db = fixture.Context())
        {
            await ReimbursementImportJobFixture.Jobs(db).RequeueAsync(
                waiting,
                fixture.Clock.UtcNow.AddMinutes(15),
                "the database could not be written",
                CancellationToken.None);
        }

        await using (var denied = fixture.DeniedContext())
        {
            // The older job is not due, so the newer one of another business is next.
            var due = await fixture.Queue(denied).FindNextDueAsync(CancellationToken.None);

            Assert.Equal(behindIt, due!.Value.JobId);
        }

        // And once its wait is over, it is offered again.
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(16);

        await using (var later = fixture.DeniedContext())
        {
            var due = await fixture.Queue(later).FindNextDueAsync(CancellationToken.None);

            Assert.Equal(waiting, due!.Value.JobId);
        }
    }

    /// <summary>A job left <c>Processing</c> by a restart is due, which is what resumes it.</summary>
    [Fact]
    public async Task A_job_left_processing_is_due_again()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(Archive());

        await using (var crash = fixture.Unrestricted())
        {
            var row = await crash.ReimbursementImportJobs.SingleAsync(job => job.Id == jobId);
            row.State = ReimbursementImportJobState.Processing;
            await crash.SaveChangesAsync();
        }

        await using var denied = fixture.DeniedContext();

        Assert.Equal(
            jobId,
            (await fixture.Queue(denied).FindNextDueAsync(CancellationToken.None))!.Value.JobId);
    }

    /// <summary>A finished job is never offered again, whatever it finished as.</summary>
    [Theory]
    [InlineData(ReimbursementImportJobState.Completed)]
    [InlineData(ReimbursementImportJobState.CompletedWithFailures)]
    [InlineData(ReimbursementImportJobState.Failed)]
    public async Task A_finished_job_is_never_offered(ReimbursementImportJobState state)
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(Archive());

        await using (var finish = fixture.Unrestricted())
        {
            var row = await finish.ReimbursementImportJobs.SingleAsync(job => job.Id == jobId);
            row.State = state;
            await finish.SaveChangesAsync();
        }

        await using var denied = fixture.DeniedContext();

        Assert.Null(await fixture.Queue(denied).FindNextDueAsync(CancellationToken.None));
    }

    /// <summary>Nothing uploaded means nothing due, rather than an empty-scope failure.</summary>
    [Fact]
    public async Task Nothing_uploaded_means_nothing_due()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();

        await using var denied = fixture.DeniedContext();

        Assert.Null(await fixture.Queue(denied).FindNextDueAsync(CancellationToken.None));
    }

    /// <summary>
    /// <strong>A job of another business is not visible.</strong> The status reads are ordinary
    /// tenant-scoped statements, so business A's job cannot be listed or fetched as business B -
    /// which is what makes the endpoint's <c>404</c> the truth rather than a comparison.
    /// </summary>
    [Fact]
    public async Task A_job_of_another_business_is_not_visible_to_the_status_reads()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var jobOfA = await fixture.QueueJobAsync(Archive(), ReimbursementImportJobFixture.BusinessA);

        await using var asB = fixture.Context(ReimbursementImportJobFixture.BusinessB);
        var store = ReimbursementImportJobFixture.Jobs(asB);

        Assert.Null(await store.FindAsync(jobOfA, CancellationToken.None));
        Assert.Empty(await store.ListRecentAsync(20, CancellationToken.None));
    }

    /// <summary>
    /// And neither can the other business's worker touch it: the claim is tenant-scoped too, so a
    /// scope resolved to the wrong business finds nothing to claim.
    /// </summary>
    [Fact]
    public async Task A_job_of_another_business_cannot_be_claimed_in_the_wrong_scope()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var jobOfA = await fixture.QueueJobAsync(Archive(), ReimbursementImportJobFixture.BusinessA);

        await using (var asB = fixture.Context(ReimbursementImportJobFixture.BusinessB))
        {
            var claimed = await ReimbursementImportJobFixture.Jobs(asB).ClaimAsync(
                jobOfA,
                ReimbursementImportRetryPolicy.MaxAttempts,
                fixture.Clock.UtcNow,
                CancellationToken.None);

            Assert.Equal(ReimbursementImportJobClaimState.NotClaimable, claimed.State);
        }

        // Untouched: still queued, still on its first attempt, still holding its archive.
        var job = await fixture.JobRowAsync(jobOfA);
        Assert.Equal(ReimbursementImportJobState.Queued, job.State);
        Assert.Equal(0, job.Attempts);
        Assert.NotNull(job.Archive);
    }

    /// <summary>
    /// Two businesses importing the same archive each import their own copy: file-hash dedupe is per
    /// business, so one business's import never tells another its own is a duplicate.
    /// </summary>
    [Fact]
    public async Task Two_businesses_importing_the_same_archive_each_import_their_own_copy()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var archive = Archive();

        var jobOfA = await fixture.QueueJobAsync(archive, ReimbursementImportJobFixture.BusinessA);
        var jobOfB = await fixture.QueueJobAsync(archive, ReimbursementImportJobFixture.BusinessB);

        await using (var asA = fixture.Context(ReimbursementImportJobFixture.BusinessA))
        {
            await fixture.Process(asA).Handle(jobOfA, CancellationToken.None);
        }

        await using (var asB = fixture.Context(ReimbursementImportJobFixture.BusinessB))
        {
            await fixture.Process(asB).Handle(jobOfB, CancellationToken.None);
        }

        Assert.Equal(
            ReimbursementImportFileOutcome.Imported,
            Assert.Single(await fixture.FileRowsAsync(jobOfA)).Outcome);
        Assert.Equal(
            ReimbursementImportFileOutcome.Imported,
            Assert.Single(await fixture.FileRowsAsync(jobOfB)).Outcome);

        await using var verify = fixture.Unrestricted();
        Assert.Equal(
            [ReimbursementImportJobFixture.BusinessA, ReimbursementImportJobFixture.BusinessB],
            await verify.ImportedFiles
                .AsNoTracking()
                .OrderBy(file => file.BusinessId)
                .Select(file => file.BusinessId)
                .ToListAsync());
    }

    private static byte[] Archive() =>
        ReimbursementArchiveFixtures.FlatArchive(
            ("report_9900001_20261005.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)));
}
