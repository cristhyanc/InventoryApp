using Inventory.Application.Machines;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Machines;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// <c>/api/local-machines</c> (issue #740, parent #329 child G1): the HTTP shape of create, rename,
/// deactivate and reactivate over the business's own machine records.
/// </summary>
public class LocalMachinesControllerTests
{
    private static LocalMachinesController CreateController(FakeLocalMachineStore store) =>
        new(
            new ListLocalMachines(store),
            new GetLocalMachine(store),
            new CreateLocalMachine(store),
            new RenameLocalMachine(store),
            new SetLocalMachineActivation(store));

    [Fact]
    public async Task GetAll_returns_ok_with_active_and_inactive_machines_ordered_by_name()
    {
        var store = new FakeLocalMachineStore(
        [
            new LocalMachineRecord(1, "Zebra bay", IsActive: false),
            new LocalMachineRecord(2, "Atrium", IsActive: true, IsProviderNamed: true),
        ]);
        var controller = CreateController(store);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var machines = Assert.IsAssignableFrom<IEnumerable<LocalMachineResponse>>(ok.Value).ToList();
        Assert.Equal(["Atrium", "Zebra bay"], machines.Select(machine => machine.Name));
        Assert.Equal([true, false], machines.Select(machine => machine.IsActive));
        // Whether the business's provider names the machine is published; its identifier is not,
        // and no field of the request corresponds to it.
        Assert.Equal([true, false], machines.Select(machine => machine.IsProviderNamed));
    }

    [Fact]
    public async Task Get_returns_ok_for_an_existing_machine_and_not_found_otherwise()
    {
        var store = new FakeLocalMachineStore([new LocalMachineRecord(1, "Atrium", IsActive: true)]);
        var controller = CreateController(store);

        var ok = Assert.IsType<OkObjectResult>((await controller.Get(1, CancellationToken.None)).Result);
        Assert.Equal("Atrium", Assert.IsType<LocalMachineResponse>(ok.Value).Name);

        Assert.IsType<NotFoundResult>((await controller.Get(999, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Create_returns_created_with_the_machine()
    {
        var store = new FakeLocalMachineStore();
        var controller = CreateController(store);

        var result = await controller.Create(new LocalMachineDto("Lobby level 1"), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<LocalMachineResponse>(created.Value);
        Assert.Equal(("Lobby level 1", true, false), (response.Name, response.IsActive, response.IsProviderNamed));
        Assert.Equal(nameof(LocalMachinesController.Get), created.ActionName);
    }

    [Fact]
    public async Task Create_returns_bad_request_with_the_refusal_for_a_machine_with_no_name()
    {
        var store = new FakeLocalMachineStore();
        var controller = CreateController(store);

        var result = await controller.Create(new LocalMachineDto("  "), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Machine name is required.", badRequest.Value);
    }

    [Fact]
    public async Task Rename_returns_no_content_and_not_found_and_bad_request()
    {
        var store = new FakeLocalMachineStore([new LocalMachineRecord(1, "Machine 7", IsActive: true)]);
        var controller = CreateController(store);

        Assert.IsType<NoContentResult>(
            await controller.Rename(1, new LocalMachineDto("Lobby level 1"), CancellationToken.None));
        Assert.IsType<NotFoundResult>(
            await controller.Rename(999, new LocalMachineDto("Anything"), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(
            await controller.Rename(1, new LocalMachineDto(" "), CancellationToken.None));

        Assert.Equal("Lobby level 1", (await store.FindByIdAsync(1, CancellationToken.None))!.Name);
    }

    [Fact]
    public async Task Deactivate_and_reactivate_return_no_content_and_change_the_machine()
    {
        var store = new FakeLocalMachineStore([new LocalMachineRecord(1, "Atrium", IsActive: true)]);
        var controller = CreateController(store);

        Assert.IsType<NoContentResult>(await controller.Deactivate(1, CancellationToken.None));
        Assert.False((await store.FindByIdAsync(1, CancellationToken.None))!.IsActive);

        Assert.IsType<NoContentResult>(await controller.Reactivate(1, CancellationToken.None));
        Assert.True((await store.FindByIdAsync(1, CancellationToken.None))!.IsActive);
    }

    [Fact]
    public async Task Deactivate_and_reactivate_return_not_found_for_a_machine_of_another_business()
    {
        var store = new FakeLocalMachineStore();
        var controller = CreateController(store);

        Assert.IsType<NotFoundResult>(await controller.Deactivate(999, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Reactivate(999, CancellationToken.None));
    }

    /// <summary>
    /// There is no delete action at all: stored sales, refills, counts and expenses refer to a
    /// machine, so a machine that stops trading is deactivated rather than removed.
    /// </summary>
    [Fact]
    public void The_resource_offers_no_delete()
    {
        var actions = typeof(LocalMachinesController)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("Delete", actions);
    }
}
