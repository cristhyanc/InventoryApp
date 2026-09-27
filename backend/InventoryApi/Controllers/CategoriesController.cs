using Inventory.Application.Categories;
using InventoryApi.DTOs;
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
    private readonly ListCategories _listCategories;
    private readonly GetCategory _getCategory;

    public CategoriesController(ListCategories listCategories, GetCategory getCategory)
    {
        _listCategories = listCategories;
        _getCategory = getCategory;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var categories = await _listCategories.Handle(cancellationToken);
        return Ok(categories.Select(ToResponse).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CategoryResponse>> Get(int id, CancellationToken cancellationToken)
    {
        var category = await _getCategory.Handle(id, cancellationToken);
        return category is null ? NotFound() : Ok(ToResponse(category));
    }

    private static CategoryResponse ToResponse(CategoryRecord record) =>
        new(record.Id, record.Name, record.Description);
}
