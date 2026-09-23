using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
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

}
