using Inventory.Domain.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Domain.NayaxSqs;

/// <summary>
/// The decision table of issue #1105: which decision a business Owner may make about a sale in which
/// state, resting on which kind of evidence, and what the sale becomes.
/// </summary>
public class NayaxSaleOwnerDecisionPolicyTests
{
    private const NayaxSaleProviderEvidenceKind Comparison = NayaxSaleProviderEvidenceKind.Comparison;
    private const NayaxSaleProviderEvidenceKind Absence = NayaxSaleProviderEvidenceKind.Absence;

    public static TheoryData<NayaxSqsVerification, NayaxSaleOwnerDecision, NayaxSaleProviderEvidenceKind, NayaxSqsVerification> Allowed() => new()
    {
        { NayaxSqsVerification.Conflict, NayaxSaleOwnerDecision.UseNayaxValues, Comparison, NayaxSqsVerification.OwnerCorrected },
        { NayaxSqsVerification.Conflict, NayaxSaleOwnerDecision.KeepSqsValues, Comparison, NayaxSqsVerification.OwnerConfirmed },
        { NayaxSqsVerification.Conflict, NayaxSaleOwnerDecision.DoNotCount, Comparison, NayaxSqsVerification.OwnerExcluded },
        { NayaxSqsVerification.Conflict, NayaxSaleOwnerDecision.DoNotCount, Absence, NayaxSqsVerification.OwnerExcluded },
        { NayaxSqsVerification.NeedsInvestigation, NayaxSaleOwnerDecision.DoNotCount, Comparison, NayaxSqsVerification.OwnerExcluded },
        { NayaxSqsVerification.NeedsInvestigation, NayaxSaleOwnerDecision.DoNotCount, Absence, NayaxSqsVerification.OwnerExcluded },
        { NayaxSqsVerification.NeedsInvestigation, NayaxSaleOwnerDecision.Genuine, Comparison, NayaxSqsVerification.OwnerConfirmed },
        { NayaxSqsVerification.NeedsInvestigation, NayaxSaleOwnerDecision.Genuine, Absence, NayaxSqsVerification.OwnerConfirmed },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Every_decision_in_the_table_is_allowed_and_gives_its_state(
        NayaxSqsVerification state,
        NayaxSaleOwnerDecision decision,
        NayaxSaleProviderEvidenceKind evidence,
        NayaxSqsVerification result)
    {
        var outcome = NayaxSaleOwnerDecisionPolicy.Evaluate(state, decision, evidence, "a reason");

        Assert.True(outcome.Allowed);
        Assert.Equal(result, outcome.ResultingState);
    }

    [Fact]
    public void Nothing_outside_the_table_is_allowed()
    {
        var allowed = Allowed()
            .Select(row => ((NayaxSqsVerification)row[0], (NayaxSaleOwnerDecision)row[1], (NayaxSaleProviderEvidenceKind)row[2]))
            .ToHashSet();

        foreach (var state in Enum.GetValues<NayaxSqsVerification>())
        {
            foreach (var decision in Enum.GetValues<NayaxSaleOwnerDecision>())
            {
                foreach (var evidence in Enum.GetValues<NayaxSaleProviderEvidenceKind>())
                {
                    if (!allowed.Contains((state, decision, evidence)))
                    {
                        Assert.False(
                            NayaxSaleOwnerDecisionPolicy.Evaluate(state, decision, evidence, "a reason").Allowed,
                            $"{decision} on {state} with {evidence} must be refused.");
                    }
                }
            }
        }

        Assert.False(NayaxSaleOwnerDecisionPolicy.Evaluate(null, NayaxSaleOwnerDecision.DoNotCount, Comparison, "r").Allowed);
    }

    [Fact]
    public void This_sale_is_genuine_is_refused_on_a_conflict()
    {
        var outcome = NayaxSaleOwnerDecisionPolicy.Evaluate(
            NayaxSqsVerification.Conflict, NayaxSaleOwnerDecision.Genuine, Comparison, "it was real");

        Assert.Equal(NayaxSaleOwnerDecisionRefusal.NotAllowedForState, outcome.Refusal);
    }

    [Theory]
    [InlineData(NayaxSaleOwnerDecision.UseNayaxValues)]
    [InlineData(NayaxSaleOwnerDecision.KeepSqsValues)]
    public void A_conflict_resolution_is_refused_on_absence_evidence(NayaxSaleOwnerDecision decision)
    {
        var outcome = NayaxSaleOwnerDecisionPolicy.Evaluate(NayaxSqsVerification.Conflict, decision, Absence, "r");

        Assert.Equal(NayaxSaleOwnerDecisionRefusal.NotAllowedForEvidence, outcome.Refusal);
    }

    [Theory]
    [InlineData(NayaxSaleOwnerDecision.KeepSqsValues, NayaxSqsVerification.Conflict)]
    [InlineData(NayaxSaleOwnerDecision.DoNotCount, NayaxSqsVerification.Conflict)]
    [InlineData(NayaxSaleOwnerDecision.Genuine, NayaxSqsVerification.NeedsInvestigation)]
    public void Every_decision_but_use_nayax_values_needs_a_reason(
        NayaxSaleOwnerDecision decision, NayaxSqsVerification state)
    {
        Assert.Equal(
            NayaxSaleOwnerDecisionRefusal.ReasonRequired,
            NayaxSaleOwnerDecisionPolicy.Evaluate(state, decision, Comparison, "   ").Refusal);
        Assert.True(NayaxSaleOwnerDecisionPolicy.Evaluate(
            NayaxSqsVerification.Conflict, NayaxSaleOwnerDecision.UseNayaxValues, Comparison, null).Allowed);
    }

    [Fact]
    public void A_reason_longer_than_the_limit_is_refused()
    {
        var reason = new string('x', NayaxSaleOwnerDecisionPolicy.MaxReasonLength + 1);

        Assert.Equal(
            NayaxSaleOwnerDecisionRefusal.ReasonTooLong,
            NayaxSaleOwnerDecisionPolicy.Evaluate(
                NayaxSqsVerification.Conflict, NayaxSaleOwnerDecision.KeepSqsValues, Comparison, reason).Refusal);
    }

    [Fact]
    public void The_page_is_offered_only_what_the_table_allows()
    {
        Assert.Equal(
            [NayaxSaleOwnerDecision.UseNayaxValues, NayaxSaleOwnerDecision.KeepSqsValues, NayaxSaleOwnerDecision.DoNotCount],
            NayaxSaleOwnerDecisionPolicy.Available(NayaxSqsVerification.Conflict, Comparison));
        Assert.Equal(
            [NayaxSaleOwnerDecision.DoNotCount, NayaxSaleOwnerDecision.Genuine],
            NayaxSaleOwnerDecisionPolicy.Available(NayaxSqsVerification.NeedsInvestigation, Absence));
        Assert.Empty(NayaxSaleOwnerDecisionPolicy.Available(NayaxSqsVerification.OwnerConfirmed, Comparison));
        Assert.Empty(NayaxSaleOwnerDecisionPolicy.Available(NayaxSqsVerification.Conflict, null));
    }
}
