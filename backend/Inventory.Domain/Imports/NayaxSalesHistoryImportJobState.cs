namespace Inventory.Domain.Imports;

/// <summary>
/// Where one uploaded Nayax sales history has got to (issue #1304).
///
/// <para>The four states are the whole vocabulary and they are stored as the numbers below, so a
/// state is never inferred from a progress count. There is deliberately no
/// "completed with failures" state as <see cref="ReimbursementImportJobState"/> has: a history's
/// unit of work is a <em>row</em>, and a row the file states something unreadable in is counted and
/// reported like the normal upload's invalid rows rather than making the whole import a failure.
/// What <see cref="Failed"/> means here is that the job itself stopped.</para>
/// </summary>
public enum NayaxSalesHistoryImportJobState
{
    /// <summary>Accepted and waiting for the worker. No row has been imported yet.</summary>
    Queued = 0,

    /// <summary>
    /// The worker is working through it. A job left in this state by a restart is resumed from the
    /// rows it had already committed, and that restart counts as one of its attempts.
    /// </summary>
    Processing = 1,

    /// <summary>Every row of the file has been read and applied.</summary>
    Completed = 2,

    /// <summary>
    /// The job itself stopped - its attempts ran out, or its stored file could no longer be read.
    /// <strong>Every batch it had already committed stays committed</strong>, the progress counts say
    /// how far it got, and the recorded reason says so; re-uploading the same file imports the rest
    /// and changes nothing about the rows that already landed, because a row is matched by its
    /// transaction id.
    /// </summary>
    Failed = 3,
}

/// <summary>
/// Which states are final (issue #1304) - the one rule that decides when the uploaded bytes are
/// deleted, when the worker stops offering the job, and when another upload is allowed.
/// </summary>
public static class NayaxSalesHistoryImportJobStates
{
    /// <summary>Whether this state is one nothing moves on from.</summary>
    public static bool IsFinal(NayaxSalesHistoryImportJobState state) =>
        state is NayaxSalesHistoryImportJobState.Completed or NayaxSalesHistoryImportJobState.Failed;
}
