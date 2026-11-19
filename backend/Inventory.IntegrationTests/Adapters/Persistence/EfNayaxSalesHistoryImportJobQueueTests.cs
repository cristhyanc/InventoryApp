using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using InventoryApi.Tests.Application.Imports;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The one cross-business read the sales history import makes (issue #1304), and the tenant boundary
/// around everything else.
///
/// <para>It is a reviewed <c>IgnoreQueryFilters</c> path that is <strong>not in a request at
/// all</strong> - the reimbursement import queue's shape exactly (AGENTS.md § Tenant ownership and
/// data isolation) - so what these cases pin is how narrow it is: it answers a job key and a business
/// key, it is read-only, it takes no input a caller could widen it with, and every other statement
/// about a job is an ordinary filtered one that cannot see another business's work at all.</para>
/// </summary>
public class EfNayaxSalesHistoryImportJobQueueTests
{
    /// <summary>
    /// The discovery read runs with <em>no</em> business resolved - which is why it has to ignore
    /// the filters - and finds a job of whichever business uploaded it.
    /// </summary>
    [Fact]
    public async Task The_discovery_read_finds_a_job_of_any_business_under_a_denied_scope()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(2), NayaxSalesHistoryImportFixture.BusinessB);

        await using var denied = fixture.DeniedContext();
        var due = await NayaxSalesHistoryImportFixture.Queue(denied)
            .FindNextDueAsync(CancellationToken.None);

        Assert.Equal(
            new DueNayaxSalesHistoryImportJob(jobId, NayaxSalesHistoryImportFixture.BusinessB), due);
    }

    /// <summary>
    /// And nothing else about the job is reachable under that denied scope: the ordinary filtered
    /// reads see no job, no sale and no pending rebuild at all, and the store's own reads answer
    /// nothing either - because they carry no business predicate of their own and the central filter
    /// is the whole boundary.
    /// </summary>
    [Fact]
    public async Task Nothing_but_the_two_keys_is_readable_under_that_denied_scope()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(2), NayaxSalesHistoryImportFixture.BusinessB);

        await using (var work = fixture.Context(NayaxSalesHistoryImportFixture.BusinessB))
            await fixture.Process(work).Handle(jobId, CancellationToken.None);

        await using var denied = fixture.DeniedContext();

        Assert.Empty(await denied.NayaxSalesHistoryImportJobs.ToListAsync());
        Assert.Empty(await denied.NayaxSales.ToListAsync());
        Assert.Empty(await denied.PendingInventoryCostRebuilds.ToListAsync());

        Assert.Empty(
            await NayaxSalesHistoryImportFixture.Jobs(denied)
                .ListRecentAsync(10, CancellationToken.None));
    }

    /// <summary>The oldest upload is taken first, so a backlog is worked through in order.</summary>
    [Fact]
    public async Task The_oldest_waiting_job_is_taken_first_across_businesses()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();

        var newer = await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(2),
            NayaxSalesHistoryImportFixture.BusinessA,
            NayaxSalesHistoryImportFixture.UploadedAt.AddMinutes(10));
        var older = await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(2),
            NayaxSalesHistoryImportFixture.BusinessB,
            NayaxSalesHistoryImportFixture.UploadedAt);

        await using var denied = fixture.DeniedContext();
        var due = await NayaxSalesHistoryImportFixture.Queue(denied)
            .FindNextDueAsync(CancellationToken.None);

        Assert.Equal(older, due!.Value.JobId);
        Assert.NotEqual(newer, due.Value.JobId);
        Assert.Equal(NayaxSalesHistoryImportFixture.BusinessB, due.Value.BusinessId);
    }

    /// <summary>A job left <c>Processing</c> by a restart is waiting, which is what resumes it.</summary>
    [Fact]
    public async Task A_job_left_processing_is_waiting_again()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(2));

        await using (var crash = fixture.Unrestricted())
        {
            var row = await crash.NayaxSalesHistoryImportJobs
                .IgnoreQueryFilters()
                .SingleAsync(job => job.Id == jobId);
            row.State = NayaxSalesHistoryImportJobState.Processing;
            await crash.SaveChangesAsync();
        }

        await using var denied = fixture.DeniedContext();

        Assert.Equal(
            jobId,
            (await NayaxSalesHistoryImportFixture.Queue(denied)
                .FindNextDueAsync(CancellationToken.None))!.Value.JobId);
    }

    /// <summary>A finished job is never offered again, whatever it finished as.</summary>
    [Theory]
    [InlineData(NayaxSalesHistoryImportJobState.Completed)]
    [InlineData(NayaxSalesHistoryImportJobState.Failed)]
    public async Task A_finished_job_is_never_offered(NayaxSalesHistoryImportJobState state)
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(2));

        await using (var finish = fixture.Unrestricted())
        {
            var row = await finish.NayaxSalesHistoryImportJobs
                .IgnoreQueryFilters()
                .SingleAsync(job => job.Id == jobId);
            row.State = state;
            await finish.SaveChangesAsync();
        }

        await using var denied = fixture.DeniedContext();

        Assert.Null(
            await NayaxSalesHistoryImportFixture.Queue(denied).FindNextDueAsync(CancellationToken.None));
    }
}
