using Inventory.Application.Imports;
using Inventory.Application.SaleTimestampRepair;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// The HTTP surface of the Nayax sale timestamp repair maintenance workflow (issue #472) over the
// Preview/Apply use cases. Each action binds its request, invokes one use case with the request's
// cancellation token, and returns the use case's own result unchanged: no timestamp parsing,
// evidence rule, business-date conversion, EF query, transaction, costing replay or per-call
// business filter lives here. Ownership is the central one - AppDbContext's query filters and its
// ownership stamp on save - so no route, body, form field or header value can name an owner, and a
// stored preview belongs to the business that created it. This is an ordinary tenant-scoped endpoint
// family, not the cross-business platform diagnostics exception.
//
// Both actions are POST. Preview writes only its own plan draft, but it is deliberately not a GET:
// it reads the live Nayax window and may read an uploaded export, it returns an identifier that is
// only valid for the exact state it was computed from, and a cached GET response would hand a caller
// a plan for data it never read.
//
// Nothing is caught here. A refusal the use cases raise - no source named, an export without the
// AuthorizationDateTimeGMT column, an unknown, foreign, expired or already-applied preview, a plan
// the database no longer matches, or a costing replay that could not complete - is a
// DomainValidationException, which DomainExceptionHandler maps centrally to a 400 ProblemDetails
// carrying the caller-safe message. A caller with no resolvable business reaches
// BusinessScopeMiddleware's 403, a caller the repair cannot be attributed to raises
// BusinessAccessDeniedException, and an unexpected framework or infrastructure failure stays
// deliberately unmapped as GlobalExceptionHandler's logged, generic 500.
[ApiController]
[Route("api/admin/nayax-sale-timestamp-repair")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class NayaxSaleTimestampRepairsController : ControllerBase
{
    // The cap on the evidence export upload, deliberately tighter than the 10 MB general
    // document-upload limit the imports, purchases and expense-attachment endpoints carry. This
    // upload is not a business document: it is a Nayax transaction export read only for its
    // AuthorizationDateTimeGMT values and then discarded, and an export covering a repair's period
    // is orders of magnitude smaller than this. Eight million bytes also sits inside SonarCloud
    // S5693's fileUploadSizeLimit, which the 10 MB limit exceeded: the rule reads a limit that large
    // as an excessive content length rather than as a mitigation. Raising this cap is a human
    // decision, pinned by NayaxSaleTimestampRepairUploadLimitTests.
    private const long MaxEvidenceExportBytes = 8_000_000;

    private readonly PreviewNayaxSaleTimestampRepair _preview;
    private readonly ApplyNayaxSaleTimestampRepair _apply;

    public NayaxSaleTimestampRepairsController(
        PreviewNayaxSaleTimestampRepair preview,
        ApplyNayaxSaleTimestampRepair apply)
    {
        _preview = preview;
        _apply = apply;
    }

    /// <summary>
    /// Projects authoritative <c>AuthorizationDateTimeGMT</c> values from the named sources onto this
    /// business's stored sales and repairs nothing. The returned <c>previewId</c> is what
    /// <see cref="Apply"/> requires back.
    ///
    /// The request is multipart because the optional <paramref name="evidenceExport"/> is a file: an
    /// operator-supplied Nayax transaction export, read for its authorization instants only. It is
    /// never imported - no sale is created and no amount, status or product mapping is updated - and
    /// it is the only supported source for a transaction older than the live rolling window.
    /// </summary>
    [HttpPost("preview")]
    [Consumes("multipart/form-data")]
    // Both limits, because each is enforced somewhere else and only together are they effective.
    // RequestSizeLimit caps the raw body through the server's max-request-body-size feature, so it
    // depends on the host implementing that feature. RequestFormLimits caps what the multipart
    // reader itself will consume, in process, whatever the host is: without it this action would
    // read a multipart body up to FormOptions' 128 MB default, because this application configures
    // neither Kestrel's limits nor FormOptions globally.
    [RequestSizeLimit(MaxEvidenceExportBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxEvidenceExportBytes)]
    public async Task<ActionResult<NayaxSaleTimestampRepairPreview>> Preview(
        [FromForm] bool includeLatestSalesApiEvidence,
        [FromForm] DateTime? reconciliationCutoffUtc,
        [FromForm] DateTime? reconciliationFromBusinessDate,
        [FromForm] DateTime? reconciliationToBusinessDate,
        IFormFile? evidenceExport,
        CancellationToken cancellationToken)
    {
        // The reconciliation window is all three values or none: a daily or weekly total is only
        // comparable with an export's when both sides cover the same Sydney dates and exclude the
        // sales authorized after the same cutoff. A partial window would silently compare a
        // different period, so it is refused at the boundary rather than defaulted.
        var supplied = new[]
        {
            reconciliationCutoffUtc.HasValue,
            reconciliationFromBusinessDate.HasValue,
            reconciliationToBusinessDate.HasValue,
        };
        if (supplied.Distinct().Count() != 1)
            return BadRequest(
                "A reconciliation needs all three of reconciliationCutoffUtc, "
                    + "reconciliationFromBusinessDate and reconciliationToBusinessDate, or none of them.");

        var request = new NayaxSaleTimestampRepairPreviewRequest(
            includeLatestSalesApiEvidence,
            reconciliationCutoffUtc is { } cutoff
                ? new NayaxSaleTimestampReconciliationRequest(
                    DateTime.SpecifyKind(cutoff, DateTimeKind.Utc),
                    reconciliationFromBusinessDate!.Value.Date,
                    reconciliationToBusinessDate!.Value.Date)
                : null);

        // The IFormFile stays here, at the HTTP boundary: the use case receives only the uploaded
        // name and a way to open the bytes, and opens and disposes the stream itself.
        var export = evidenceExport is { Length: > 0 }
            ? new NayaxSalesFileInput(evidenceExport.FileName, evidenceExport.OpenReadStream)
            : null;
        return Ok(await _preview.Handle(request, export, cancellationToken));
    }

    /// <summary>
    /// Writes exactly the previewed repair, as one transaction: each named sale's authoritative
    /// instant, its audit row, and the affected products' costing replay. A sale's transaction id,
    /// amount, status and product mapping are untouched, no sale is created or removed, and a plan the
    /// database no longer matches - or one already applied or expired - is refused.
    /// </summary>
    [HttpPost("apply")]
    public async Task<ActionResult<NayaxSaleTimestampRepairApplied>> Apply(
        [FromBody] ApplyNayaxSaleTimestampRepairRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _apply.Handle(request, cancellationToken));
}
