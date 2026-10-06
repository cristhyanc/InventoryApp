using Inventory.Application.Gst;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// The HTTP surface of the historical GST classification maintenance workflow (issue #433) over the
// Preview/Apply use cases. Each action binds its request, invokes one use case with the request's
// cancellation token, and returns the use case's own result unchanged: no classification rule,
// precedence, GST calculation, EF query, transaction, recomputation or per-call business filter
// lives here. Ownership is the central one - AppDbContext's query filters and its ownership stamp on
// save - and the business is resolved from the authenticated actor's membership inside the use case,
// so no route, body or header value can name an owner. This is an ordinary tenant-scoped endpoint
// family, not the cross-business platform diagnostics exception.
//
// Both actions are POST. Preview writes nothing at all, but it is not a GET: its response carries
// the fingerprint the apply is validated against, and a cached GET response would hand a caller a
// fingerprint for data it never read. POST keeps the preview uncacheable, which is the honest
// representation of a value that is only valid for the exact state it was computed from.
//
// Nothing is caught. A stale, foreign or tampered preview - any fingerprint that is not the one the
// apply recomputes from its own read - throws DomainValidationException, which
// DomainExceptionHandler (see Http/DomainExceptionHandler.cs) maps centrally to a 400
// ProblemDetails carrying the Application's caller-safe message, the same message in the `message`
// extension the Angular client reads, and a trace identifier. A stale preview is deliberately part
// of that 400 contract rather than a 409: the use case classifies it as a validation failure, and
// the HTTP boundary does not reclassify it. A caller with no resolvable business reaches
// BusinessScopeMiddleware's 403, and an unexpected framework or infrastructure failure is
// deliberately unmapped - it surfaces as GlobalExceptionHandler's logged, generic 500.
[ApiController]
[Route("api/admin/historical-gst-classification")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class HistoricalGstClassificationController : ControllerBase
{
    private readonly PreviewHistoricalGstClassification _preview;
    private readonly ApplyHistoricalGstClassification _apply;

    public HistoricalGstClassificationController(
        PreviewHistoricalGstClassification preview,
        ApplyHistoricalGstClassification apply)
    {
        _preview = preview;
        _apply = apply;
    }

    /// <summary>
    /// Projects the configured product GST rules and supplier GST defaults onto this business's
    /// unclassified purchase components and persists nothing, not even a draft. The returned
    /// <c>fingerprint</c> is what <see cref="Apply"/> requires back.
    /// </summary>
    [HttpPost("preview")]
    public async Task<ActionResult<HistoricalGstClassificationPreview>> Preview(
        CancellationToken cancellationToken) =>
        Ok(await _preview.Handle(cancellationToken));

    /// <summary>
    /// Writes exactly the previewed classifications, as one transaction. Accounting data only:
    /// purchase amounts, unit costs, inventory costing and stock movements are untouched, and a
    /// classification that already exists - manual or rule-based - is never overwritten.
    /// </summary>
    [HttpPost("apply")]
    public async Task<ActionResult<HistoricalGstClassificationApplied>> Apply(
        [FromBody] ApplyHistoricalGstClassificationRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _apply.Handle(request, cancellationToken));
}
