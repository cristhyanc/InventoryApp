using Inventory.Domain.Machines;
using Xunit;

namespace InventoryApi.Tests.Domain.Machines;

/// <summary>
/// The one way a machine is named in a caller-facing message (issue #1240), beside
/// <c>ProductLabel</c> and with the same rules: the name is shown with the id so an operator knows
/// which machine to go to without looking the id up, and a machine whose name was never loaded - or
/// is blank - keeps the bare-id wording rather than showing empty brackets.
/// </summary>
public class MachineLabelTests
{
    [Fact]
    public void A_named_machine_is_shown_as_its_name_and_id()
    {
        Assert.Equal("machine Front Lobby (7)", MachineLabel.Phrase(7, "Front Lobby"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_missing_or_blank_name_falls_back_to_the_bare_id(string? name)
    {
        Assert.Equal("machine 7", MachineLabel.Phrase(7, name));
    }

    [Fact]
    public void The_id_is_rendered_invariantly_whatever_its_magnitude()
    {
        Assert.Equal("machine 9007199254740993", MachineLabel.Phrase(9007199254740993, null));
    }
}
