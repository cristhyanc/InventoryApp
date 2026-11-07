namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// Why a business's sales are <em>not</em> arriving from SQS reliably enough for the Dashboard to
/// stop reading Nayax on load (issue #1106, child of #1100).
///
/// <para>Each value names one of the three conditions, so a person reading the response or a log can
/// tell "nobody recorded this business's sender" from "the queue stopped answering" from "nothing has
/// verified this business's sales recently". There is deliberately no reason for a quiet queue: time
/// since the last message is not a condition at all.</para>
/// </summary>
public enum NayaxSqsNotLiveReason
{
    /// <summary>
    /// No sender id is recorded for this business - no row has ever existed, or a Super Admin's
    /// "Remove" cleared the one it had. Nothing on the shared queue can be attributed to it.
    /// </summary>
    NoSender = 0,

    /// <summary>A sender id is recorded and the mapping is switched off, so nothing is attributed.</summary>
    SenderDisabled = 1,

    /// <summary>
    /// The queue reader has no successful poll inside <see cref="NayaxSqsLivePolicy.PollWindow"/> -
    /// including a reader that has never polled, which is every process immediately after a restart.
    /// </summary>
    ReaderNotPolling = 2,

    /// <summary>
    /// The reader's receives are failing: <c>ConsecutivePollFailures</c> is above zero, which the next
    /// successful poll resets, so it is a statement about now rather than a lifetime tally.
    /// </summary>
    ReaderFailing = 3,

    /// <summary>
    /// This business's hourly reconciliation has not <strong>fully</strong> succeeded inside
    /// <see cref="NayaxSqsLivePolicy.ReconciliationWindow"/> - including a business whose
    /// reconciliation has never completed in full.
    /// </summary>
    ReconciliationStale = 4,
}

/// <summary>
/// Everything the live decision is made from, as one reading (issue #1106).
///
/// <para><strong>It carries no sender id.</strong> Which AWS IAM user sends a business's sales is the
/// Super Admin's record and is on no member-facing surface (AGENTS.md § Nayax integration); the
/// decision needs only whether one is recorded and switched on, so that is all this state can
/// express.</para>
/// </summary>
/// <param name="SenderRecorded">
/// Whether a sender id is recorded for the business at all. <see langword="false"/> for a business
/// with no sender row and for a row whose id was cleared - the same answer, because both attribute
/// nothing.
/// </param>
/// <param name="IsEnabled">Whether the recorded mapping is switched on.</param>
/// <param name="LastSuccessfulPollUtc">
/// The reader's last <c>ReceiveMessage</c> that returned without error, empty ones included, or
/// <see langword="null"/> when it has not polled since the process started.
/// </param>
/// <param name="ConsecutivePollFailures">How many receives in a row have failed.</param>
/// <param name="LastReconciliationSucceededAtUtc">
/// When this business's hourly reconciliation last fully succeeded, or <see langword="null"/> when it
/// never has.
/// </param>
public sealed record NayaxSqsLiveState(
    bool SenderRecorded,
    bool IsEnabled,
    DateTime? LastSuccessfulPollUtc,
    int ConsecutivePollFailures,
    DateTime? LastReconciliationSucceededAtUtc);

/// <summary>
/// When a business counts as <strong>live via SQS</strong> - the one rule the Dashboard's on-load
/// synchronization skip is decided by (issue #1106, child of #1100).
///
/// <para><strong>Why all three conditions, and why these three.</strong> Skipping the on-load sync
/// means showing stored sales and telling the operator they are current, so the claim has to rest on
/// evidence that the queue path is actually working. An enabled sender says messages can be
/// attributed to this business at all. A reader that polled successfully moments ago with no failures
/// says the queue is answering <em>now</em> - and an empty poll counts, because a fleet that sold
/// nothing is not a failure. A reconciliation that fully succeeded recently says something has
/// compared what the queue delivered against the provider's own record. An enabled sender alone, or a
/// reconciliation alone, says nothing about whether the reader works.</para>
///
/// <para><strong>Time since the last message is deliberately not a condition.</strong> A quiet queue
/// is the normal overnight state of a vending fleet; making it a condition would send every quiet
/// business back to one Nayax call per machine at breakfast, for sales nobody made.</para>
///
/// <para><strong>Why 150 minutes.</strong> The reconciliation runs hourly, so a run succeeding at
/// minute 0 with the runs due at 60 and 120 both missed leaves the business live until minute 150 -
/// a 30-minute grace <em>after the second missed run</em> rather than a limit that expires the moment
/// one is missed. A 90-minute limit tolerates no missed run at all: when the run due at 60 is missed,
/// the last success is already 90 minutes old.</para>
///
/// <para>It is a pure rule: no clock of its own, no database, no reader and no business. The instant
/// is passed in, which is what makes every boundary below testable without a process to restart.</para>
/// </summary>
public static class NayaxSqsLivePolicy
{
    /// <summary>
    /// How recently the reader must have polled successfully: two minutes, the value proposed on
    /// issue #1106. The reader's own poll interval is far shorter, so a reader that is running at all
    /// is comfortably inside it and one that has stopped falls out of it within one window.
    /// </summary>
    public static readonly TimeSpan PollWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How recently this business's hourly reconciliation must have fully succeeded: 150 minutes, the
    /// value proposed on issue #1106 - two missed hourly runs plus a 30-minute grace.
    /// </summary>
    public static readonly TimeSpan ReconciliationWindow = TimeSpan.FromMinutes(150);

    /// <summary>
    /// Why <paramref name="state"/> is not live as of <paramref name="nowUtc"/>, or
    /// <see langword="null"/> when every condition holds and the business <em>is</em> live.
    ///
    /// <para>The conditions are evaluated in the order the issue states them, so the reason names the
    /// first thing that is wrong: a business nobody has recorded a sender for is told that rather
    /// than being told about a reader whose state cannot be about it.</para>
    ///
    /// <para>An instant in the future - which only a clock moved backwards produces - is read as no
    /// evidence rather than as a comfortably recent one, the same fail-closed direction the
    /// recent-sync freshness memory takes for its own recorded instant.</para>
    /// </summary>
    public static NayaxSqsNotLiveReason? FindNotLiveReason(NayaxSqsLiveState state, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.SenderRecorded)
        {
            return NayaxSqsNotLiveReason.NoSender;
        }

        if (!state.IsEnabled)
        {
            return NayaxSqsNotLiveReason.SenderDisabled;
        }

        if (!IsWithin(state.LastSuccessfulPollUtc, nowUtc, PollWindow))
        {
            return NayaxSqsNotLiveReason.ReaderNotPolling;
        }

        if (state.ConsecutivePollFailures > 0)
        {
            return NayaxSqsNotLiveReason.ReaderFailing;
        }

        if (!IsWithin(state.LastReconciliationSucceededAtUtc, nowUtc, ReconciliationWindow))
        {
            return NayaxSqsNotLiveReason.ReconciliationStale;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="instantUtc"/> is recorded and less than <paramref name="window"/>
    /// before <paramref name="nowUtc"/>. An absent instant and a future one are both "no", so the
    /// absence of knowledge never reads as evidence.
    /// </summary>
    private static bool IsWithin(DateTime? instantUtc, DateTime nowUtc, TimeSpan window)
    {
        if (instantUtc is not { } recorded)
        {
            return false;
        }

        var elapsed = nowUtc - recorded;

        return elapsed >= TimeSpan.Zero && elapsed < window;
    }
}
