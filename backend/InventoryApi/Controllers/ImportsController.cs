using Inventory.Application.Imports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/imports")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class ImportsController : ControllerBase
{
    private readonly ImportNayaxProductCatalog _importProductCatalog;
    private readonly ImportNayaxSales _importNayaxSales;
    private readonly ImportPendingReimbursementXmlFiles _importPendingXmlFiles;

    public ImportsController(
        ImportNayaxProductCatalog importProductCatalog,
        ImportNayaxSales importNayaxSales,
        ImportPendingReimbursementXmlFiles importPendingXmlFiles)
    {
        _importProductCatalog = importProductCatalog;
        _importNayaxSales = importNayaxSales;
        _importPendingXmlFiles = importPendingXmlFiles;
    }

    [HttpPost("products")]
    public async Task<ActionResult<bool>> ImportProducts(CancellationToken cancellationToken)
    {
        await _importProductCatalog.Handle(cancellationToken);

        // This endpoint has always answered a constant `true`; a failed import surfaces as an
        // exception, never as `false`. The use case therefore reports no result of its own and the
        // response body is kept exactly as it was.
        return Ok(true);
    }

    [HttpPost("nayax-sales")]
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
