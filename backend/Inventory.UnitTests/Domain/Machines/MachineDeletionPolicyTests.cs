using Inventory.Domain.Machines;
using Inventory.Domain.Providers;
using Xunit;

namespace InventoryApi.Tests.Domain.Machines;

/// <summary>
/// Whether one of the business's own machines may be deleted (issue #896). A machine added by
/// mistake is removed; a machine anything recorded against is kept, because the alternative - a
/// cascade that erases a count, a stock movement, a refill or an expense, or a raw database error -
/// is exactly what this policy exists to prevent (AGENTS.md § Inventory and historical costing
/// invariants).
/// </summary>
public class MachineDeletionPolicyTests
{
    [Fact]
    public void A_machine_nothing_refers_to_may_be_deleted()
    {
        Assert.Null(MachineDeletionPolicy.Refusal(
            isProviderNamed: false, provider: null, LocalMachineDeletionReferences.None));
    }

    [Theory]
    [InlineData(1, 0, 0, 0, "1 machine stock count")]
    [InlineData(3, 0, 0, 0, "3 machine stock counts")]
    [InlineData(0, 2, 0, 0, "2 stock movements")]
    [InlineData(0, 0, 1, 0, "1 pick list refill")]
    [InlineData(0, 0, 0, 4, "4 operating expenses")]
    public void Each_kind_of_history_refuses_the_delete_and_is_named_with_its_count(
        int machineStockCounts,
        int stockMovements,
        int pickListRefills,
        int operatingExpenses,
        string expected)
    {
        var refusal = MachineDeletionPolicy.Refusal(
            isProviderNamed: false,
            provider: null,
            new LocalMachineDeletionReferences(
                machineStockCounts, stockMovements, pickListRefills, operatingExpenses));

        Assert.NotNull(refusal);
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
        Assert.Contains("Deactivate it instead", refusal, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted.", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_kinds_of_history_are_all_named()
    {
        var refusal = MachineDeletionPolicy.Refusal(
            isProviderNamed: false,
            provider: null,
            new LocalMachineDeletionReferences(2, 1, 0, 3));

        Assert.Equal(
            "This machine cannot be deleted because its history refers to it (2 machine stock counts, "
                + "1 stock movement, 3 operating expenses). Deactivate it instead - a deactivated "
                + "machine keeps its history, stays listed and can be put back in service. Nothing "
                + "was deleted.",
            refusal);
    }

    /// <summary>
    /// A machine the provider names is refused even with no local history at all, and for its own
    /// reason: it is managed at the provider, which is also why it may not be deactivated here
    /// (PR #889), so the refusal deliberately does not offer deactivation.
    /// </summary>
    [Fact]
    public void A_provider_named_machine_may_not_be_deleted_even_with_no_history()
    {
        var refusal = MachineDeletionPolicy.Refusal(
            isProviderNamed: true, ExternalProvider.Nayax, LocalMachineDeletionReferences.None);

        Assert.Equal(
            "This machine comes from Nayax and cannot be deleted here. Nothing was deleted.",
            refusal);
    }

    /// <summary>
    /// The provider refusal wins over the history one: naming the provider is the actionable answer,
    /// and it is true whatever the counts are.
    /// </summary>
    [Fact]
    public void A_provider_named_machine_with_history_is_refused_as_a_provider_machine()
    {
        var refusal = MachineDeletionPolicy.Refusal(
            isProviderNamed: true,
            ExternalProvider.Nayax,
            new LocalMachineDeletionReferences(1, 1, 1, 1));

        Assert.NotNull(refusal);
        Assert.Contains("comes from Nayax", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("Deactivate it instead", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row carrying a provider id always belongs to a business whose <c>SalesProvider</c> is set,
    /// so the fallback describes a state the application cannot reach rather than guessing a name -
    /// the same wording rule the rename and deactivation refusals follow.
    /// </summary>
    [Fact]
    public void A_provider_named_machine_of_a_business_with_no_named_provider_is_still_refused()
    {
        var refusal = MachineDeletionPolicy.Refusal(
            isProviderNamed: true, provider: null, LocalMachineDeletionReferences.None);

        Assert.NotNull(refusal);
        Assert.Contains("this business's sales provider", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void References_report_whether_anything_refers_to_the_machine()
    {
        Assert.False(LocalMachineDeletionReferences.None.Any);
        Assert.True(new LocalMachineDeletionReferences(0, 0, 0, 1).Any);
    }

    /// <summary>
    /// The database's own backstop needs a sentence of its own: the restricted foreign keys refuse
    /// the delete when something was recorded against the machine after the count inside the
    /// delete's transaction, and that has to reach the operator as the same kind of refusal rather
    /// than as a server error.
    /// </summary>
    [Fact]
    public void The_concurrent_reference_refusal_says_what_happened_and_that_nothing_was_deleted()
    {
        Assert.Contains(
            "Deactivate it instead",
            MachineDeletionPolicy.ReferencedWhileDeletingRefusal,
            StringComparison.Ordinal);
        Assert.Contains(
            "Nothing was deleted.",
            MachineDeletionPolicy.ReferencedWhileDeletingRefusal,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The read's answer (the product pattern of issue #737) is the delete's answer: a machine
    /// nothing refers to is deletable and carries no reason to show.
    /// </summary>
    [Fact]
    public void Eligibility_allows_the_delete_for_a_machine_nothing_refers_to()
    {
        var eligibility = LocalMachineDeletionEligibility.Decide(
            isProviderNamed: false, provider: null, LocalMachineDeletionReferences.None);

        Assert.True(eligibility.Allowed);
        Assert.Null(eligibility.Refusal);
    }

    /// <summary>
    /// A history refusal is published as the very message the delete would answer with, so the row
    /// that offers no Delete explains it in the server's own words rather than inventing a second
    /// wording that could drift from the refusal a stale decision still produces.
    /// </summary>
    [Fact]
    public void Eligibility_refuses_a_machine_with_history_in_the_deletes_own_words()
    {
        var references = new LocalMachineDeletionReferences(0, 1, 0, 0);

        var eligibility = LocalMachineDeletionEligibility.Decide(
            isProviderNamed: false, provider: null, references);

        Assert.False(eligibility.Allowed);
        Assert.Equal(
            MachineDeletionPolicy.Refusal(isProviderNamed: false, provider: null, references),
            eligibility.Refusal);
    }

    [Fact]
    public void Eligibility_refuses_a_provider_named_machine_and_names_the_provider()
    {
        var eligibility = LocalMachineDeletionEligibility.Decide(
            isProviderNamed: true, ExternalProvider.Nayax, LocalMachineDeletionReferences.None);

        Assert.False(eligibility.Allowed);
        Assert.Contains("comes from Nayax", eligibility.Refusal!, StringComparison.Ordinal);
    }
}
