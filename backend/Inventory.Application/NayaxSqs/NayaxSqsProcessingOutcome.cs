namespace Inventory.Application.NayaxSqs;

/// <summary>
/// What the reader does with a message once it has finished with it (issue #1103). Exactly three
/// answers, which is the issue's own outcome table.
/// </summary>
public enum NayaxSqsDisposition
{
    /// <summary>
    /// The message is finished with and is deleted: a sale saved with no pending rebuild left, a
    /// duplicate whose rebuilds are clear, or a message that needs no sale at all.
    /// </summary>
    Done,

    /// <summary>
    /// Something that may succeed later did not succeed now - a busy database, a network or AWS
    /// failure, a Nayax timeout, a rebuild that is still failing. The message is <em>not</em>
    /// deleted and its visibility is set to a bounded backoff, so the next receive is the retry.
    /// </summary>
    Transient,

    /// <summary>
    /// Retrying cannot change the answer: the message cannot be read, is not a sale this application
    /// consumes, or is attributed to nobody who may own it. It is not deleted either - its visibility
    /// is set to zero so it reaches the dead-letter queue as a sample once the queue's own
    /// <c>maxReceiveCount</c> is spent.
    /// </summary>
    Permanent,
}

/// <summary>
/// Why the reader reached the disposition it did (issue #1103).
///
/// <para><strong>These are codes rather than sentences, for the reason
/// <c>NayaxSqsRejectionReason</c>'s are.</strong> The reader records why a message was not saved, and
/// a message it could not save is exactly the one whose content must not be echoed: a reason is a
/// fixed enum member, never a fragment of a payload, a receipt handle or an exception from AWS.</para>
/// </summary>
public enum NayaxSqsProcessingReason
{
    /// <summary>The sale was saved by this message, and nothing is still pending for it.</summary>
    Saved = 0,

    /// <summary>
    /// The sale was already stored - a redelivery, or the other import path got there first - and the
    /// stored row was enriched where it was still missing something. Nothing is pending for it.
    /// </summary>
    AlreadyStored,

    /// <summary>
    /// The message is understood and needs no sale: a prepaid credit top-up, or a multivend parent
    /// #1100 leaves to the hourly <c>lastSales</c> run.
    /// </summary>
    NotASaleToStore,

    /// <summary>The queue reported no <c>SenderId</c>, so the message is attributable to nobody.</summary>
    SenderMissing,

    /// <summary>No business has recorded this sender id.</summary>
    SenderUnknown,

    /// <summary>The sender is recorded but the mapping is switched off.</summary>
    SenderDisabled,

    /// <summary>The business the sender belongs to is not in an approved, active lifecycle state.</summary>
    BusinessNotActive,

    /// <summary>
    /// The machine the message names is not one of that business's machines - checked against the
    /// cached fleet list and then against one fresh read. Saving it would attribute a sale, its stock
    /// and its revenue to a machine the business does not have.
    /// </summary>
    MachineNotInFleet,

    /// <summary>
    /// <c>NayaxSqsTransactionParser</c> could not read the message as a transaction at all, or
    /// refused it as one this application does not consume. The parser's own reason code is logged
    /// beside this one.
    /// </summary>
    MessageRefused,

    /// <summary>
    /// The business has no usable Nayax connection, or its plan does not include the sales provider.
    /// Transient rather than permanent: unlike an unknown sender or a foreign machine, this is not a
    /// statement about the message - reconnecting or upgrading makes the very same message saveable -
    /// so the sale is parked for the next receive rather than thrown at the dead-letter queue.
    /// </summary>
    ConnectionUnavailable,

    /// <summary>The sale was saved or found, and at least one product's inventory-cost rebuild is still pending.</summary>
    RebuildPending,

    /// <summary>
    /// The write could not take the database's write lock within its busy timeout, or another
    /// persistence failure that may pass. Nothing was saved.
    /// </summary>
    StoreUnavailable,

    /// <summary>A Nayax read the gates or the slot lookup needed failed or timed out.</summary>
    ProviderUnavailable,

    /// <summary>
    /// A refund moved a stored sale from status <c>12</c> to status <c>62</c>, once, with an audit
    /// row (issue #1107). The money is reversed in every completed-sale total and the unit stays
    /// consumed.
    /// </summary>
    RefundApplied,

    /// <summary>
    /// A refund for a sale already stored as status <c>62</c>: a redelivery, or a second message for
    /// the same transaction. Nothing changed and nothing was audited again, so the message is
    /// finished with.
    /// </summary>
    RefundAlreadyApplied,

    /// <summary>
    /// A refund naming a transaction this business has no sale for. Nothing is saved - a refund never
    /// becomes a sale - and the message is released towards the dead-letter queue as a sample,
    /// because that is the safe outcome if the refund delivery shape this is built on is wrong.
    /// </summary>
    RefundUnmatched,

    /// <summary>
    /// A refund whose machine, amount or product contradicts the sale it names, so the two are not
    /// plainly the same transaction. Nothing is changed and the message is kept as a sample.
    /// </summary>
    RefundContradicted,

    /// <summary>
    /// A refund naming a stored sale that is not a status-<c>12</c> sale - pending, cancelled,
    /// declined, unknown or without a status. There is nothing to refund, and moving such a row to
    /// refunded on a message alone would change a financial status on a guess.
    /// </summary>
    RefundNotApplicable,
}

/// <summary>
/// The reader's whole answer about one message: what to do with it, and why (issue #1103).
/// </summary>
/// <param name="Disposition">Delete it, back it off, or let it reach the dead-letter queue.</param>
/// <param name="Reason">The fixed code that says why.</param>
public sealed record NayaxSqsProcessingOutcome(
    NayaxSqsDisposition Disposition,
    NayaxSqsProcessingReason Reason)
{
    /// <summary>The message is finished with and may be deleted.</summary>
    public static NayaxSqsProcessingOutcome Done(NayaxSqsProcessingReason reason) =>
        new(NayaxSqsDisposition.Done, reason);

    /// <summary>The message is kept and backed off.</summary>
    public static NayaxSqsProcessingOutcome Transient(NayaxSqsProcessingReason reason) =>
        new(NayaxSqsDisposition.Transient, reason);

    /// <summary>The message is kept and released towards the dead-letter queue.</summary>
    public static NayaxSqsProcessingOutcome Permanent(NayaxSqsProcessingReason reason) =>
        new(NayaxSqsDisposition.Permanent, reason);
}
