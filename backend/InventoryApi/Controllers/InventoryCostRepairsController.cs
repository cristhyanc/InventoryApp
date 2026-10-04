using Inventory.Application.Costing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// The HTTP surface of the costing repair (issue #360) over the issue #359 use cases. Each action
// binds its request, invokes one use case with the request's cancellation token, and returns the
// use case's own result unchanged: no costing rule, EF query, transaction, recomputation or
// per-call business filter lives here. Ownership is the central one - AppDbContext's query filters
// and its ownership stamp on save - so another business's product is simply not there, and the use
// case reports it exactly as it reports one that does not exist.
//
// Nothing is caught. The deliberate repair checks (quantity, unit cost, reason, the transition
// cutoff, placement before the sale being covered, a stale preview whose ledger fingerprint no
// longer matches, and a repair that would leave the history fatally incomplete) all throw
// DomainValidationException, which DomainExceptionHandler (see Http/DomainExceptionHandler.cs)
// maps centrally to a 400 ProblemDetails carrying the Application's caller-safe message, the same
// message in the `message` extension the Angular client reads, and a trace identifier. A stale
// preview is deliberately part of that 400 contract rather than a 409: the use case classifies it
// as a validation failure, and the HTTP boundary does not reclassify it. An unidentifiable caller
// reaches BusinessScopeMiddleware's 403, and an unexpected framework or infrastructure failure is
// deliberately unmapped - it surfaces as GlobalExceptionHandler's logged, generic 500.
//
// Timestamps: the effective instant is a UTC instant. CostingRepairPolicy.NormalizeEffectiveAt
// converts an offset-bearing value and reads a timezone-less one as UTC (issue #359's existing
// policy, unchanged here), so every response reports the UTC instant the request named.
[ApiController]
[Route("api/admin/inventory-cost-repair")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class InventoryCostRepairsController : ControllerBase
{
    private readonly PreviewInventoryCostRepair _preview;
    private readonly ApplyInventoryCostRepair _apply;
    private readonly GetInventoryCostRepairHistory _history;

    public InventoryCostRepairsController(
        PreviewInventoryCostRepair preview,
        ApplyInventoryCostRepair apply,
        GetInventoryCostRepairHistory history)
    {
        _preview = preview;
        _apply = apply;
        _history = history;
    }

    /// <summary>
    /// Projects a proposed costing repair for one product and persists nothing, not even a draft.
    /// The returned <c>LedgerFingerprint</c> is what <see cref="Apply"/> requires back.
    /// </summary>
    [HttpPost("preview")]
    public async Task<ActionResult<InventoryCostRepairPreview>> Preview(
        [FromBody] InventoryCostRepairRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _preview.Handle(request, cancellationToken));

    /// <summary>
    /// Appends the previewed repair and recosts the product's later completed sales from it.
    /// Costing-only: physical stock, stock adjustments, machine refills and the transition
    /// baseline are untouched.
    /// </summary>
    [HttpPost("apply")]
    public async Task<ActionResult<InventoryCostRepairApplied>> Apply(
        [FromBody] ApplyInventoryCostRepairRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _apply.Handle(request, cancellationToken));

    /// <summary>One product's costing repairs, newest effective first.</summary>
    [HttpGet("{productId:long}")]
    public async Task<ActionResult<IReadOnlyList<InventoryCostRepairRecord>>> History(
        long productId,
        CancellationToken cancellationToken) =>
        Ok(await _history.Handle(productId, cancellationToken));
}
