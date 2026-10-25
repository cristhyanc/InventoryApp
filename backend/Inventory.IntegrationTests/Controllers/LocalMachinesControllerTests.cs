using Inventory.Application.Machines;
using Inventory.Domain.Machines;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Machines;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// <c>/api/local-machines</c> (issue #740, parent #329 child G1): the HTTP shape of create, rename,
/// deactivate, reactivate and - since issue #896 - delete over the business's own machine records.
/// </summary>
public class LocalMachinesControllerTests
{
    private static LocalMachinesController CreateController(FakeLocalMachineStore store) =>
        new(
            new ListLocalMachines(store),
            new GetLocalMachine(store),
            new CreateLocalMachine(store),
            new RenameLocalMachine(store),
            new SetLocalMachineActivation(store),
            new DeleteLocalMachine(store));

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

    /// <summary>
    /// A rename or deactivation the store refuses - a machine the provider names - is a 400 carrying
    /// the store's own sentence, which the page shows as it is.
    /// </summary>
    [Fact]
    public async Task Rename_and_deactivate_return_bad_request_with_the_stores_refusal()
    {
        const string refusal = "This machine comes from Nayax and cannot be renamed here. Nothing was saved.";
        var store = new FakeLocalMachineStore(
            [new LocalMachineRecord(1, "Machine 7", IsActive: true, IsProviderNamed: true)])
        {
            RefusalToReturn = refusal,
        };
        var controller = CreateController(store);

        var renamed = Assert.IsType<BadRequestObjectResult>(
            await controller.Rename(1, new LocalMachineDto("Lobby level 1"), CancellationToken.None));
        Assert.Equal(refusal, renamed.Value);
        var deactivated = Assert.IsType<BadRequestObjectResult>(
            await controller.Deactivate(1, CancellationToken.None));
        Assert.Equal(refusal, deactivated.Value);

        var unchanged = (await store.FindByIdAsync(1, CancellationToken.None))!;
        Assert.Equal(("Machine 7", true), (unchanged.Name, unchanged.IsActive));
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
    /// The delete answers <c>204</c> and removes the machine (issue #896). Before that issue this
    /// resource deliberately had no delete action at all; the rule changed to "only a machine nothing
    /// refers to", which the store decides - what the controller owns is the status code.
    /// </summary>
    [Fact]
    public async Task Delete_returns_no_content_and_removes_the_machine()
    {
        var store = new FakeLocalMachineStore([new LocalMachineRecord(1, "Added by mistake", IsActive: true)]);
        var controller = CreateController(store);

        Assert.IsType<NoContentResult>(await controller.Delete(1, CancellationToken.None));

        Assert.Equal(1, store.LastDeletedId);
        Assert.Null(await store.FindByIdAsync(1, CancellationToken.None));
    }

    /// <summary>
    /// A refused delete - a machine with history, or one the provider names - is a <c>400</c>
    /// carrying the store's own sentence, the same shape the rename and deactivation refusals use,
    /// and the machine is still there.
    /// </summary>
    [Fact]
    public async Task Delete_returns_bad_request_with_the_stores_refusal_and_keeps_the_machine()
    {
        const string refusal =
            "This machine cannot be deleted because its history refers to it (1 operating expense).";
        var store = new FakeLocalMachineStore([new LocalMachineRecord(1, "Atrium", IsActive: true)])
        {
            RefusalToReturn = refusal,
        };
        var controller = CreateController(store);

        var badRequest = Assert.IsType<BadRequestObjectResult>(await controller.Delete(1, CancellationToken.None));

        Assert.Equal(refusal, badRequest.Value);
        Assert.NotNull(await store.FindByIdAsync(1, CancellationToken.None));
    }

    /// <summary>
    /// A machine of another business is invisible through the central tenant query filters, so the
    /// delete reports the same <c>404</c> an unknown id gets - never a refusal that would confirm the
    /// row exists somewhere.
    /// </summary>
    [Fact]
    public async Task Delete_returns_not_found_for_a_machine_of_another_business()
    {
        var controller = CreateController(new FakeLocalMachineStore());

        Assert.IsType<NotFoundResult>(await controller.Delete(999, CancellationToken.None));
    }

    /// <summary>
    /// The list and the single-machine read publish the deletion rule's own answer (issue #896),
    /// following the product pattern of issues #737 and #825: a plain <c>true</c> where the delete
    /// would be allowed, and the server's refusal where it would not. A record that did not resolve
    /// the question publishes neither, because absence is not permission.
    /// </summary>
    [Fact]
    public async Task The_list_and_the_read_publish_can_delete_and_the_refusal()
    {
        const string refusal = "This machine cannot be deleted because its history refers to it (1 sale).";
        var store = new FakeLocalMachineStore(
        [
            new LocalMachineRecord(1, "Added by mistake", IsActive: true)
            {
                DeletionEligibility = new LocalMachineDeletionEligibility(null),
            },
            new LocalMachineRecord(2, "Busy machine", IsActive: true)
            {
                DeletionEligibility = new LocalMachineDeletionEligibility(refusal),
            },
            new LocalMachineRecord(3, "Unresolved", IsActive: true),
        ]);
        var controller = CreateController(store);

        var ok = Assert.IsType<OkObjectResult>((await controller.GetAll(CancellationToken.None)).Result);
        var machines = Assert.IsAssignableFrom<IEnumerable<LocalMachineResponse>>(ok.Value)
            .ToDictionary(machine => machine.Name, StringComparer.Ordinal);

        Assert.Equal(true, machines["Added by mistake"].CanDelete);
        Assert.Null(machines["Added by mistake"].DeletionRefusal);
        Assert.Equal(false, machines["Busy machine"].CanDelete);
        Assert.Equal(refusal, machines["Busy machine"].DeletionRefusal);
        // Never resolved, so neither answer is published: absence is not permission.
        Assert.Null(machines["Unresolved"].CanDelete);
        Assert.Null(machines["Unresolved"].DeletionRefusal);

        var one = Assert.IsType<LocalMachineResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(2, CancellationToken.None)).Result).Value);
        Assert.Equal(false, one.CanDelete);
        Assert.Equal(refusal, one.DeletionRefusal);
    }
}
