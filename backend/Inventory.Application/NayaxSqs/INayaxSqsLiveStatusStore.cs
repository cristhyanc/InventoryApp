namespace Inventory.Application.NayaxSqs;

/// <summary>
/// What the current business's own Nayax SQS sender row says about its queue participation (issue
/// #1106) - the stored half of the live decision, beside the in-memory
/// <see cref="NayaxSqsReaderHealth"/>.
/// </summary>
/// <param name="SenderIdRecorded">
/// Whether a sender id is recorded at all. It is a <strong>boolean and never the id</strong>: which
/// AWS IAM user sends a business's sales is the Super Admin's record and is readable on no
/// member-facing surface (AGENTS.md § Nayax integration), and the decision needs only whether one
/// exists. A row whose id was cleared by a "Remove" answers <see langword="false"/>, exactly as a
/// business with no row does.
/// </param>
/// <param name="IsEnabled">Whether the recorded mapping is switched on.</param>
/// <param name="LastMessageReceivedAtUtc">
/// When a message from this business's sender was last accepted, or <see langword="null"/> for one
/// that has never sent anything (issue #1103). It is reported to the Dashboard and is deliberately
/// <em>not</em> a condition of being live: a quiet queue is not a failure.
/// </param>
/// <param name="LastReconciliationSucceededAtUtc">
/// When this business's hourly reconciliation last <strong>fully</strong> succeeded, or
/// <see langword="null"/> when it never has (issue #1104, which is the only writer of that column).
/// </param>
public sealed record NayaxSqsLiveSenderStatus(
    bool SenderIdRecorded,
    bool IsEnabled,
    DateTime? LastMessageReceivedAtUtc,
    DateTime? LastReconciliationSucceededAtUtc);

/// <summary>
/// "Is this business on the queue, and when did anything last confirm it?" (issue #1106) - the one
/// read the Dashboard's synchronization makes before deciding that it has nothing to read.
///
/// <para><strong>It is an ordinary tenant-owned read, and that is the point.</strong> It runs inside
/// the service scope the request resolved to the caller's own business, so the central query filter
/// is what finds the row: there is no business id parameter, no <c>IgnoreQueryFilters</c> and no
/// predicate a caller could supply, and a business id nobody passed in cannot be the wrong one. It is
/// the same shape as <see cref="INayaxSqsSenderActivityStore"/> and
/// <c>INayaxReconciliationSuccessStore</c>, which write single columns of that same row.</para>
///
/// <para><strong>It is read-only and publishes no identity.</strong> The projection carries whether a
/// sender is recorded rather than which one, so no member-facing path can learn, echo or log the AWS
/// IAM user id a Super Admin recorded - the rule that record exists for (issue #1101). The enabled
/// flag, the revision and the id itself stay the Super Admin's surface's alone to report, and this
/// port writes nothing at all.</para>
/// </summary>
public interface INayaxSqsLiveStatusStore
{
    /// <summary>
    /// The current business's sender status, or <see langword="null"/> when it has no sender row -
    /// which the live decision reads as "no sender", the same as a row whose id was cleared.
    /// </summary>
    Task<NayaxSqsLiveSenderStatus?> FindCurrentAsync(CancellationToken cancellationToken);
}
