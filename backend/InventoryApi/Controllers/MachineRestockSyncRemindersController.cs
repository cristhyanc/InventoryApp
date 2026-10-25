using Inventory.Application.Access;
using Inventory.Application.MachineStockSync;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Dashboard's machine restock-sync reminder (issue #891). A thin HTTP boundary: it binds no
/// input, invokes <see cref="GetMachineRestockSyncReminder"/> and returns what that built.
///
/// <para>It requires <c>Inventory.Operate</c> (issue #525), the same capability the Sync Restock
/// workflow on <c>MachinesController</c> requires, because this reminder is about exactly that work:
/// a role that may not reconcile restocks has nothing to do with a reminder to go and reconcile
/// them. It takes no business, machine or site identifier from the caller - the business is resolved
/// from the actor's membership and every read is tenant-scoped centrally.</para>
///
/// <para>It is a separate endpoint from <c>GET /api/dashboard/summary</c> on purpose. The summary's
/// refill and ordering cards need the live Nayax fleet; this read touches only local tables, needs a
/// different capability, and must be able to fail without taking the Dashboard's local sections with
/// it.</para>
///
/// <para>It is a <c>GET</c> and it is read-only in the strongest sense: it imports nothing,
/// acknowledges nothing, applies no restock event, resolves no duplicate and makes no Nayax
/// request. Reading it leaves every pending event exactly as pending as it was.</para>
/// </summary>
[ApiController]
[Route("api/machine-restock-sync")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.InventoryOperate)]
public sealed class MachineRestockSyncRemindersController : ControllerBase
{
    private readonly GetMachineRestockSyncReminder _getReminder;

    public MachineRestockSyncRemindersController(GetMachineRestockSyncReminder getReminder)
    {
        _getReminder = getReminder;
    }

    /// <summary>
    /// The machines whose explicit Sync Restock needs attention, or the withheld shape with the
    /// reason this business is shown no reminder at all.
    ///
    /// The declared type is the published union of the two shapes
    /// (<see cref="MachineRestockSyncReminderDto"/> and
    /// <see cref="HiddenMachineRestockSyncReminderDto"/>); the response is serialized from the one
    /// the use case returned, so a client branches on the stated <c>applicable</c> flag rather than
    /// on the absence of properties.
    /// </summary>
    [HttpGet("reminder")]
    public Task<IMachineRestockSyncReminderDto> Reminder(CancellationToken cancellationToken) =>
        _getReminder.Handle(cancellationToken);
}
