using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.DTOs;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CategoriesController : ControllerBase
{
    private readonly AppDbContext _db;
    public CategoriesController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Category>>> GetAll() =>
        Ok(await _db.Categories.OrderBy(c => c.Name).ToListAsync());

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Category>> Get(int id)
    {
        var category = await _db.Categories.FindAsync(id);
        return category is null ? NotFound() : Ok(category);
    }

    [HttpPost]
    public async Task<ActionResult<Category>> Create(CategoryDto dto)
    {
        var category = new Category { Name = dto.Name, Description = dto.Description };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = category.Id }, category);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CategoryDto dto)
    {
        var category = await _db.Categories.FindAsync(id);
        if (category is null) return NotFound();
        category.Name = dto.Name;
        category.Description = dto.Description;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var category = await _db.Categories.FindAsync(id);
        if (category is null) return NotFound();
        _db.Categories.Remove(category);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
