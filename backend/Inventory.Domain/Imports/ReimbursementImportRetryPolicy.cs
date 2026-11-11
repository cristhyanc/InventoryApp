namespace Inventory.Domain.Imports;

/// <summary>
/// When a reimbursement import job tries again, and when it stops trying (issue #1149).
///
/// <para><strong>What is retried is deliberately narrow.</strong> A file this application cannot
/// read is that file's own recorded outcome and is never retried - trying a malformed XML four more
/// times produces the same malformed XML. What a retry is for is the database being momentarily
/// unavailable to a single-writer SQLite file, which is transient by nature and is exactly the
/// condition another attempt fixes.</para>
///
/// <para><strong>A restart counts.</strong> An attempt is counted when it <em>starts</em>, so a
/// process that died mid-job has already spent one: resuming the job is its next attempt, not a free
/// one. That is what bounds a job that crashes the process it runs in.</para>
///
/// <para><strong>The back-off is deliberately long.</strong> 1 minute, 5 minutes, 15 minutes, then
/// an hour: nothing is waiting on a settlement import, and a database under pressure is helped by
/// being left alone rather than by being asked again immediately.</para>
/// </summary>
public static class ReimbursementImportRetryPolicy
{
    /// <summary>
    /// How many attempts a job gets in total: five. The fifth failure is final.
    /// </summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// The safe reason a job that has used all its attempts records. It says what happened and what
    /// to do, and carries no XML content, figure, payment detail or database message.
    /// </summary>
    public const string AttemptsExhaustedReason =
        "Stopped after repeated database errors. Upload the ZIP again.";

    /// <summary>
    /// The wait before attempt <c>n + 1</c>, indexed by the number of attempts already made.
    /// </summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
    ];

    /// <summary>
    /// Whether a job that has made <paramref name="attempts"/> attempts has run out of them.
    /// </summary>
    public static bool IsExhausted(int attempts) => attempts >= MaxAttempts;

    /// <summary>
    /// When a job that has just failed its <paramref name="attempts"/>-th attempt may be picked up
    /// again, or <see langword="null"/> when it has none left - in which case the job is
    /// <see cref="ReimbursementImportJobState.Failed"/> with
    /// <see cref="AttemptsExhaustedReason"/> rather than queued for a wait nobody ends.
    /// </summary>
    /// <param name="attempts">How many attempts have been made, including the one that just failed.</param>
    /// <param name="nowUtc">The instant the failure was handled.</param>
    public static DateTime? NextAttemptAtUtc(int attempts, DateTime nowUtc)
    {
        if (IsExhausted(attempts)) return null;

        // An attempts value below one cannot happen - an attempt is counted when it starts - and is
        // treated as the first rather than as a reason to index outside the schedule.
        var index = Math.Clamp(attempts, 1, Backoff.Length) - 1;

        return nowUtc + Backoff[index];
    }

    /// <summary>The whole back-off schedule, so a test and the documentation read one list.</summary>
    public static IReadOnlyList<TimeSpan> Schedule => Backoff;
}
