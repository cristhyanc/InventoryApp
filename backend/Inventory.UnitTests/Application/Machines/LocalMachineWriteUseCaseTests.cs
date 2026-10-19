using Inventory.Application.Machines;
using Inventory.Domain.Machines;
using Xunit;

namespace InventoryApi.Tests.Application.Machines;

/// <summary>
/// The machine write use cases (issue #740): what they decide themselves, and what they leave to the
/// store.
///
/// <para>They validate the submitted name - the one rule that needs no database - and refuse before
/// the store is reached at all, which is what keeps a blank name from ever becoming a row. Whether
/// the machine exists is a statement about stored rows and belongs to the store, where the read and
/// the write are one operation.</para>
/// </summary>
public class LocalMachineWriteUseCaseTests
{
    private static LocalMachineWriteFields Named(string? name) => new(name);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Create_refuses_a_machine_with_no_name_without_reaching_the_store(string? name)
    {
        var store = new FakeLocalMachineStore();

        var attempt = await new CreateLocalMachine(store).Handle(Named(name), CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.Refused, attempt.Outcome);
        Assert.Equal(MachineCataloguePolicy.NameRequiredMessage, attempt.Refusal);
        Assert.Null(store.LastWritten);
    }

    [Fact]
    public async Task Create_hands_the_submitted_name_to_the_store_and_reports_the_new_id()
    {
        var store = new FakeLocalMachineStore();

        var attempt = await new CreateLocalMachine(store).Handle(
            Named("Lobby level 1"), CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.Succeeded, attempt.Outcome);
        Assert.Equal(store.LastWrittenId, attempt.MachineId);
        Assert.Equal("Lobby level 1", store.LastWritten!.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Rename_refuses_a_blank_name_without_reaching_the_store(string? name)
    {
        var store = new FakeLocalMachineStore([new LocalMachineRecord(1, "Atrium", IsActive: true)]);

        var attempt = await new RenameLocalMachine(store).Handle(1, Named(name), CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.Refused, attempt.Outcome);
        Assert.Equal(MachineCataloguePolicy.NameRequiredMessage, attempt.Refusal);
        Assert.Null(store.LastWritten);
    }

    [Fact]
    public async Task Rename_reports_the_stores_not_found_answer_for_a_machine_of_another_business()
    {
        var store = new FakeLocalMachineStore();

        var attempt = await new RenameLocalMachine(store).Handle(
            99, Named("Mine now"), CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.NotFound, attempt.Outcome);
    }

    [Fact]
    public async Task Setting_the_activation_passes_the_direction_through_and_reports_not_found()
    {
        var store = new FakeLocalMachineStore([new LocalMachineRecord(1, "Atrium", IsActive: true)]);
        var useCase = new SetLocalMachineActivation(store);

        Assert.Equal(
            LocalMachineWriteOutcome.Succeeded,
            (await useCase.Handle(1, isActive: false, CancellationToken.None)).Outcome);
        Assert.False(store.LastActivation);
        Assert.False((await store.FindByIdAsync(1, CancellationToken.None))!.IsActive);

        Assert.Equal(
            LocalMachineWriteOutcome.Succeeded,
            (await useCase.Handle(1, isActive: true, CancellationToken.None)).Outcome);
        Assert.True(store.LastActivation);

        Assert.Equal(
            LocalMachineWriteOutcome.NotFound,
            (await useCase.Handle(99, isActive: false, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Listing_returns_the_stores_machines_in_its_order()
    {
        var store = new FakeLocalMachineStore(
        [
            new LocalMachineRecord(1, "Zebra bay", IsActive: false),
            new LocalMachineRecord(2, "Atrium", IsActive: true, IsProviderNamed: true),
        ]);

        var machines = await new ListLocalMachines(store).Handle(CancellationToken.None);

        Assert.Equal(["Atrium", "Zebra bay"], machines.Select(machine => machine.Name));
    }

    [Fact]
    public async Task Getting_one_machine_returns_it_or_nothing()
    {
        var store = new FakeLocalMachineStore([new LocalMachineRecord(1, "Atrium", IsActive: true)]);
        var useCase = new GetLocalMachine(store);

        Assert.Equal("Atrium", (await useCase.Handle(1, CancellationToken.None))!.Name);
        Assert.Null(await useCase.Handle(99, CancellationToken.None));
    }
}
