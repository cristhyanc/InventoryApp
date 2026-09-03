using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MachinesController : ControllerBase
{
    private readonly IMachineService _service;

    public MachinesController(IMachineService service)
    {
        _service = service;
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<Machine>> GetById(long id)
    {
        var machine = await _service.GetById(id);
        return machine is null ? NotFound() : Ok(machine);
    }

    [HttpGet]
    public async Task<ActionResult<List<Machine>>> GetAll()
    {
        var machines = await _service.GetAll();
        return Ok(machines);
    }

    [HttpGet("{id:long}/products")]
    public async Task<ActionResult<List<Product>>> GetMachineProducts(long id)
    {
        var products = await _service.GetMachineProducts(id);
        return Ok(products);
    }

    [HttpPost("import-nayax-sales")]
    [RequestSizeLimit(10485760)]
    public async Task<ActionResult<object>> ImportNayaxSales([FromForm] IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest("An Excel file is required.");

        try
        {
            var result = await _service.ImportNayaxSalesFromExcelAsync(file, ct);
            return Ok(new { imported = result.Imported, updated = result.Updated, skipped = result.Skipped });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
