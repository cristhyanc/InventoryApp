using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Domain.NayaxSqs;

/// <summary>
/// The completed-sale rule of issue #1105: status <c>12</c> and a verification that is not
/// <c>Conflict</c> or <c>OwnerExcluded</c>. Only an authoritative contradiction excludes
/// automatically; a sale needing investigation keeps counting.
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
