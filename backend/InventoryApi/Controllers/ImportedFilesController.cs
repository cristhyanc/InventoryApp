using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImportedFilesController : ControllerBase
{
    private readonly IImportedFileService _service;

    public ImportedFilesController(IImportedFileService service)
    {
        _service = service;
    }

    [HttpPost("import")]
    public async Task<ActionResult<ImportedFileImportResult>> Import()
    {
        return Ok(await _service.ImportPendingFiles());
    }
}
