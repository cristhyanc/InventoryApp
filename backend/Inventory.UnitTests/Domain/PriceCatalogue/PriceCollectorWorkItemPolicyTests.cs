using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The consistency rules of one issued piece of collector work (issue #924): which product it names,
/// and what the collector may later say about it.
/// </summary>
public class PriceCollectorWorkItemPolicyTests
{
    /// <summary>
    /// A work item names exactly one product, in exactly one of the two ways a product can be named.
    /// Both at once is a search claiming to be about two products; neither is work no result could
    /// ever be attributed to, which is the whole reason work items exist.
    /// </summary>
    [Theory]
    [InlineData(7L, null, true)]
    [InlineData(null, "16:COCA-COLACLASSIC|5:375ML", true)]
    [InlineData(7L, "16:COCA-COLACLASSIC|5:375ML", false)]
    [InlineData(null, null, false)]
    public void A_work_item_names_exactly_one_product(long? masterProductId, string? waitingKey, bool isConsistent)
    {
        Assert.Equal(
            isConsistent,
            PriceCollectorWorkItemPolicy.HasExactlyOneProduct(masterProductId, waitingKey));

        var refusal = Record.Exception(
            () => PriceCollectorWorkItemPolicy.RequireConsistentWorkItem(
                masterProductId, waitingKey, outcome: null));

        if (isConsistent)
        {
            Assert.Null(refusal);
            return;
        }

        Assert.Equal(
            PriceCollectorWorkItemPolicy.ExactlyOneProductMessage,
            Assert.IsType<DomainValidationException>(refusal).Message);
    }

    /// <summary>
    /// A blank waiting key is no waiting key. A stored empty string would be a key nothing can ever
    /// be found under, so it is refused beside a null one rather than accepted as a second spelling
    /// of "waiting".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_waiting_key_is_no_waiting_key(string waitingKey) =>
        Assert.False(PriceCollectorWorkItemPolicy.HasExactlyOneProduct(
            masterProductId: null, waitingKey));

    /// <summary>
    /// No outcome is a state, and it is the one a work item is issued in. It is deliberately not the
    /// same thing as <see cref="PriceCollectorWorkItemOutcome.NotSearched"/>, which is a collector
    /// saying it chose not to look.
    /// </summary>
    [Fact]
    public void An_empty_outcome_is_accepted_because_issued_work_has_none() =>
        Assert.True(PriceCollectorWorkItemPolicy.IsDeclaredOutcome(null));

    /// <summary>Every declared outcome is accepted, and the five of them are the complete set.</summary>
    [Fact]
    public void The_declared_outcomes_are_the_five_the_issue_names()
    {
        Assert.Equal(
            ["Complete", "Truncated", "NoMatches", "Failed", "NotSearched"],
            Enum.GetNames<PriceCollectorWorkItemOutcome>());

        Assert.All(
            Enum.GetValues<PriceCollectorWorkItemOutcome>(),
            outcome => Assert.True(PriceCollectorWorkItemPolicy.IsDeclaredOutcome(outcome)));
    }

    /// <summary>
    /// An outcome that is not a declared one is refused, because an enum is a compile-time constraint
    /// and not a validation: a JSON number binds whatever integer it likes, and a stored number no
    /// outcome covers would be read as an answer nobody gave.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(-1)]
    public void An_undeclared_outcome_is_refused(int outcome)
    {
        Assert.False(PriceCollectorWorkItemPolicy.IsDeclaredOutcome((PriceCollectorWorkItemOutcome)outcome));

        var refusal = Assert.Throws<DomainValidationException>(
            () => PriceCollectorWorkItemPolicy.RequireConsistentWorkItem(
                masterProductId: 7, waitingKey: null, (PriceCollectorWorkItemOutcome)outcome));

        Assert.Equal(PriceCollectorWorkItemPolicy.UnknownOutcomeMessage, refusal.Message);
    }
}
