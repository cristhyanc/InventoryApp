using Inventory.Domain.Machines;
using Xunit;

namespace InventoryApi.Tests.Domain.Machines;

/// <summary>
/// <see cref="ProviderMachineLinkPolicy"/> (issue #742, parent #329 child G3): when a provider
/// machine may be linked without asking, when a link may be recorded at all, and what a machine
/// created for one is called.
/// </summary>
public class ProviderMachineLinkPolicyTests
{
    private const string ProviderMachine = "7";

    [Fact]
    public void A_business_with_no_unlinked_machine_of_its_own_links_automatically()
    {
        // Nothing local the new provider machine could be a duplicate of, so creating one invents
        // nothing and there is no question with more than one answer.
        Assert.True(ProviderMachineLinkPolicy.MayLinkAutomatically(anyLocalMachineWithoutProviderId: false));
    }

    [Fact]
    public void A_business_with_one_unlinked_machine_of_its_own_does_not()
    {
        // The whole failure this rule exists to prevent: a business that recorded its machines by
        // hand before connecting a provider would get a second row for every one of them.
        Assert.False(ProviderMachineLinkPolicy.MayLinkAutomatically(anyLocalMachineWithoutProviderId: true));
    }

    [Fact]
    public void Linking_to_an_existing_machine_needs_that_machine_named()
    {
        var refusal = ProviderMachineLinkPolicy.DecisionRefusal(
            ProviderMachineLinkAction.LinkExisting, localMachineId: null);

        Assert.NotNull(refusal);
        Assert.Contains("Nothing was changed.", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProviderMachineLinkAction.Defer)]
    [InlineData(ProviderMachineLinkAction.CreateNew)]
    public void A_machine_may_only_be_named_when_linking_to_it(ProviderMachineLinkAction action) =>
        Assert.NotNull(ProviderMachineLinkPolicy.DecisionRefusal(action, localMachineId: 4));

    [Theory]
    [InlineData(ProviderMachineLinkAction.Defer)]
    [InlineData(ProviderMachineLinkAction.CreateNew)]
    public void Those_actions_are_usable_without_a_machine_named(ProviderMachineLinkAction action) =>
        Assert.Null(ProviderMachineLinkPolicy.DecisionRefusal(action, localMachineId: null));

    [Fact]
    public void Linking_an_existing_machine_with_a_machine_named_is_usable() =>
        Assert.Null(ProviderMachineLinkPolicy.DecisionRefusal(
            ProviderMachineLinkAction.LinkExisting, localMachineId: 4));

    [Fact]
    public void A_machine_already_linked_to_another_provider_machine_is_refused()
    {
        var refusal = ProviderMachineLinkPolicy.LinkRefusal(
            ProviderMachine, chosenMachineProviderId: "9", anotherMachineCarriesIt: false);

        Assert.NotNull(refusal);
        Assert.Contains("9", refusal, StringComparison.Ordinal);
        Assert.Contains(ProviderMachine, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_machine_another_machine_already_carries_is_refused()
    {
        // The acceptance criterion in one rule: one business cannot link two machines to the same
        // provider machine id. The filtered unique index refuses it too; this says so first, and
        // with a sentence an operator can act on.
        var refusal = ProviderMachineLinkPolicy.LinkRefusal(
            ProviderMachine, chosenMachineProviderId: null, anotherMachineCarriesIt: true);

        Assert.NotNull(refusal);
        Assert.Contains(ProviderMachine, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_carrying_no_provider_id_may_be_linked() =>
        Assert.Null(ProviderMachineLinkPolicy.LinkRefusal(
            ProviderMachine, chosenMachineProviderId: null, anotherMachineCarriesIt: false));

    [Fact]
    public void Recording_the_id_a_machine_already_carries_is_not_a_refusal() =>
        // It is the state the apply was asked to reach, which is what makes applying the same
        // decision twice a no-op rather than an error.
        Assert.Null(ProviderMachineLinkPolicy.LinkRefusal(
            ProviderMachine, chosenMachineProviderId: ProviderMachine, anotherMachineCarriesIt: false));

    [Fact]
    public void A_created_machine_takes_the_providers_name_trimmed() =>
        Assert.Equal(
            "Lobby level 1",
            ProviderMachineLinkPolicy.LinkedMachineName("  Lobby level 1  ", ProviderMachine));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_machine_the_provider_names_nothing_takes_the_backfills_default_name(string? providerName) =>
        // The same shape AddMachines wrote for a machine nothing had named, so a machine linked now
        // is named the way one linked then was.
        Assert.Equal(
            MachineCataloguePolicy.DefaultName(ProviderMachine),
            ProviderMachineLinkPolicy.LinkedMachineName(providerName, ProviderMachine));
}
