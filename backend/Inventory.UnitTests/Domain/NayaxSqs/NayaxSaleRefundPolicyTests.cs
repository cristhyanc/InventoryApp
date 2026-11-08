using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Domain.NayaxSqs;

/// <summary>
/// The refund decision table of issue #1107: what a status-<c>62</c> message does to the sale it
/// names, and why it is so often "nothing".
///
/// <para>The refusals matter more than the acceptance here. The delivery shape this rests on - the
/// same <c>TransactionId</c> delivered again with status <c>62</c> - is an owner-approved assumption
/// that Nayax does not document, so every case the policy cannot place has to end with the stored
/// sale untouched and the message kept as a sample.</para>
/// </summary>
public class NayaxSaleRefundPolicyTests
{
    private const long MachineId = 4200001;
    private const long NayaxProductId = 4300001;

    [Fact]
    public void A_refund_matching_a_stored_completed_sale_is_applied()
    {
        var decided = NayaxSaleRefundPolicy.Decide(
            NayaxTransactionStatusIds.Completed, Stored(), Message());

        Assert.Equal(NayaxSaleRefundDecision.Apply, decided.Decision);
        Assert.Equal(NayaxSaleFieldComparison.Match, decided.Fields.Machine);
        Assert.Equal(NayaxSaleFieldComparison.Match, decided.Fields.Amount);
        Assert.Equal(NayaxSaleFieldComparison.Match, decided.Fields.Product);
    }

    /// <summary>
    /// A sale already at <c>62</c> answers "already refunded" and is never compared into a different
    /// answer. That ordering <em>is</em> the idempotency: a redelivery of the message that applied
    /// the refund cannot produce a second status change or a second audit row.
    /// </summary>
    [Fact]
    public void A_refund_of_an_already_refunded_sale_changes_nothing()
    {
        Assert.Equal(
            NayaxSaleRefundDecision.AlreadyRefunded,
            NayaxSaleRefundPolicy.Decide(NayaxTransactionStatusIds.Refunded, Stored(), Message()).Decision);

        // Even with values that would otherwise contradict: the sale is refunded either way, and a
        // second message is not a reason to reopen a settled financial status.
        Assert.Equal(
            NayaxSaleRefundDecision.AlreadyRefunded,
            NayaxSaleRefundPolicy
                .Decide(NayaxTransactionStatusIds.Refunded, Stored(), Message(machineId: 9999))
                .Decision);
    }

    /// <summary>
    /// Only a completed sale can be refunded. A pending, cancelled, declined, unrecognised or
    /// status-less row is left alone: moving one to refunded would change a financial status for a
    /// transaction nobody was shown to have been charged for.
    /// </summary>
    [Theory]
    [InlineData(26)]
    [InlineData(28)]
    [InlineData(31)]
    [InlineData(55)]
    [InlineData(80)]
    [InlineData(250)]
    [InlineData(999)]
    [InlineData(null)]
    public void A_refund_of_a_sale_that_is_not_completed_is_not_applied(int? storedStatusId)
    {
        Assert.Equal(
            NayaxSaleRefundDecision.NotRefundable,
            NayaxSaleRefundPolicy.Decide(storedStatusId, Stored(), Message()).Decision);
    }

    /// <summary>
    /// A different machine, a different amount or a different product means the two records are not
    /// plainly the same transaction, whatever the transaction id says, so nothing is applied.
    /// </summary>
    [Fact]
    public void A_refund_whose_machine_amount_or_product_differs_is_not_applied()
    {
        Assert.Equal(
            NayaxSaleRefundDecision.Contradicted,
            NayaxSaleRefundPolicy
                .Decide(NayaxTransactionStatusIds.Completed, Stored(), Message(machineId: 9999))
                .Decision);
        Assert.Equal(
            NayaxSaleRefundDecision.Contradicted,
            NayaxSaleRefundPolicy
                .Decide(NayaxTransactionStatusIds.Completed, Stored(), Message(settlementValue: 2.5m))
                .Decision);
        Assert.Equal(
            NayaxSaleRefundDecision.Contradicted,
            NayaxSaleRefundPolicy
                .Decide(NayaxTransactionStatusIds.Completed, Stored(), Message(nayaxProductId: 7777))
                .Decision);
    }

    /// <summary>
    /// Partial refunds are out of scope because no evidence exists that Nayax sends one, and the rule
    /// that implements that is an exact amount comparison. A refund reported as the negated amount is
    /// therefore <em>not</em> applied either: it reaches the dead-letter queue as a sample of a real
    /// refund instead of being read as a full refund of something else.
    /// </summary>
    [Theory]
    [InlineData(1.75)]
    [InlineData(-3.5)]
    [InlineData(0)]
    public void An_amount_that_is_not_the_stored_amount_is_a_contradiction_including_a_negated_one(
        double settlementValue)
    {
        Assert.Equal(
            NayaxSaleRefundDecision.Contradicted,
            NayaxSaleRefundPolicy
                .Decide(
                    NayaxTransactionStatusIds.Completed,
                    Stored(),
                    Message(settlementValue: (decimal)settlementValue))
                .Decision);
    }

    /// <summary>
    /// Amounts are compared at the currency's minor unit, half away from zero - the application's own
    /// money convention, shared with the <c>lastSales</c> comparison - so sub-cent noise on either
    /// side is not a disagreement about anything.
    /// </summary>
    [Theory]
    [InlineData(3.5)]
    [InlineData(3.504)]
    [InlineData(3.4951)]
    public void Amounts_are_compared_at_two_decimal_places(double settlementValue)
    {
        Assert.Equal(
            NayaxSaleRefundDecision.Apply,
            NayaxSaleRefundPolicy
                .Decide(
                    NayaxTransactionStatusIds.Completed,
                    Stored(),
                    Message(settlementValue: (decimal)settlementValue))
                .Decision);
    }

    /// <summary>
    /// A product missing on either side is <c>NotCompared</c> and is no contradiction: a message that
    /// stated no product id is not evidence that a different product was sold, and refusing every
    /// such refund would refuse the refund rather than the mistake.
    /// </summary>
    [Fact]
    public void A_product_missing_on_either_side_is_not_compared_and_blocks_nothing()
    {
        var messageWithoutProduct = NayaxSaleRefundPolicy.Decide(
            NayaxTransactionStatusIds.Completed, Stored(), Message(nayaxProductId: null));

        Assert.Equal(NayaxSaleRefundDecision.Apply, messageWithoutProduct.Decision);
        Assert.Equal(NayaxSaleFieldComparison.NotCompared, messageWithoutProduct.Fields.Product);

        var saleWithoutProduct = NayaxSaleRefundPolicy.Decide(
            NayaxTransactionStatusIds.Completed, Stored(nayaxProductId: null), Message());

        Assert.Equal(NayaxSaleRefundDecision.Apply, saleWithoutProduct.Decision);
        Assert.Equal(NayaxSaleFieldComparison.NotCompared, saleWithoutProduct.Fields.Product);
    }

    /// <summary>
    /// The field answers are recorded whatever the decision came out as, so a refusal can be read
    /// back as "which field disagreed" rather than only as "refused".
    /// </summary>
    [Fact]
    public void The_field_answers_are_reported_even_when_nothing_is_applied()
    {
        var decided = NayaxSaleRefundPolicy.Decide(
            NayaxTransactionStatusIds.Completed, Stored(), Message(machineId: 9999, settlementValue: 1m));

        Assert.Equal(NayaxSaleFieldComparison.Mismatch, decided.Fields.Machine);
        Assert.Equal(NayaxSaleFieldComparison.Mismatch, decided.Fields.Amount);
        Assert.Equal(NayaxSaleFieldComparison.Match, decided.Fields.Product);
    }

    /// <summary>
    /// Every declared decision is produced by one of the cases above, so a decision added later
    /// cannot arrive with no case saying what causes it.
    /// </summary>
    [Fact]
    public void Every_declared_decision_is_reachable_from_the_cases_above()
    {
        var produced = new[]
        {
            NayaxSaleRefundPolicy.Decide(NayaxTransactionStatusIds.Completed, Stored(), Message()).Decision,
            NayaxSaleRefundPolicy.Decide(NayaxTransactionStatusIds.Refunded, Stored(), Message()).Decision,
            NayaxSaleRefundPolicy.Decide(250, Stored(), Message()).Decision,
            NayaxSaleRefundPolicy
                .Decide(NayaxTransactionStatusIds.Completed, Stored(), Message(machineId: 9999))
                .Decision,
        }.ToHashSet();

        Assert.Equal(Enum.GetValues<NayaxSaleRefundDecision>().ToHashSet(), produced);
    }

    private static NayaxStoredSaleValues Stored(long? nayaxProductId = NayaxProductId) =>
        new(
            MachineId,
            SettlementValue: 3.5m,

            // Not compared for a refund: the policy decides what is compared, and the record keeps
            // one shape for both comparisons.
            AuthorizationInstantUtc: null,
            CurrencyCode: null,
            nayaxProductId);

    private static NayaxRefundMessageValues Message(
        long machineId = MachineId,
        decimal settlementValue = 3.5m,
        long? nayaxProductId = NayaxProductId) =>
        new(machineId, settlementValue, nayaxProductId);
}
