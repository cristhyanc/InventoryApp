using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Domain.NayaxSqs;

/// <summary>
/// The two rules this policy states.
///
/// <para>The completed-sale rule of issue #1105: status <c>12</c> and a verification that is not
/// <c>Conflict</c> or <c>OwnerExcluded</c>. Only an authoritative contradiction excludes
/// automatically; a sale needing investigation keeps counting.</para>
///
/// <para>And the stock rule of issue #1107: status <c>12</c> <em>or</em> status <c>62</c>, under the
/// same two exclusions - because a refunded sale dispensed its unit and only its money was
/// reversed.</para>
/// </summary>
public class NayaxSaleCountingPolicyTests
{
    public static TheoryData<NayaxSqsVerification?, bool> EveryState() => new()
    {
        { null, true },
        { NayaxSqsVerification.Unverified, true },
        { NayaxSqsVerification.Verified, true },
        { NayaxSqsVerification.Conflict, false },
        { NayaxSqsVerification.NeedsInvestigation, true },
        { NayaxSqsVerification.OwnerConfirmed, true },
        { NayaxSqsVerification.OwnerCorrected, true },
        { NayaxSqsVerification.OwnerExcluded, false },
    };

    [Theory]
    [MemberData(nameof(EveryState))]
    public void A_status_12_sale_counts_unless_contradicted_or_excluded_by_the_owner(
        NayaxSqsVerification? state, bool counted)
    {
        Assert.Equal(counted, NayaxSaleCountingPolicy.IsCountedSale(NayaxTransactionStatusIds.Completed, state));
    }

    [Fact]
    public void Every_declared_state_is_decided_by_the_table_above()
    {
        var decided = EveryState().Select(row => (NayaxSqsVerification?)row[0]).ToHashSet();
        foreach (var state in Enum.GetValues<NayaxSqsVerification>())
        {
            Assert.Contains(state, decided);
        }
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public void A_sale_that_is_not_status_12_never_counts_whatever_its_state(NayaxSqsVerification? state, bool _)
    {
        Assert.False(NayaxSaleCountingPolicy.IsCountedSale(250, state));
        Assert.False(NayaxSaleCountingPolicy.IsCountedSale(null, state));
    }

    /// <summary>
    /// <strong>The split of issue #1107.</strong> A refunded sale is not a completed sale - its money
    /// is reversed, exactly as status <c>62</c> has always behaved - and it still consumed its unit,
    /// because the machine dispensed it and the business no longer owns it.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryState))]
    public void A_refunded_sale_never_counts_and_still_consumes_stock(
        NayaxSqsVerification? state, bool countedWhenCompleted)
    {
        Assert.False(NayaxSaleCountingPolicy.IsCountedSale(NayaxTransactionStatusIds.Refunded, state));

        // The same two exclusions, which is the point of writing the rule over both columns: a
        // contradicted or Owner-excluded sale says nothing reliable about what left the machine
        // either, so it consumes nothing whatever its status.
        Assert.Equal(
            countedWhenCompleted,
            NayaxSaleCountingPolicy.ConsumesStock(NayaxTransactionStatusIds.Refunded, state));
    }

    /// <summary>A completed sale consumes stock on exactly the same terms as it counts.</summary>
    [Theory]
    [MemberData(nameof(EveryState))]
    public void A_status_12_sale_consumes_stock_exactly_when_it_counts(
        NayaxSqsVerification? state, bool counted)
    {
        Assert.Equal(
            counted, NayaxSaleCountingPolicy.ConsumesStock(NayaxTransactionStatusIds.Completed, state));
    }

    /// <summary>
    /// And every other status consumes nothing, whatever its state: a pending, cancelled, declined
    /// or unknown transaction never dispensed anything. <c>55</c> and <c>80</c> are pending,
    /// <c>26</c>, <c>28</c>, <c>31</c> and <c>250</c> are cancelled or declined, <c>999</c> is
    /// unrecognised and <see langword="null"/> is no status at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStatusThatConsumesNothing))]
    public void No_other_status_consumes_stock_whatever_its_state(int? statusId)
    {
        foreach (var state in EveryState().Select(row => (NayaxSqsVerification?)row[0]))
        {
            Assert.False(NayaxSaleCountingPolicy.ConsumesStock(statusId, state));
        }
    }

    public static TheoryData<int?> EveryStatusThatConsumesNothing() =>
        new() { 26, 28, 31, 55, 80, 250, 999, null };

    /// <summary>
    /// Every established status id is decided by one of the two claims above, so a status added to
    /// <c>NayaxTransactionStatusIds</c> later cannot quietly inherit "consumes stock".
    /// </summary>
    [Fact]
    public void Exactly_status_12_and_status_62_consume_stock_among_the_established_ids()
    {
        var consuming = new[]
            {
                NayaxTransactionStatusIds.Completed,
                NayaxTransactionStatusIds.CancelledOrDeclined26,
                NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed,
                NayaxTransactionStatusIds.CancelledOrDeclined31,
                NayaxTransactionStatusIds.PendingSettlementNotFinal,
                NayaxTransactionStatusIds.Refunded,
                NayaxTransactionStatusIds.PendingBatch,
                NayaxTransactionStatusIds.CancelledOrDeclined250,
            }
            .Where(status => NayaxSaleCountingPolicy.ConsumesStock(status, verification: null))
            .ToArray();

        Assert.Equal(
            [NayaxTransactionStatusIds.Completed, NayaxTransactionStatusIds.Refunded], consuming);
    }

    [Fact]
    public void Only_a_contradiction_excludes_automatically_never_a_sale_needing_investigation()
    {
        Assert.True(NayaxSaleCountingPolicy.IsExcluded(NayaxSqsVerification.Conflict));
        Assert.False(NayaxSaleCountingPolicy.IsExcluded(NayaxSqsVerification.NeedsInvestigation));
    }

    [Fact]
    public void An_excluded_status_12_sale_is_reported_as_excluded_by_review_and_says_why()
    {
        Assert.Equal(
            NayaxTransactionStatus.ExcludedByReview,
            NayaxSaleCountingPolicy.Classify(NayaxTransactionStatusIds.Completed, NayaxSqsVerification.Conflict));
        Assert.Equal(
            "Not counted - contradicted by Nayax",
            NayaxSaleCountingPolicy.Describe(NayaxTransactionStatusIds.Completed, NayaxSqsVerification.Conflict));
        Assert.Equal(
            "Not counted - excluded by the owner",
            NayaxSaleCountingPolicy.Describe(NayaxTransactionStatusIds.Completed, NayaxSqsVerification.OwnerExcluded));
        Assert.Equal(
            NayaxTransactionStatus.Completed,
            NayaxSaleCountingPolicy.Classify(NayaxTransactionStatusIds.Completed, NayaxSqsVerification.NeedsInvestigation));
    }

    [Fact]
    public void A_non_completed_sale_keeps_its_own_status_even_when_excluded()
    {
        Assert.Equal(
            NayaxTransactionStatusClassifier.Classify(250),
            NayaxSaleCountingPolicy.Classify(250, NayaxSqsVerification.Conflict));
    }

    [Theory]
    [InlineData(NayaxSqsVerification.Unverified, NayaxSqsVerification.Conflict, true)]
    [InlineData(NayaxSqsVerification.Conflict, NayaxSqsVerification.OwnerConfirmed, true)]
    [InlineData(NayaxSqsVerification.Conflict, NayaxSqsVerification.OwnerExcluded, false)]
    [InlineData(NayaxSqsVerification.NeedsInvestigation, NayaxSqsVerification.OwnerExcluded, true)]
    [InlineData(NayaxSqsVerification.NeedsInvestigation, NayaxSqsVerification.OwnerConfirmed, false)]
    [InlineData(NayaxSqsVerification.OwnerExcluded, NayaxSqsVerification.Conflict, false)]
    public void Changes_counting_is_whether_the_sale_moves_into_or_out_of_the_totals(
        NayaxSqsVerification from, NayaxSqsVerification to, bool changes)
    {
        Assert.Equal(changes, NayaxSaleCountingPolicy.ChangesCounting(from, to));
    }
}
