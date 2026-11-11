namespace Inventory.Domain.Imports;

/// <summary>
/// Where one uploaded reimbursement report archive has got to (issue #1149).
///
/// <para>The five states are the whole vocabulary and they are stored as the numbers below, so a
/// state is never inferred from the presence of an outcome row or from a count.</para>
/// </summary>
public enum ReimbursementImportJobState
{
    /// <summary>Accepted and waiting for the worker. Nothing has been imported yet.</summary>
    Queued = 0,

    /// <summary>
    /// The worker is working through it. A job left in this state by a restart is resumed, and that
    /// restart counts as one of its attempts.
    /// </summary>
    Processing = 1,

    /// <summary>Every XML in the archive was imported or recognised as an already-imported duplicate.</summary>
    Completed = 2,

    /// <summary>
    /// The job finished, and at least one XML in it could not be imported. The rest were, which is
    /// why this is a separate state rather than a failure: re-uploading imports only what is missing.
    /// </summary>
    CompletedWithFailures = 3,

    /// <summary>
    /// The job itself stopped - the attempts ran out, or the stored archive could no longer be read.
    /// Whatever it had already imported stays imported and keeps its outcome.
    /// </summary>
    Failed = 4,
}

/// <summary>What one XML file inside an archive turned into (issue #1149).</summary>
public enum ReimbursementImportFileOutcome
{
    /// <summary>Its reimbursement rows were committed by this job.</summary>
    Imported = 0,

    /// <summary>
    /// A file with these exact bytes was already imported by this business, so nothing was written
    /// again. Deduplication stays the byte-identical file hash it has always been.
    /// </summary>
    Skipped = 1,

    /// <summary>
    /// The file could not be imported, for the safe reason recorded beside it. It is a fact about
    /// one file, never a reason to retry the job.
    /// </summary>
    Failed = 2,
}

/// <summary>
/// Which states are final (issue #1149) - the one rule that decides when the uploaded bytes are
/// deleted and when the worker stops offering the job.
/// </summary>
public static class ReimbursementImportJobStates
{
    /// <summary>Whether this state is one nothing moves on from.</summary>
    public static bool IsFinal(ReimbursementImportJobState state) =>
        state is ReimbursementImportJobState.Completed
            or ReimbursementImportJobState.CompletedWithFailures
            or ReimbursementImportJobState.Failed;
}
