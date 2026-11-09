namespace Inventory.Application.Imports;

/// <summary>
/// The caller's own business's recent reimbursement import jobs (issue #1149).
///
/// <para><strong>The page size is the server's.</strong> The request carries nothing - no page, no
/// filter, no business, no state to ask for - so there is no input to widen: the twenty most
/// recently uploaded jobs are what this answers with, which is what the Reimbursement Report page
/// (issue #1152) shows.</para>
///
/// <para><strong>The counts come from the outcome rows.</strong> Nothing here or in the adapter
/// keeps a running total, so what a job reports is always what it actually recorded.</para>
/// </summary>
public sealed class ListReimbursementImportJobs
{
    /// <summary>How many jobs the list answers with: the latest twenty.</summary>
    public const int PageSize = 20;

    private readonly IReimbursementImportJobStore _jobs;

    public ListReimbursementImportJobs(IReimbursementImportJobStore jobs)
    {
        _jobs = jobs;
    }

    /// <summary>The latest jobs of the caller's own business, newest first.</summary>
    public Task<IReadOnlyList<ReimbursementImportJobSummary>> Handle(
        CancellationToken cancellationToken) =>
        _jobs.ListRecentAsync(PageSize, cancellationToken);
}

/// <summary>
/// One of the caller's own business's reimbursement import jobs, with every file outcome recorded
/// for it (issue #1149).
///
/// <para><strong>A job of another business is simply not found.</strong> The lookup is tenant-scoped
/// by the central mechanism, so there is no comparison to get wrong and nothing that could answer
/// with somebody else's job: the boundary returns <see langword="null"/> and the endpoint answers
/// <c>404</c>.</para>
/// </summary>
public sealed class GetReimbursementImportJob
{
    private readonly IReimbursementImportJobStore _jobs;

    public GetReimbursementImportJob(IReimbursementImportJobStore jobs)
    {
        _jobs = jobs;
    }

    /// <summary>The job, or <see langword="null"/> when this business has no job with that key.</summary>
    public Task<ReimbursementImportJobDetail?> Handle(
        int jobId, CancellationToken cancellationToken) =>
        _jobs.FindAsync(jobId, cancellationToken);
}
