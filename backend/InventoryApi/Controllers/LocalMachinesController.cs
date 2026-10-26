using Inventory.Application.Access;
using Inventory.Application.Machines;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The machines this business operates, as records it owns (issue #740, parent #329 child G1):
/// create one, rename it, take it out of service, put it back. A machine the business's sales
/// provider names cannot be renamed or taken out of service here; it can be put back.
///
/// <para><strong>It works with no sales provider at all</strong>, which is the point of the issue. It
/// is therefore a different resource from <c>/api/machines</c>, which is the live Nayax fleet with
/// this business's sales and costs resolved onto it and needs working credentials for every one of
/// its reads. Since issue #741 (G2) the stored stock and count machine references point at these
/// rows too, and <c>/api/machines/{id}</c> takes their ids. The provider machine review of issue
/// #742 (G3) is deliberately <em>not</em> in this surface: it is
/// <c>/api/machine-link-review</c>, because it reads the live fleet and this resource reads no
/// provider at all.</para>
///
/// <para>Every action requires <c>Inventory.Operate</c> (issue #525), the same capability the fleet
/// controller and the rest of day-to-day stock keeping declare: recording the machines you refill is
/// that work. Which also means a read-only support session is refused every write here by the one
/// central rule that allows it only <c>GET</c> and <c>HEAD</c> (issue #551) - there is no per-action
/// check for it in this file.</para>
///
/// <para><strong>A machine's provider id is not writable and not published as an identifier.</strong>
/// No request field corresponds to it, and the response carries only whether the business's provider
/// names the machine.</para>
///
/// <para><strong>A machine may be deleted only while nothing refers to it</strong> (issue #896). A
/// machine added by mistake is removed; a machine with machine stock counts, stock movements,
/// pick-list refills or operating expenses, and a machine the provider names, are refused with the
/// reason - a machine that stops trading is deactivated and keeps all of its history. The list and
/// the single-machine read publish that same answer as <c>canDelete</c>/<c>deletionRefusal</c>, so
/// the page offers Delete only where this resource allows it.</para>
/// </summary>
[ApiController]
[Route("api/local-machines")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.InventoryOperate)]
public class LocalMachinesController : ControllerBase
{
    private readonly ListLocalMachines _listLocalMachines;
    private readonly GetLocalMachine _getLocalMachine;
    private readonly CreateLocalMachine _createLocalMachine;
    private readonly RenameLocalMachine _renameLocalMachine;
    private readonly SetLocalMachineActivation _setLocalMachineActivation;
    private readonly DeleteLocalMachine _deleteLocalMachine;

    public LocalMachinesController(
        ListLocalMachines listLocalMachines,
        GetLocalMachine getLocalMachine,
        CreateLocalMachine createLocalMachine,
        RenameLocalMachine renameLocalMachine,
        SetLocalMachineActivation setLocalMachineActivation,
        DeleteLocalMachine deleteLocalMachine)
    {
        _listLocalMachines = listLocalMachines;
        _getLocalMachine = getLocalMachine;
        _createLocalMachine = createLocalMachine;
        _renameLocalMachine = renameLocalMachine;
        _setLocalMachineActivation = setLocalMachineActivation;
        _deleteLocalMachine = deleteLocalMachine;
    }

    /// <summary>
    /// Every machine this business has recorded, active and inactive alike, ordered by name. A
    /// deactivated machine stays listed so it can be reactivated.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<LocalMachineResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var machines = await _listLocalMachines.Handle(cancellationToken);
        return Ok(machines.Select(ToResponse).ToList());
    }

    [HttpGet("{id:long}")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LocalMachineResponse>> Get(long id, CancellationToken cancellationToken)
    {
        var machine = await _getLocalMachine.Handle(id, cancellationToken);
        return machine is null ? NotFound() : Ok(ToResponse(machine));
    }

    /// <summary>Records a machine this business operates.</summary>
    /// <response code="201">The created machine, with its database-assigned id.</response>
    /// <response code="400">The refusal message: the machine has no name.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LocalMachineResponse>> Create(
        LocalMachineDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var attempt = await _createLocalMachine.Handle(
            new LocalMachineWriteFields(dto.Name), cancellationToken);
        if (attempt.Outcome != LocalMachineWriteOutcome.Succeeded) return BadRequest(attempt.Refusal);

        var created = await _getLocalMachine.Handle(attempt.MachineId!.Value, cancellationToken);

        // Only reachable if the row vanished in between, which nothing in the application does:
        // there is then nothing left to describe, which is what 404 says.
        return created is null
            ? NotFound()
            : CreatedAtAction(nameof(Get), new { id = created.Id }, ToResponse(created));
    }

    /// <summary>
    /// Renames a machine. A machine the business's sales provider names is managed there and is
    /// refused.
    /// </summary>
    /// <response code="204">The machine was renamed.</response>
    /// <response code="400">The refusal message: the submitted name is blank, or the provider names
    /// the machine.</response>
    /// <response code="404">No such machine in this business.</response>
    [HttpPut("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rename(
        long id, LocalMachineDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var attempt = await _renameLocalMachine.Handle(
            id, new LocalMachineWriteFields(dto.Name), cancellationToken);
        return Result(attempt);
    }

    /// <summary>
    /// Takes the machine out of service. It stays listed and keeps every stored sale, refill, count
    /// and expense that refers to it - which is what a machine with history gets instead of a
    /// delete. A machine the business's sales provider names is taken out of service there and is
    /// refused.
    /// </summary>
    /// <response code="204">The machine is inactive.</response>
    /// <response code="400">The refusal message: the provider names the machine.</response>
    /// <response code="404">No such machine in this business.</response>
    [HttpPost("{id:long}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(long id, CancellationToken cancellationToken) =>
        Result(await _setLocalMachineActivation.Handle(id, isActive: false, cancellationToken));

    /// <summary>Puts a deactivated machine back in service.</summary>
    /// <response code="204">The machine is active.</response>
    /// <response code="404">No such machine in this business.</response>
    [HttpPost("{id:long}/reactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reactivate(long id, CancellationToken cancellationToken) =>
        Result(await _setLocalMachineActivation.Handle(id, isActive: true, cancellationToken));

    /// <summary>
    /// Deletes a machine the business added by mistake, when it may be deleted at all (issue #896).
    ///
    /// <para><strong>Only a machine nothing refers to.</strong> A machine with machine stock counts,
    /// stock movements, pick-list refills or operating expenses is refused with the count of what is
    /// in the way, and a machine the business's sales provider names is refused for its own reason:
    /// it is managed at the provider. In both cases nothing is written, and <c>deactivate</c> above
    /// is how a machine that stops trading is retired. Nothing is cascaded, nulled or reassigned, and
    /// no stock movement is recorded (<c>Inventory.Domain.Machines.MachineDeletionPolicy</c>).</para>
    ///
    /// <para>The reference count and the delete are one operation inside the store's write
    /// transaction, so a machine that gains a reference or a provider link after a caller read it is
    /// refused rather than deleted - which is also why a client has to show this refusal even when
    /// the response it is working from said the machine was deletable.</para>
    /// </summary>
    /// <response code="204">The machine was deleted.</response>
    /// <response code="400">
    /// The refusal message: what refers to the machine, or which provider manages it.
    /// </response>
    /// <response code="404">No such machine in this business.</response>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken) =>
        Result(await _deleteLocalMachine.Handle(id, cancellationToken));

    private IActionResult Result(LocalMachineWriteAttempt attempt) =>
        attempt.Outcome switch
        {
            LocalMachineWriteOutcome.Succeeded => NoContent(),
            LocalMachineWriteOutcome.NotFound => NotFound(),
            _ => BadRequest(attempt.Refusal),
        };

    /// <summary>
    /// The wire shape of one machine. <c>canDelete</c>/<c>deletionRefusal</c> come straight from the
    /// record's own eligibility (issue #896), which the store decided with the same rule the delete
    /// applies - the controller adds no second answer of its own, and a record that did not resolve
    /// the question publishes neither.
    /// </summary>
    private static LocalMachineResponse ToResponse(LocalMachineRecord record) =>
        new(
            record.Id,
            record.Name,
            record.IsActive,
            record.IsProviderNamed,
            record.DeletionEligibility?.Allowed,
            record.DeletionEligibility?.Refusal);
}
