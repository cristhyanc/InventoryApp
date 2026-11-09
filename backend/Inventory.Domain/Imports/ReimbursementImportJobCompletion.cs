namespace Inventory.Domain.Imports;

/// <summary>
/// How many of a job's files turned into what (issue #1149).
///
/// <para><strong>These are always counted from the recorded outcomes</strong>, never kept as a
/// running total on the job: a total maintained beside the outcomes is a total that can disagree
/// with them after a restart, and this record exists for financial imports where "what actually
/// landed?" has to be answerable from the rows themselves.</para>
/// </summary>
/// <param name="Imported">Files whose reimbursement rows this job committed.</param>
/// <param name="Skipped">Files already imported by this business, so nothing was written again.</param>
/// <param name="Failed">Files that could not be imported.</param>
public readonly record struct ReimbursementImportFileCounts(int Imported, int Skipped, int Failed)
{
    /// <summary>How many files have an outcome at all.</summary>
    public int Total => Imported + Skipped + Failed;
}

/// <summary>
/// The one rule that decides what a finished reimbursement import job is called (issue #1149).
/// </summary>
public static class ReimbursementImportJobCompletion
{
    /// <summary>
    /// The state a job reaches once every file in it has an outcome:
    /// <see cref="ReimbursementImportJobState.Completed"/> when none of them failed, and
    /// <see cref="ReimbursementImportJobState.CompletedWithFailures"/> when any did.
    ///
    /// <para>A job all of whose files were skipped is <em>completed</em>, not failed: re-uploading
    /// the same archive is a legitimate thing to do and "everything was already imported" is a
    /// success. A job with no files at all cannot exist - an archive with no XML is refused at
    /// upload - and is answered as completed rather than as a special case nobody can reach.</para>
    /// </summary>
    public static ReimbursementImportJobState StateFor(ReimbursementImportFileCounts counts) =>
        counts.Failed > 0
            ? ReimbursementImportJobState.CompletedWithFailures
            : ReimbursementImportJobState.Completed;

    /// <summary>The counts of a sequence of recorded outcomes.</summary>
    public static ReimbursementImportFileCounts Count(
        IEnumerable<ReimbursementImportFileOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        var imported = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var outcome in outcomes)
        {
            switch (outcome)
            {
                case ReimbursementImportFileOutcome.Imported:
                    imported++;
                    break;
                case ReimbursementImportFileOutcome.Skipped:
                    skipped++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        return new ReimbursementImportFileCounts(imported, skipped, failed);
    }
}
