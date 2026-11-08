using Inventory.Application.SalesSync;
using Inventory.Domain.NayaxSqs;

namespace InventoryApi.DTOs;

/// <summary>
/// The codes <c>POST api/nayax-sales-sync</c> publishes for
/// <see cref="NayaxSqsNotLiveReason"/> (issue #1106).
///
/// <para>A code rather than the enum's number, exactly as <c>MachineNeedUnavailableReasons</c>
/// publishes its own vocabulary: a client branches on a stable name, and reordering or extending the
/// enum cannot silently change what a response means.</para>
/// </summary>
public static class NayaxSqsNotLiveReasons
{
    /// <summary>No sender id is recorded for this business, so nothing on the queue is its own.</summary>
    public const string NoSender = "NoSender";

    /// <summary>A sender id is recorded and the mapping is switched off.</summary>
    public const string SenderDisabled = "SenderDisabled";

    /// <summary>The queue reader has no successful poll inside the two-minute window.</summary>
    public const string ReaderNotPolling = "ReaderNotPolling";

    /// <summary>The reader's receives are failing.</summary>
    public const string ReaderFailing = "ReaderFailing";

    /// <summary>
    /// This business's hourly reconciliation has not fully succeeded inside the 150-minute window.
    /// </summary>
    public const string ReconciliationStale = "ReconciliationStale";

    /// <exception cref="ArgumentOutOfRangeException">
    /// A reason this vocabulary has never heard of - a value cast from an integer, which an enum
    /// cannot prevent. Refused rather than published under a guessed code.
    /// </exception>
    public static string Code(NayaxSqsNotLiveReason reason) => reason switch
    {
        NayaxSqsNotLiveReason.NoSender => NoSender,
        NayaxSqsNotLiveReason.SenderDisabled => SenderDisabled,
        NayaxSqsNotLiveReason.ReaderNotPolling => ReaderNotPolling,
        NayaxSqsNotLiveReason.ReaderFailing => ReaderFailing,
        NayaxSqsNotLiveReason.ReconciliationStale => ReconciliationStale,
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Unknown Nayax SQS not-live reason."),
    };
}

/// <summary>
/// What <c>POST api/nayax-sales-sync</c> answers when it read nothing from Nayax because there was
/// nothing to read: this business's sales were synchronized moments ago (issue #843), or they arrive
/// live from the shared Nayax SQS queue (issue #1106).
///
/// It is a <c>200</c> with this body rather than a further meaning for the endpoint's existing
/// statuses: <c>204</c> still means "the synchronization ran", <c>409</c> "a sync for this business is
/// already running" (issue #1104) and <c>429</c> "too many runs too recently" (issue #681), all
/// unchanged for every existing caller. A skip is none of those - nothing ran, and nothing was
/// refused - and the Dashboard has to be able to tell them apart to say "Sales synced N min ago" or
/// "Live from Nayax" instead of a warning.
///
/// <para>Every <c>*AtUtc</c> field is a true instant, as everywhere else in this API; the client
/// renders it in the business's own timezone. The response carries no business id, no operator id, no
/// credential revision and - deliberately - <strong>no AWS sender id</strong>: which IAM user sends a
/// business's sales is the Super Admin's record and is on no member-facing surface (issue #1101).</para>
/// </summary>
/// <param name="WasRecentlySynced">
/// Whether the run was skipped because this business synchronized less than three minutes ago (issue
/// #843). A client reads the answer from the payload rather than inferring it from the status code
/// alone. It is <see langword="false"/> on the live answer below, which is a different skip.
/// </param>
/// <param name="LastSyncedAtUtc">
/// When the last fully successful synchronization finished - the instant the figures in front of the
/// operator were read - or <c>null</c> when this answer is not about a recent run, which is the live
/// answer below.
/// </param>
/// <param name="LiveViaSqs">
/// Whether this business's sales arrive live from the shared Nayax SQS queue, so this call read
/// nothing, wrote nothing and claimed no run allowance (issue #1106). It is <see langword="true"/>
/// only while the business has an enabled SQS sender, the queue reader polled successfully inside
/// <see cref="NayaxSqsLivePolicy.PollWindow"/> with no consecutive failures, and this business's
/// hourly reconciliation fully succeeded inside
/// <see cref="NayaxSqsLivePolicy.ReconciliationWindow"/>.
/// </param>
/// <param name="LastSqsMessageReceivedAtUtc">
/// When a message from this business's SQS sender was last accepted, or <c>null</c> when none ever
/// has - a healthy queue that has delivered nothing yet, which is still live. The time since the last
/// message is never a condition of being live: a quiet queue is not a failure.
/// </param>
/// <param name="SqsNotLiveReason">
/// Why the business is not live via SQS, as one of <see cref="NayaxSqsNotLiveReasons"/>, or
/// <c>null</c> when it is live. It is also <c>null</c> on a forced Refresh, which makes no live
/// decision at all.
/// </param>
public sealed record NayaxSalesRecentlySyncedResponse(
    bool WasRecentlySynced,
    DateTime? LastSyncedAtUtc,
    bool LiveViaSqs,
    DateTime? LastSqsMessageReceivedAtUtc,
    string? SqsNotLiveReason)
{
    /// <summary>
    /// Projects the use case's own answer. Every judgement on it - whether the business is live, why
    /// it is not, and whether the sales were already recent - was made by the use case and the Domain
    /// rule behind it; this mapper only renames.
    /// </summary>
    public static NayaxSalesRecentlySyncedResponse From(NayaxSalesSyncOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new NayaxSalesRecentlySyncedResponse(
            outcome.RecentlySyncedAtUtc is not null,
            outcome.RecentlySyncedAtUtc,
            outcome.LiveViaSqs,
            outcome.LastSqsMessageReceivedAtUtc,
            outcome.SqsNotLiveReason is { } reason ? NayaxSqsNotLiveReasons.Code(reason) : null);
    }
}
