using InventoryApi.DTOs;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
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
