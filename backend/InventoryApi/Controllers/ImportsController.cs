using Inventory.Application.Access;
using Inventory.Application.Imports;
using Inventory.Domain.Subscriptions;
using InventoryApi.Auth;
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
/// sees it and the requirement is declared here, on that one action. The reimbursement XML import
/// beside it carries no plan requirement: it is a settlement statement the business was sent, and
/// the fee and reimbursement figures it records are read by the Bookkeeping and GST reports a
/// Starter business includes. A refused import stores nothing - the plan is evaluated before the
/// multipart body is read at all - and nothing already imported is deleted or altered.</para>
/// </summary>
[ApiController]
[Route("api/imports")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.ImportsRun)]
public sealed class ImportsController : ControllerBase
{
    private readonly ImportNayaxSales _importNayaxSales;
    private readonly ImportPendingReimbursementXmlFiles _importPendingXmlFiles;

    public ImportsController(
        ImportNayaxSales importNayaxSales,
        ImportPendingReimbursementXmlFiles importPendingXmlFiles)
    {
        _importNayaxSales = importNayaxSales;
        _importPendingXmlFiles = importPendingXmlFiles;
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

    [HttpPost("pending-xml")]
    public async Task<ActionResult<ImportedFileImportResult>> ImportPendingXmlFiles(
        CancellationToken cancellationToken) =>
        Ok(await _importPendingXmlFiles.Handle(cancellationToken));
}
