using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// What a status-<c>62</c> Nayax SQS message does to the sale it names (issue #1107). One answer per
/// message; <see cref="NayaxSaleRefundPolicy.Decide"/> states the order they are decided in.
/// </summary>
public enum NayaxSaleRefundDecision
{
    /// <summary>
    /// The stored sale moves from status <c>12</c> to status <c>62</c>, once, with an audit row.
    /// </summary>
    Apply = 10,

    /// <summary>
    /// The stored sale is already status <c>62</c> <em>and the message agrees with it</em>: a genuine
    /// redelivery of the refund that was already applied. Nothing changes and nothing is audited a
    /// second time - that is what makes applying a refund idempotent without a second table to look
    /// in. A later message that disagrees about the machine, the amount or the product is
    /// <see cref="Contradicted"/> instead, not a redelivery.
    /// </summary>
    AlreadyRefunded = 20,

    /// <summary>
    /// The stored sale is neither <c>12</c> nor <c>62</c> - pending, cancelled, declined, unknown or
    /// no status at all. Only a completed sale can be refunded, and a message that claims otherwise
    /// is not applied: it would move a transaction nobody was ever charged for into the refunded
    /// column, and silently change a financial status on a guess.
    /// </summary>
    NotRefundable = 30,

    /// <summary>
    /// The message and the stored sale disagree about the machine, the amount or the product. The
    /// two are then not plainly the same transaction - whatever the transaction id says - so nothing
    /// is applied and the message is kept as a dead-letter sample for a person to look at.
    ///
    /// <para>This answer is given <strong>whatever the stored sale's status is</strong>, the already
    /// refunded one included: a second status-<c>62</c> message for one transaction that disagrees
    /// about what was sold is the sample a person most needs to see, and it is the undocumented
    /// delivery shape's loudest contradiction.</para>
    /// </summary>
    Contradicted = 40,
}

/// <summary>
/// The refund message's own side of the comparison (issue #1107): the three values that say
/// <em>which</em> transaction it is about, and nothing else.
///
/// <para>The authorization instant is deliberately <strong>not</strong> here. Nayax documents
/// neither a status id list nor whether a refund message repeats the original authorization time or
/// carries the refund's own, so comparing it would turn an undocumented field into a reason to
/// refuse a real refund. The machine, the amount and the product are the three the issue names, and
/// each of them is a statement about what was sold.</para>
/// </summary>
/// <param name="MachineId">The message's root <c>MachineId</c>.</param>
/// <param name="SettlementValue">The message's <c>Data.SeValue</c>, as reported.</param>
/// <param name="NayaxProductId">
/// The provider's own product id from the message's product entry, or <see langword="null"/> when it
/// stated no usable one. Never a local product id.
/// </param>
public sealed record NayaxRefundMessageValues(
    long MachineId, decimal SettlementValue, long? NayaxProductId);

/// <summary>Each compared field's own answer (issue #1107), on the terms of issue #1104's model.</summary>
public sealed record NayaxSaleRefundComparisons(
    NayaxSaleFieldComparison Machine,
    NayaxSaleFieldComparison Amount,
    NayaxSaleFieldComparison Product);

/// <summary>
/// What comparing one refund message with the sale it names decided, and the field answers behind it.
/// </summary>
public sealed record NayaxSaleRefundResult(
    NayaxSaleRefundDecision Decision, NayaxSaleRefundComparisons Fields);

/// <summary>
/// "Does this refund message belong to that stored sale, and may it be applied?" - the one place
/// that question is answered (issue #1107, child of #1100).
///
/// <para><strong>The delivery shape it rests on is an owner-approved assumption, not a documented
/// contract.</strong> The repository owner decided on 8 November 2026 to build on a refund arriving
/// as the <em>same</em> <c>TransactionId</c> delivered again with status <c>62</c>. Nayax's own SQS
/// Transaction JSON reference documents <c>Transaction Status ID</c> as an <c>Integer</c> and
/// enumerates no values at all (checked 8 November 2026), and says nothing about whether a refund is
/// a new message or an update to the original, so <c>62</c> is this application's own established id
/// (<see cref="NayaxTransactionStatusIds.Refunded"/>, AGENTS.md § Sales, payment methods, and
/// statuses) and the shape is unverified. Nothing here reads <c>Original Transaction ID</c>: a refund
/// delivered as a new transaction pointing back at the original is explicitly a different design, and
/// inferring it from a field name is what AGENTS.md § Nayax contract verification forbids.</para>
///
/// <para><strong>Everything it cannot confirm, it refuses.</strong> A message matching no stored sale
/// never becomes a sale of its own, a message whose values contradict the stored sale is not applied,
/// and both are left for the dead-letter queue as samples - which is the safe outcome if the
/// assumption turns out to be wrong, and the only way a real sample reaches a person.</para>
///
/// <para><strong>It is a pure function and it writes nothing.</strong> No database, no clock, no
/// configuration; the amount normalisation is the application's own money convention, shared with
/// <see cref="NayaxSaleProviderComparisonPolicy"/>, rather than a tolerance somebody can widen at run
/// time.</para>
/// </summary>
public static class NayaxSaleRefundPolicy
{
    /// <summary>
    /// Decides what a refund message does to the sale it names. The order is the rule: a
    /// contradiction, then an already refunded sale, then a sale that is not a completed sale, and
    /// only then the refund itself.
    /// </summary>
    /// <param name="storedTransactionStatusId">The stored sale's current status.</param>
    /// <param name="stored">The stored sale's own values.</param>
    /// <param name="refund">The refund message's values.</param>
    public static NayaxSaleRefundResult Decide(
        int? storedTransactionStatusId,
        NayaxStoredSaleValues stored,
        NayaxRefundMessageValues refund)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(refund);

        var fields = Compare(stored, refund);

        // A contradiction comes first, and it comes first for every stored status - the already
        // refunded one included. A message that disagrees about the machine, the amount or the
        // product is not plainly about the sale it names at all, so the stored status has nothing to
        // decide, and the message has to reach a person. Answering "already refunded" before looking
        // at the compared fields lost exactly that: the reader acknowledges that outcome, so a second
        // status-62 message for one transaction naming a different machine, amount or product
        // disappeared from the queue instead of being kept as the dead-letter sample the issue
        // requires - and under an undocumented delivery shape, that message is the evidence.
        if (Contradicts(fields))
            return new NayaxSaleRefundResult(NayaxSaleRefundDecision.Contradicted, fields);

        // Then an already refunded sale, and that is the idempotency: a genuine redelivery - the same
        // values delivered again - answers "already refunded" having changed nothing, so a second
        // status change and a second audit row are impossible.
        if (storedTransactionStatusId == NayaxTransactionStatusIds.Refunded)
            return new NayaxSaleRefundResult(NayaxSaleRefundDecision.AlreadyRefunded, fields);

        if (storedTransactionStatusId != NayaxTransactionStatusIds.Completed)
            return new NayaxSaleRefundResult(NayaxSaleRefundDecision.NotRefundable, fields);

        return new NayaxSaleRefundResult(NayaxSaleRefundDecision.Apply, fields);
    }

    /// <summary>
    /// The three field answers. The machine and the amount are present on both sides and are always
    /// compared; the product is compared only when both sides carry one, on issue #1104's rule that
    /// a value missing on either side is <see cref="NayaxSaleFieldComparison.NotCompared"/> and is
    /// never read as agreement.
    /// </summary>
    private static NayaxSaleRefundComparisons Compare(
        NayaxStoredSaleValues stored, NayaxRefundMessageValues refund) =>
        new(
            Machine: Compared(stored.MachineId == refund.MachineId),
            Amount: Compared(Money(stored.SettlementValue) == Money(refund.SettlementValue)),
            Product: stored.NayaxProductId is { } storedProduct
                && refund.NayaxProductId is { } refundProduct
                    ? Compared(storedProduct == refundProduct)
                    : NayaxSaleFieldComparison.NotCompared);

    /// <summary>
    /// Whether these answers contradict the stored sale. Only a <see cref="NayaxSaleFieldComparison.Mismatch"/>
    /// does: a product the message did not state is no evidence that a different product was sold,
    /// and refusing every refund message that omits one would refuse the refund rather than the
    /// mistake.
    /// </summary>
    private static bool Contradicts(NayaxSaleRefundComparisons fields) =>
        fields.Machine is NayaxSaleFieldComparison.Mismatch
        || fields.Amount is NayaxSaleFieldComparison.Mismatch
        || fields.Product is NayaxSaleFieldComparison.Mismatch;

    private static NayaxSaleFieldComparison Compared(bool equal) =>
        equal ? NayaxSaleFieldComparison.Match : NayaxSaleFieldComparison.Mismatch;

    /// <summary>
    /// The amount both sides are compared at: two decimal places, half away from zero - the same
    /// money convention as <see cref="NayaxSaleProviderComparisonPolicy"/>, read from there rather
    /// than restated, so the two comparisons cannot drift apart.
    ///
    /// <para>It is the <em>signed</em> amount. Partial refunds are out of scope because no evidence
    /// exists that Nayax sends one, and a message whose amount differs from the stored sale's is not
    /// applied: that is the acceptance criterion, and it means a refund reported as a negated amount
    /// reaches the dead-letter queue as a sample instead of being quietly treated as a full refund of
    /// something else.</para>
    /// </summary>
    private static decimal Money(decimal value) =>
        Math.Round(
            value,
            NayaxSaleProviderComparisonPolicy.AmountDecimalPlaces,
            NayaxSaleProviderComparisonPolicy.AmountRounding);
}
