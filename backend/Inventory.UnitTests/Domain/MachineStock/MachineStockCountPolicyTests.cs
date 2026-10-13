using Inventory.Domain.Exceptions;
using Inventory.Domain.MachineStock;
using Xunit;

namespace InventoryApi.Tests.Domain.MachineStock;

/// <summary>What an operator may record as a machine-stock count (issue #56).</summary>
public class MachineStockCountPolicyTests
{
    [Fact]
    public void An_empty_machine_is_a_valid_count() =>
        MachineStockCountPolicy.Validate(0, notes: null);

    [Fact]
    public void A_negative_count_is_refused()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => MachineStockCountPolicy.Validate(-1, notes: null));

        Assert.Equal("A counted machine stock quantity cannot be negative.", exception.Message);
    }

    [Fact]
    public void A_note_at_the_stored_length_is_accepted_and_a_longer_one_is_refused()
    {
        MachineStockCountPolicy.Validate(3, new string('n', MachineStockCountPolicy.MaxNotesLength));

        Assert.Throws<DomainValidationException>(
            () => MachineStockCountPolicy.Validate(3, new string('n', MachineStockCountPolicy.MaxNotesLength + 1)));
    }
}
