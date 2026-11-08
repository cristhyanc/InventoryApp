namespace Inventory.Application.NayaxSqs;

/// <summary>
/// Records that a message from the current business's recorded SQS sender has just been accepted
/// (issue #1103).
///
/// <para><strong>It is deliberately a different port from <see cref="INayaxSqsSenderDirectory"/>.</strong>
/// The directory answers "whose sale is this?" before any business scope exists and must therefore
/// ignore the tenant query filters; this one runs <em>inside</em> the scope resolved to that one
/// business, so it is an ordinary tenant-owned update - filtered centrally, stamped centrally, and
/// addressed to the business's own row by the query filter rather than by a business id anybody
/// passed in. Folding it into the directory would have widened the one filter-ignoring statement into
/// a write, which is the opposite of what makes that statement safe.</para>
///
/// <para><strong>It writes one observability column and nothing financial.</strong>
/// <c>BusinessNayaxSqsSender.LastMessageReceivedAtUtc</c> is what the Super Admin's page reports so a
/// person can see that a business's queue is actually delivering (issue #1101). It is written after
/// the sale's own durable transaction has committed, on purpose: a process that stops in between
/// loses an instant somebody looks at, and keeping it out of the sale's transaction keeps the
/// financial write as small as it can be.</para>
/// </summary>
public interface INayaxSqsSenderActivityStore
{
    /// <summary>
    /// Stamps <paramref name="receivedAtUtc"/> on the current business's sender row, or does nothing
    /// when that business has no row - which cannot normally happen, because the directory found one
    /// to attribute the message by, and must still be a no-op rather than an insert: this path never
    /// creates a sender mapping.
    /// </summary>
    Task RecordMessageReceivedAsync(DateTime receivedAtUtc, CancellationToken cancellationToken);
}
