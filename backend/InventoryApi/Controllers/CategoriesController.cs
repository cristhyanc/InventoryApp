using Inventory.Application.Access;
using Inventory.Application.Categories;
using Inventory.Domain.Providers;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The product categories. Part of day-to-day inventory work, so every action requires
/// <c>Inventory.Operate</c> (issue #525): a category is how the catalogue is navigated.
///
/// <para><strong>Read-only until issue #736</strong> (parent #329 decision P2), because categories
/// only ever arrived through the Nayax catalogue import. A business without a sales provider now
/// creates and maintains its own: <c>POST</c>, <c>PUT</c> and <c>DELETE</c> sit behind that same
/// capability, which also means a read-only support session is refused them by the central rule
/// allowing it only <c>GET</c> and <c>HEAD</c> (issue #551).</para>
///
/// <para>Two refusals are the point of the write surface. A category the business's sales provider
/// named keeps its name - the next import would overwrite a local rename - and a category products
/// still use is not deleted, because a product whose category disappears is left uncategorised. Both
/// come from the Domain policies the use cases apply, never from a rule written here.</para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.InventoryOperate)]
public class CategoriesController : ControllerBase
{
    private readonly ListCategories _listCategories;
    private readonly GetCategory _getCategory;
    private readonly CreateCategory _createCategory;
    private readonly UpdateCategory _updateCategory;
    private readonly DeleteCategory _deleteCategory;

    public CategoriesController(
        ListCategories listCategories,
        GetCategory getCategory,
        CreateCategory createCategory,
        UpdateCategory updateCategory,
        DeleteCategory deleteCategory)
    {
        _listCategories = listCategories;
        _getCategory = getCategory;
        _createCategory = createCategory;
        _updateCategory = updateCategory;
        _deleteCategory = deleteCategory;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var categories = await _listCategories.Handle(cancellationToken);
        return Ok(categories.Select(ToResponse).ToList());
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<CategoryResponse>> Get(long id, CancellationToken cancellationToken)
    {
        var category = await _getCategory.Handle(id, cancellationToken);
        return category is null ? NotFound() : Ok(ToResponse(category));
    }

    /// <summary>Creates a category this business maintains itself (issue #736).</summary>
    /// <response code="201">The created category, with its database-assigned id.</response>
    /// <response code="400">
    /// The refusal message: no name, or a name this business already uses for another category.
    /// </response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CategoryResponse>> Create(CategoryDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var attempt = await _createCategory.Handle(
            new CategoryWriteFields(dto.Name, dto.Description), cancellationToken);
        if (attempt.Outcome != CategoryWriteOutcome.Succeeded) return BadRequest(attempt.Refusal);

        var created = await _getCategory.Handle(attempt.CategoryId!.Value, cancellationToken);

        // Only reachable if another request deleted the category in between: there is nothing left
        // to describe, which is what 404 says.
        return created is null
            ? NotFound()
            : CreatedAtAction(nameof(Get), new { id = created.Id }, ToResponse(created));
    }

    /// <summary>
    /// Renames a category or changes its description (issue #736). For a category the business's
    /// sales provider named, a <em>change</em> to the name is refused with the provider's name in the
    /// message; sending the stored name back is not a rename, so the description stays editable.
    /// </summary>
    /// <response code="204">The category was updated.</response>
    /// <response code="400">
    /// The refusal message: no name, a name another category of this business uses, or a name the
    /// sales provider manages.
    /// </response>
    /// <response code="404">No such category in this business.</response>
    [HttpPut("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(long id, CategoryDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var attempt = await _updateCategory.Handle(
            id, new CategoryWriteFields(dto.Name, dto.Description), cancellationToken);
        return attempt.Outcome switch
        {
            CategoryWriteOutcome.Succeeded => NoContent(),
            CategoryWriteOutcome.NotFound => NotFound(),
            _ => BadRequest(attempt.Refusal),
        };
    }

    /// <summary>
    /// Deletes a category no product uses (issue #736). A category products still use is refused with
    /// how many use it, because deleting it would quietly uncategorise them.
    /// </summary>
    /// <response code="204">The category was deleted.</response>
    /// <response code="404">No such category in this business.</response>
    /// <response code="409">The refusal message, including how many products use the category.</response>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var attempt = await _deleteCategory.Handle(id, cancellationToken);
        return attempt.Outcome switch
        {
            CategoryWriteOutcome.Succeeded => NoContent(),
            CategoryWriteOutcome.NotFound => NotFound(),
            _ => Conflict(attempt.Refusal),
        };
    }

    /// <summary>
    /// The category as this resource publishes it, including which of its fields the business's sales
    /// provider manages (issue #736) - the same Domain statement the update refuses a rename with, so
    /// a client cannot lock a different set from the one the server enforces.
    /// </summary>
    private static CategoryResponse ToResponse(CategoryRecord record) =>
        new(
            record.Id,
            record.Name,
            record.Description,
            ProviderManagedCatalogueFields.ForCategory(record.IsProviderManaged));
}
