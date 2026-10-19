using Inventory.Domain.Machines;
using Xunit;

namespace InventoryApi.Tests.Domain.Machines;

/// <summary>
/// <see cref="MachineCataloguePolicy"/> (issue #740): the invariants on a machine an operator
/// maintains by hand.
/// </summary>
public class MachineCataloguePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_machine_with_no_name_is_refused(string? name) =>
        Assert.Equal(MachineCataloguePolicy.NameRequiredMessage, MachineCataloguePolicy.Validate(name));

    [Fact]
    public void A_machine_with_a_name_is_accepted() =>
        Assert.Null(MachineCataloguePolicy.Validate("Lobby level 1"));

    [Fact]
    public void A_name_is_stored_trimmed_because_trailing_space_is_not_another_machine() =>
        Assert.Equal("Lobby level 1", MachineCataloguePolicy.Normalize("  Lobby level 1 "));

    /// <summary>
    /// The shape the <c>AddMachines</c> backfill writes in SQL for a machine nothing ever named. It
    /// is stated here because a migration cannot call into the Domain, and
    /// <c>AddMachinesMigrationTests</c> compares the two.
    /// </summary>
    [Theory]
    [InlineData("7", "Machine 7")]
    [InlineData("1234567890123", "Machine 1234567890123")]
    public void The_default_name_is_the_word_Machine_and_the_providers_own_id(string externalId, string expected) =>
        Assert.Equal(expected, MachineCataloguePolicy.DefaultName(externalId));
}
