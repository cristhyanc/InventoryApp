using Inventory.Application.Access;
using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Domain.Subscriptions;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The sales and reimbursement imports behind Admin → Imports.
///
/// Every action requires <c>Imports.Run</c> (issue #526). An import is what puts the raw financial
/// facts every report is then calculated from into the business's records - a duplicated or
/// mis-timed import is a reconciliation problem, not a display one - so #328's table gives running
/// one to a Manager and an Owner.
///
/// <para><strong>The product catalogue is no longer imported from here</strong> (issue #739, parent
/// #329 decision C2). <c>POST api/imports/products</c> wrote the provider's catalogue straight
/// through: it created a row for every entry it did not recognise and overwrote four fields on every
/// one it did, which is why it had to be refused outright for a business holding products of its own
/// (issue #736). <c>CatalogueReviewController</c> replaced it for every business - the same import,
/// previewed as a review and decided per entry - and carries the same <c>Imports.Run</c>
/// capability.</para>
///
/// <para><strong>The Nayax sales file import additionally requires
/// <see cref="PlanFeature.SalesFileImport"/></strong> (issue #967, decided on #965's review of 29
/// October 2026). It reads an uploaded file rather than the Nayax API, so the credential gate never
/// sees it and the requirement is declared here, on that one action. The reimbursement report upload
/// beside it carries no plan requirement: it is a settlement statement the business was sent, and
/// the fee and reimbursement figures it records are read by the Bookkeeping and GST reports a
/// Starter business includes. A refused import stores nothing - the plan is evaluated before the
/// multipart body is read at all - and nothing already imported is deleted or altered.</para>
///
/// <para><strong>A reimbursement report is uploaded here and nowhere else</strong> (issue #1237).
/// <c>POST api/imports/pending-xml</c> imported whatever XML somebody with server access had left in
/// a folder under the API's web root; it was the one import that did not take its input from the
/// signed-in business's own upload, and it is gone. The upload below is the single route.</para>
/// </summary>
[ApiController]
[Route("api/imports")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.ImportsRun)]
public sealed class ImportsController : ControllerBase
{
    private readonly ImportNayaxSales _importNayaxSales;
    private readonly UploadReimbursementReportArchive _uploadReimbursementReport;
    private readonly ListReimbursementImportJobs _listReimbursementImportJobs;
    private readonly GetReimbursementImportJob _getReimbursementImportJob;

    public ImportsController(
        ImportNayaxSales importNayaxSales,
        UploadReimbursementReportArchive uploadReimbursementReport,
        ListReimbursementImportJobs listReimbursementImportJobs,
        GetReimbursementImportJob getReimbursementImportJob)
    {
        _importNayaxSales = importNayaxSales;
        _uploadReimbursementReport = uploadReimbursementReport;
        _listReimbursementImportJobs = listReimbursementImportJobs;
        _getReimbursementImportJob = getReimbursementImportJob;
    }

    [HttpPost("nayax-sales")]
    [RequirePlan(PlanFeature.SalesFileImport)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10485760)]
    public async Task<ActionResult<NayaxSalesImportResult>> ImportNayaxSales(
    IFormFile file,
    CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("An Excel file is required.");

        try
        {
            // The IFormFile stays here, at the HTTP boundary: the use case receives only the
            // uploaded name and a way to open the bytes, and opens and disposes the stream itself.
            return Ok(await _importNayaxSales.Handle(
                new NayaxSalesFileInput(file.FileName, file.OpenReadStream), cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            // Unchanged from before the import's migration, including the unsupported-format
            // message the use case throws. Converting this action to the centralized domain-error
            // mapping would change its response body from a bare string to ProblemDetails and is
            // still its own later change (docs/architecture.md § Domain and application error
            // mapping).
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Accepts the ZIP Nayax emails, exactly as it arrives, and queues it for import (issue #1149).
    ///
    /// <para><strong>It imports nothing and waits for nothing.</strong> The archive is checked
    /// against <c>Inventory.Domain.Imports.ReimbursementArchivePolicy</c> here and now - so a bad
    /// archive is a <c>400</c> the operator sees in seconds - and, if it holds at least one
    /// reimbursement XML and breaks no limit, stored as a queued job. The reply is <c>202</c> with
    /// the job id, and the background worker does the importing.</para>
    ///
    /// <para><strong>The business is the caller's own resolved business.</strong> Nothing in the
    /// request can choose one: there is no business id in the route, the form or a header. A role
    /// preview and a read-only support session are refused centrally because this is a <c>POST</c>,
    /// exactly as the two import writes above are - there is nothing endpoint-specific to get
    /// wrong.</para>
    ///
    /// <para>No plan requirement, unlike the Nayax sales file import above: a settlement statement
    /// the business was sent feeds the Bookkeeping and GST reports a Starter business includes.</para>
    /// </summary>
    [HttpPost("reimbursement-report")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ReimbursementArchivePolicy.MaxUploadBytes)]
    public async Task<ActionResult<ReimbursementReportUploadAccepted>> UploadReimbursementReport(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ReimbursementArchivePolicy.EmptyUploadMessage);

        if (file.Length > ReimbursementArchivePolicy.MaxUploadBytes)
            return BadRequest(ReimbursementArchivePolicy.TooLargeMessage);

        // The IFormFile stays here, at the HTTP boundary. The bytes are read into memory rather
        // than to a path: nothing about this import touches the filesystem, and the request size
        // limit above - plus the length check beside it, for a client that lied about it - is what
        // bounds how much is read.
        var archive = await ReadAsync(file, cancellationToken);

        var result = await _uploadReimbursementReport.Handle(
            file.FileName, archive, cancellationToken);

        if (!result.IsAccepted) return BadRequest(result.Rejection);

        return Accepted(new ReimbursementReportUploadAccepted(result.JobId!.Value));
    }

    /// <summary>
    /// The caller's own business's latest reimbursement report import jobs (issue #1149), newest
    /// first. Readable by the same members who may run the import, and - because it is a
    /// <c>GET</c> - by a read-only support session, by that feature's own central method rule.
    /// </summary>
    [HttpGet("reimbursement-report/jobs")]
    public async Task<ActionResult<IReadOnlyList<ReimbursementImportJobSummary>>>
        ReimbursementReportJobs(CancellationToken cancellationToken) =>
        Ok(await _listReimbursementImportJobs.Handle(cancellationToken));

    /// <summary>
    /// One of the caller's own business's reimbursement report import jobs, with what each file in
    /// it turned into (issue #1149).
    ///
    /// <para>A job of another business is <c>404</c>, and not because of a comparison made here: the
    /// lookup is tenant-scoped centrally, so another business's job is not found at all.</para>
    /// </summary>
    [HttpGet("reimbursement-report/jobs/{jobId:int}")]
    public async Task<ActionResult<ReimbursementImportJobDetail>> ReimbursementReportJob(
        int jobId,
        CancellationToken cancellationToken)
    {
        var job = await _getReimbursementImportJob.Handle(jobId, cancellationToken);

        return job is null ? NotFound() : Ok(job);
    }

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await using var stream = file.OpenReadStream();

        await stream.CopyToAsync(buffer, cancellationToken);

        return buffer.ToArray();
    }
}
