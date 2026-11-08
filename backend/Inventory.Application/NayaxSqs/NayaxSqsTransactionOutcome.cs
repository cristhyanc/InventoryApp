using Inventory.Application.Nayax;

namespace Inventory.Application.NayaxSqs;

/// <summary>
/// What one Nayax SQS transaction message means (issue #1102). Exactly one kind applies to a
/// message, chosen by the first matching rule of <see cref="NayaxSqsTransactionParser"/>'s ordered
/// table, so "what does this message mean" is decided in one place instead of by each consumer.
/// </summary>
public enum NayaxSqsOutcomeKind
{
    /// <summary>
    /// The body is not a transaction message this application can read at all: not JSON, or missing
    /// one of the fields a sale cannot be built without. A permanent failure for the queue reader -
    /// retrying cannot change the answer.
    /// </summary>
    Invalid,

    /// <summary>
    /// A prepaid credit top-up (or its reward form) rather than a product sale. Nothing is saved and
    /// nothing is wrong: the reader acknowledges and deletes the message.
    /// </summary>
    Revalue,

    /// <summary>
    /// A multi-selection vend. Deferred by parent #1100 because the existing store keeps one product
    /// per transaction and does not guarantee multivend quantities; the hourly <c>lastSales</c> run
    /// imports it exactly as it does today.
    /// </summary>
    Multivend,

    /// <summary>
    /// A transaction this application deliberately does not consume from the queue - see the
    /// <see cref="NayaxSqsRejectionReason"/> on the outcome. A permanent failure, kept in the
    /// dead-letter queue as a sample.
    /// </summary>
    Unsupported,

    /// <summary>
    /// A cancelled or declined transaction. It is saved as a non-completed row carrying that status,
    /// so it stays visible in status reporting instead of being discarded (AGENTS.md § Sales,
    /// payment methods, and statuses).
    /// </summary>
    CancelledOrDeclined,

    /// <summary>
    /// An approved transaction charging exactly nothing. Unsupported for now: neither the reference
    /// nor the delivery setup page says how a free vend is marked, so a sample has to exist before
    /// its stock handling is decided.
    /// </summary>
    FreeVend,

    /// <summary>A completed sale, saved as one.</summary>
    CompletedSale,

    /// <summary>
    /// A refund of a transaction this application may already have stored (issue #1107, status
    /// <c>62</c>). It is <strong>never saved as a sale</strong>: it can only move a sale this
    /// business already has from status <c>12</c> to status <c>62</c>, and a message matching none is
    /// left for the dead-letter queue exactly as it was before this outcome existed.
    ///
    /// <para>Appended last so no declared ordinal moves.</para>
    /// </summary>
    Refund
}

/// <summary>
/// Why a message was rejected. A reason is reported for <see cref="NayaxSqsOutcomeKind.Invalid"/>
/// and <see cref="NayaxSqsOutcomeKind.Unsupported"/>; every other kind carries
/// <see cref="None"/>, because the kind is already the whole answer.
///
/// <para>These are codes rather than sentences on purpose. The queue reader records why a message
/// was not saved, and a message that cannot be read is exactly the one whose content must not be
/// echoed: a reason is a fixed enum member, never a fragment of the payload.</para>
/// </summary>
public enum NayaxSqsRejectionReason
{
    /// <summary>No rejection: the message was understood.</summary>
    None = 0,

    /// <summary>The body is not a JSON object.</summary>
    NotJson,

    /// <summary>The root <c>TransactionId</c> is missing, null or not an integer.</summary>
    TransactionIdUnreadable,

    /// <summary>The root <c>MachineId</c> is missing, null or not an integer.</summary>
    MachineIdUnreadable,

    /// <summary>The <c>Data</c> object is missing, null or not an object.</summary>
    DataMissing,

    /// <summary>
    /// <c>Data.SeValue</c> is missing, null or not a number. The amount is never defaulted to zero.
    /// </summary>
    SettlementValueUnreadable,

    /// <summary><c>Data."Transaction Status ID"</c> is missing, null or not an integer.</summary>
    TransactionStatusIdUnreadable,

    /// <summary>
    /// <c>Data."Authorization Time"</c> is missing, or is not one of the date-time shapes a field
    /// documented as GMT may carry.
    /// </summary>
    AuthorizationTimeUnreadable,

    /// <summary><c>Data.Products</c> is missing, null, not an array, or empty.</summary>
    ProductsMissing,

    /// <summary>
    /// <c>Data."Transaction ID"</c> is present and is not the root <c>TransactionId</c>. The two
    /// copies of one identifier cannot disagree about which transaction the message is, and a
    /// present but unreadable copy cannot be shown to agree.
    /// </summary>
    TransactionIdMismatch,

    /// <summary>
    /// <c>Data."Machine ID"</c> is present and is not the root <c>MachineId</c>, on the same terms
    /// as <see cref="TransactionIdMismatch"/>. Saving a sale against the wrong machine would move
    /// stock and revenue between two machines' figures.
    /// </summary>
    MachineIdMismatch,

    /// <summary>The root <c>Void</c> flag is true, and the reference does not describe it.</summary>
    VoidTransaction,

    /// <summary><c>Data."Settlment Failed"</c> is true: the settlement process failed.</summary>
    SettlementFailed,

    /// <summary>
    /// Status <c>62</c>: refunded. Kept as a declared value although no outcome carries it any more -
    /// issue #1107 gave a refund its own <see cref="NayaxSqsOutcomeKind.Refund"/> outcome, so a
    /// refund is no longer a rejection. Removing the member would move the ordinals of every reason
    /// after it, and these codes reach a log.
    /// </summary>
    Refunded,

    /// <summary>
    /// Status <c>55</c> or <c>80</c>: pending and not final. Whether Nayax sends a second, final
    /// message for the same transaction is unknown until a sample shows one.
    /// </summary>
    PendingStatus,

    /// <summary>
    /// A status id that is not one of the application's established ones. Nayax publishes no list of
    /// <c>Transaction Status ID</c> values, so an unexpected value is refused rather than guessed -
    /// which is what keeps it from ever becoming a completed sale.
    /// </summary>
    UnrecognisedStatus,

    /// <summary>A negative <c>SeValue</c>. Money moving the other way is issue #1107's subject.</summary>
    NegativeSettlementValue,

    /// <summary>
    /// The status says approved while <c>Cancel Type</c> says the transaction was cancelled for some
    /// reason other than settling successfully. The two cannot both be true, so neither is believed.
    /// </summary>
    StatusContradiction
}

/// <summary>
/// The single answer <see cref="NayaxSqsTransactionParser"/> returns for one message body: either
/// the sale record this application already knows how to save, or an explicit "do not save" with a
/// reason (issue #1102).
///
/// <para><see cref="Report"/> is present for <see cref="NayaxSqsOutcomeKind.CompletedSale"/>,
/// <see cref="NayaxSqsOutcomeKind.CancelledOrDeclined"/> - the two kinds that are saved - and
/// <see cref="NayaxSqsOutcomeKind.Refund"/>, which saves nothing and carries the report only so the
/// three values that identify the transaction can be compared with the stored sale.
/// <see cref="ProductSlot"/> is present only for the two saved kinds: a refund resolves no product
/// at all, so it carries no slot to resolve one from. The slot
/// travels beside the report rather than inside it because <see cref="NayaxLastSalesReport"/> models
/// the <c>lastSales</c> payload, which has no slot: an SQS message carries the machine's MDB code as
/// well as Nayax's product id, and turning the slot into a product id when the message stated no
/// usable one needs the machine's product map (<see cref="NayaxSqsSlotProductResolver"/>), which is
/// a read this parser deliberately does not make.</para>
/// </summary>
public sealed record NayaxSqsTransactionOutcome
{
    private NayaxSqsTransactionOutcome(
        NayaxSqsOutcomeKind kind,
        NayaxSqsRejectionReason reason,
        NayaxLastSalesReport? report,
        int? productSlot)
    {
        Kind = kind;
        Reason = reason;
        Report = report;
        ProductSlot = productSlot;
    }

    /// <summary>What the message means.</summary>
    public NayaxSqsOutcomeKind Kind { get; }

    /// <summary>Why it was rejected, or <see cref="NayaxSqsRejectionReason.None"/>.</summary>
    public NayaxSqsRejectionReason Reason { get; }

    /// <summary>
    /// The sale record to save, for the two saved kinds; <see langword="null"/> for every other kind.
    /// Its <c>NayaxProductId</c> is set only from a usable <c>Product ID</c> the message itself
    /// stated, and is otherwise left for the reader to resolve from <see cref="ProductSlot"/>.
    /// </summary>
    public NayaxLastSalesReport? Report { get; }

    /// <summary>
    /// The machine slot (<c>Product Code in Map</c>, the MDB code) the vended product sat in, or
    /// <see langword="null"/> when the message did not carry a readable one.
    /// </summary>
    public int? ProductSlot { get; }

    /// <summary>A message that cannot be read as a transaction at all.</summary>
    public static NayaxSqsTransactionOutcome Invalid(NayaxSqsRejectionReason reason) =>
        new(NayaxSqsOutcomeKind.Invalid, reason, report: null, productSlot: null);

    /// <summary>A prepaid credit top-up, which is not a product sale.</summary>
    public static NayaxSqsTransactionOutcome Revalue() =>
        new(NayaxSqsOutcomeKind.Revalue, NayaxSqsRejectionReason.None, report: null, productSlot: null);

    /// <summary>A multi-selection vend, deferred to the hourly <c>lastSales</c> run.</summary>
    public static NayaxSqsTransactionOutcome Multivend() =>
        new(NayaxSqsOutcomeKind.Multivend, NayaxSqsRejectionReason.None, report: null, productSlot: null);

    /// <summary>A transaction this application does not consume from the queue.</summary>
    public static NayaxSqsTransactionOutcome Unsupported(NayaxSqsRejectionReason reason) =>
        new(NayaxSqsOutcomeKind.Unsupported, reason, report: null, productSlot: null);

    /// <summary>An approved transaction charging exactly nothing.</summary>
    public static NayaxSqsTransactionOutcome FreeVend() =>
        new(NayaxSqsOutcomeKind.FreeVend, NayaxSqsRejectionReason.None, report: null, productSlot: null);

    /// <summary>A cancelled or declined transaction, saved as a non-completed row.</summary>
    public static NayaxSqsTransactionOutcome CancelledOrDeclined(
        NayaxLastSalesReport report, int? productSlot) =>
        new(NayaxSqsOutcomeKind.CancelledOrDeclined, NayaxSqsRejectionReason.None, report, productSlot);

    /// <summary>A completed sale.</summary>
    public static NayaxSqsTransactionOutcome CompletedSale(
        NayaxLastSalesReport report, int? productSlot) =>
        new(NayaxSqsOutcomeKind.CompletedSale, NayaxSqsRejectionReason.None, report, productSlot);

    /// <summary>
    /// A refund of a transaction this business may already have stored (issue #1107). It carries no
    /// slot: a refund never resolves a product and never becomes a sale, so there is nothing for the
    /// machine's product map to answer.
    /// </summary>
    public static NayaxSqsTransactionOutcome Refund(NayaxLastSalesReport report) =>
        new(NayaxSqsOutcomeKind.Refund, NayaxSqsRejectionReason.None, report, productSlot: null);
}
