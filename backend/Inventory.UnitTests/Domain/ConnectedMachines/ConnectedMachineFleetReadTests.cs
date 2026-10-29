using Inventory.Domain.ConnectedMachines;
using Xunit;

namespace InventoryApi.Tests.Domain.ConnectedMachines;

/// <summary>
/// What a fleet read counts, and the canonical form the ids are stored in (issue #978).
///
/// <para>The count is the number of machines on the business's Nayax account, so what this file has
/// to pin is which entries of a fleet answer are machines: every one Nayax returned with a usable id,
/// counted once, and nothing else - no local machine, no duplicate, and no entry that identifies no
/// machine at all.</para>
/// </summary>
public class ConnectedMachineFleetReadTests
{
    /// <summary>
    /// Every machine the fleet read returns is counted. There is no status filtering: the Lynx fleet
    /// contract publishes <c>MachineStatusBit</c> only as "a bit field indicating the status of the
    /// machine", and the one place the Nayax documentation gives it a meaning is the create-machine
    /// guide, as a request value - so what a read returns for an inactive or removed machine is not
    /// confirmed and this application does not deserialise the field at all (AGENTS.md § Nayax
    /// contract verification).
    /// </summary>
    [Fact]
    public void Counts_every_machine_the_fleet_read_returned()
    {
        var read = ConnectedMachineFleetRead.Complete([101, 102, 103]);

        Assert.Equal(3, read.Count);
        Assert.Equal(new long[] { 101, 102, 103 }, read.ProviderMachineIds);
        Assert.True(read.IsComplete);
    }

    /// <summary>
    /// One machine is one machine however many times the answer names it, and the stored order is
    /// the same for one fleet whatever order Nayax answered in - so one day's evidence cannot differ
    /// from another's by the order of a response.
    /// </summary>
    [Fact]
    public void Counts_each_machine_once_and_records_the_ids_in_one_order()
    {
        var read = ConnectedMachineFleetRead.Complete([103, 101, 102, 101]);

        Assert.Equal(3, read.Count);
        Assert.Equal(new long[] { 101, 102, 103 }, read.ProviderMachineIds);
        Assert.Equal("101,102,103", read.FormatProviderMachineIds());
    }

    /// <summary>
    /// An entry with no id, or with an id that cannot identify a machine, is not counted: it names no
    /// machine, and a count has to be explainable from the ids stored beside it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Does_not_count_an_entry_that_identifies_no_machine(long? unusableId)
    {
        var read = ConnectedMachineFleetRead.Complete([101, unusableId]);

        Assert.Equal(1, read.Count);
        Assert.Equal(new long[] { 101 }, read.ProviderMachineIds);
    }

    /// <summary>
    /// A manual machine has no provider id, so it is never in a fleet answer and can never be
    /// counted. The rule is structural rather than a filter: this type only ever sees what Nayax
    /// returned.
    /// </summary>
    [Fact]
    public void A_fleet_with_no_machines_is_a_count_of_none()
    {
        var read = ConnectedMachineFleetRead.Complete([]);

        Assert.Equal(0, read.Count);
        Assert.Empty(read.ProviderMachineIds);
        Assert.Equal(string.Empty, read.FormatProviderMachineIds());
    }

    /// <summary>An incomplete read still says what it saw, and says that it is incomplete.</summary>
    [Fact]
    public void An_incomplete_read_is_marked_incomplete()
    {
        var read = ConnectedMachineFleetRead.Incomplete([101, 102]);

        Assert.False(read.IsComplete);
        Assert.Equal(2, read.Count);
    }

    /// <summary>
    /// The stored form round-trips, because a billing question answered months later is answered from
    /// that text. An absent or blank value is no machines - a real count of none.
    /// </summary>
    [Theory]
    [InlineData(null, new long[0])]
    [InlineData("", new long[0])]
    [InlineData("101", new[] { 101L })]
    [InlineData("101,102,103", new[] { 101L, 102L, 103L })]
    [InlineData(" 101 , 102 ", new[] { 101L, 102L })]
    public void The_stored_id_form_round_trips(string? stored, long[] expected)
    {
        Assert.Equal(expected, ConnectedMachineFleetRead.ParseIds(stored));
    }

    /// <summary>
    /// An unreadable stored value fails rather than being read as a smaller fleet: a snapshot is
    /// billing evidence, and silently losing a machine from it is the one outcome worse than an
    /// error.
    /// </summary>
    [Fact]
    public void An_unreadable_stored_id_list_fails_rather_than_counting_fewer_machines()
    {
        Assert.Throws<FormatException>(() => ConnectedMachineFleetRead.ParseIds("101,not-an-id"));
    }
}
