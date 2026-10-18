using Inventory.Application.Access;
using Inventory.Application.Machines;
using Inventory.Application.MachineStock;
using Inventory.Application.MachineStockSync;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The machine fleet, its per-machine product lists and the Sync Restock reconciliation workflow.
/// All of it is day-to-day inventory work, so every action requires <c>Inventory.Operate</c>
/// (issue #525) - Sync Restock included, because reconciling what a machine reported it dispensed
/// is exactly the refill work an Operator does.
///
/// <para><b>The four read actions withhold their financial figures from a caller without
/// <c>Dashboard.ViewFinancials</c></b> (issue #698, extended to the latest-sales read by issue
/// #768). The endpoints themselves stay open to every role
/// that may operate inventory, because that is what they are for; what changes is the shape of the
/// response, which an <c>AuthorizeAttribute</c> cannot express. The decision is taken once per
/// request from <see cref="CurrentCapabilities"/> - the one service that answers "what may this
/// request do", so it cannot disagree with the capability the Dashboard summary reads, the policies
/// the authorization middleware enforces or the list <c>GET /api/me/access</c> reports - and is
/// handed to the response mappers, which own the projections. No role name appears here.</para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.InventoryOperate)]
public class MachinesController : ControllerBase
{
    private readonly GetMachineDashboard _getMachineDashboard;
    private readonly ListMachineDashboard _listMachineDashboard;
    private readonly ListMachineProducts _listMachineProducts;
    private readonly GetMachineLatestSales _getMachineLatestSales;
    private readonly SyncMachineStockFromNayax _syncMachineStock;
    private readonly ApplyMachineStockSync _applyMachineStockSync;
    private readonly ResolveMachineStockDuplicate _resolveMachineStockDuplicate;
    private readonly ResolveMachineStockEventsAsAlreadyRecorded _resolveMachineStockEventsAsAlreadyRecorded;
    private readonly GetMachineStockReconciliation _getMachineStockReconciliation;
    private readonly RecordMachineStockCount _recordMachineStockCount;
    private readonly CurrentCapabilities _capabilities;

    public MachinesController(
        GetMachineDashboard getMachineDashboard,
        ListMachineDashboard listMachineDashboard,
        ListMachineProducts listMachineProducts,
        GetMachineLatestSales getMachineLatestSales,
        SyncMachineStockFromNayax syncMachineStock,
        ApplyMachineStockSync applyMachineStockSync,
        ResolveMachineStockDuplicate resolveMachineStockDuplicate,
        ResolveMachineStockEventsAsAlreadyRecorded resolveMachineStockEventsAsAlreadyRecorded,
        GetMachineStockReconciliation getMachineStockReconciliation,
        RecordMachineStockCount recordMachineStockCount,
        CurrentCapabilities capabilities)
    {
        _getMachineDashboard = getMachineDashboard;
        _listMachineDashboard = listMachineDashboard;
        _listMachineProducts = listMachineProducts;
        _getMachineLatestSales = getMachineLatestSales;
        _syncMachineStock = syncMachineStock;
        _applyMachineStockSync = applyMachineStockSync;
        _resolveMachineStockDuplicate = resolveMachineStockDuplicate;
        _resolveMachineStockEventsAsAlreadyRecorded = resolveMachineStockEventsAsAlreadyRecorded;
        _getMachineStockReconciliation = getMachineStockReconciliation;
        _recordMachineStockCount = recordMachineStockCount;
        _capabilities = capabilities;
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<MachineResponse>> GetById(long id)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);
        var machine = await _getMachineDashboard.Handle(id, CancellationToken.None);
        return machine is null ? NotFound() : Ok(MachineResponseMapper.ToResponse(machine, visibility));
    }

    [HttpGet]
    public async Task<ActionResult<List<MachineResponse>>> GetAll()
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);
        var machines = await _listMachineDashboard.Handle(CancellationToken.None);
        return Ok(machines.Select(machine => MachineResponseMapper.ToResponse(machine, visibility)).ToList());
    }

    [HttpGet("{id:long}/products")]
    public async Task<ActionResult<List<ProductResponse>>> GetMachineProducts(long id)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);
        var products = await _listMachineProducts.Handle(id, CancellationToken.None);
        return Ok(products.Select(product => ProductRecordResponseMapper.ToResponse(product, visibility)).ToList());
    }

    // The machine's latest Nayax sales (issue #768): the Sales tab of the machine-details page.
    //
    // A pure read of the provider's own rolling latest-sales window - it imports nothing, persists
    // nothing, deducts no storage, recosts no inventory and applies no stock event, which the use
    // case guarantees structurally by holding no port but the Nayax client. An unconfigured or
    // unusable provider is answered 200 with isAvailable: false and a stable reason code, because
    // that is a state of this business and not a failure of the call; a transport failure, a
    // cancellation and the support-session refusal keep their own status codes.
    [HttpGet("{id:long}/latest-sales")]
    public async Task<ActionResult<MachineLatestSalesResponse>> GetLatestSales(long id, CancellationToken ct)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(ct);
        var latest = await _getMachineLatestSales.Handle(id, ct);
        return Ok(MachineLatestSalesResponseMapper.ToResponse(id, latest, visibility));
    }

    // Fetches new Nayax stock-adjustment alerts and returns a reconciliation preview; it never
    // changes storage inventory itself (issue #183). fromDate/includeReconciled (issue #206) are
    // presentation/workflow filters over the same imported facts: fromDate bounds the canonical
    // EventDateTimeGMT, includeReconciled shows or hides events already reconciled as "already
    // recorded manually". Omitting either preserves the original unfiltered behaviour. fromDate is
    // bound as DateTimeOffset, not DateTime (issue #218): the frontend always sends a calendar
    // boundary in the current business's own configured IANA time zone - the one
    // GET /api/business/current reports (issue #499), not a fixed Australian one - already converted
    // to a UTC instant, and ASP.NET Core's
    // default DateTime query binding reinterprets a UTC instant against the server process's local
    // time zone - DateTimeOffset carries its own offset, so UtcDateTime is the exact instant chosen
    // regardless of the server's local time zone.
    [HttpPost("{id:long}/sync-restock")]
    public async Task<ActionResult<NayaxMachineStockSyncPreviewDto>> SyncRestock(
        long id, CancellationToken ct, [FromQuery] DateTimeOffset? fromDate = null, [FromQuery] bool includeReconciled = false)
    {
        var preview = await _syncMachineStock.Handle(id, ct, fromDate?.UtcDateTime, includeReconciled);
        return Ok(preview);
    }

    // Applies only the explicitly accepted events from the preview. Unresolved/needs-review/
    // insufficient-stock events are left untouched even when included in the request. A flagged
    // possible duplicate is never applied here: it must go through resolve-duplicate first
    // (issue #196).
    [HttpPost("{id:long}/sync-restock/apply")]
    public async Task<ActionResult<NayaxMachineStockApplyResponseDto>> ApplySyncRestock(
        long id, NayaxStockEventApplyRequestDto dto, CancellationToken ct)
    {
        var result = await _applyMachineStockSync.Handle(id, dto.EventIds, ct);
        return Ok(result);
    }

    // The explicit resolution for a Nayax event flagged as a possible duplicate of a manual refill
    // (issue #196): "already recorded manually" reconciles it without any movement; "apply as
    // separate restock" is an explicit, auditable override that applies it exactly once.
    [HttpPost("{id:long}/sync-restock/resolve-duplicate")]
    public async Task<ActionResult<NayaxStockEventApplyResultDto>> ResolveSyncRestockDuplicate(
        long id, NayaxResolveDuplicateRequestDto dto, CancellationToken ct)
    {
        var result = await _resolveMachineStockDuplicate.Handle(id, dto.EventId, dto.Resolution, ct);
        return Ok(result);
    }

    // The bulk "already recorded manually" action (issue #242): reconciles every selected event in
    // one explicit operator action, whether or not the app flagged it as a possible duplicate - that
    // flag is a suggestion, not a precondition. Each event keeps its own idempotent, individually
    // reported outcome, following the same per-event batch shape as ApplySyncRestock.
    [HttpPost("{id:long}/sync-restock/resolve-manual")]
    public async Task<ActionResult<NayaxMachineStockApplyResponseDto>> ResolveSyncRestockManually(
        long id, NayaxResolveManyAsAlreadyRecordedRequestDto dto, CancellationToken ct)
    {
        var result = await _resolveMachineStockEventsAsAlreadyRecorded.Handle(id, dto.EventIds, ct);
        return Ok(result);
    }

    // The machine-stock ledger (issue #56): per product, what this machine is expected to be
    // holding, what it reports it is holding, and the discrepancy/shrinkage between them. A pure
    // read - it creates no movement, applies no correction and writes nothing.
    [HttpGet("{id:long}/stock-ledger")]
    public async Task<ActionResult<MachineStockReconciliationDto>> GetStockLedger(long id, CancellationToken ct)
    {
        var reconciliation = await _getMachineStockReconciliation.Handle(id, ct);
        return Ok(reconciliation);
    }

    // Records an operator's physical count of one product in this machine (issue #56): the baseline
    // the ledger counts forward from. It appends an observation only - no storage quantity, costing
    // quantity, inventory value, stock movement or sale cost changes - and the difference it absorbs
    // is derived by the ledger rather than written away as a correction.
    [HttpPost("{id:long}/stock-ledger/counts")]
    public async Task<ActionResult<MachineStockCountResultDto>> RecordStockCount(
        long id, MachineStockCountRequestDto dto, CancellationToken ct)
    {
        var count = await _recordMachineStockCount.Handle(id, dto, ct);
        return count is null ? NotFound() : Ok(count);
    }
}
