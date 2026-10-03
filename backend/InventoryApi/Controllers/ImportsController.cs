using Inventory.Application.Imports;
using InventoryApi.Services.Interfaces;
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
    private readonly IImportService _service;
    private readonly ImportPendingReimbursementXmlFiles _importPendingXmlFiles;

    public ImportsController(
        IImportService service,
        ImportPendingReimbursementXmlFiles importPendingXmlFiles)
    {
        _service = service;
        _importPendingXmlFiles = importPendingXmlFiles;
    }

    [HttpPost("products")]
    public async Task<ActionResult<bool>> ImportProducts() =>
        Ok(await _service.ImportProductsAsync());

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
            return Ok(await _service.ImportNayaxSalesFromExcelAsync(file, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("pending-xml")]
    public async Task<ActionResult<ImportedFileImportResult>> ImportPendingXmlFiles(
        CancellationToken cancellationToken) =>
        Ok(await _importPendingXmlFiles.Handle(cancellationToken));
}
