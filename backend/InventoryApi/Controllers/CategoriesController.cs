using InventoryApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
public class CategoriesController : ControllerBase
{
    private readonly InventoryApi.Services.Interfaces.ICategoryService _service;
    public CategoriesController(InventoryApi.Services.Interfaces.ICategoryService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Category>>> GetAll() => Ok(await _service.GetAll());

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Category>> Get(int id)
    {
        var category = await _service.Get(id);
        return category is null ? NotFound() : Ok(category);
    }
}
