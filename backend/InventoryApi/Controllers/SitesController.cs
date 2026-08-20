using InventoryApi.DTOs;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SitesController : ControllerBase
{
    private readonly ISiteService _service;

    public SitesController(ISiteService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<SiteSummaryDto>>> GetAll()
    {
        return Ok(await _service.GetAll());
    }

    [HttpGet("{id:long}/products")]
    public async Task<ActionResult<List<SiteProductDto>>> GetProducts(long id)
    {
        return Ok(await _service.GetProducts(id));
    }
}
