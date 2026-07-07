using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.DTOs;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SuppliersController : ControllerBase
{
    private readonly AppDbContext _db;
    public SuppliersController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Supplier>>> GetAll() =>
        Ok(await _db.Suppliers.OrderBy(s => s.Name).ToListAsync());

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Supplier>> Get(int id)
    {
        var supplier = await _db.Suppliers.FindAsync(id);
        return supplier is null ? NotFound() : Ok(supplier);
    }

    [HttpPost]
    public async Task<ActionResult<Supplier>> Create(SupplierDto dto)
    {
        var supplier = new Supplier
        {
            Name = dto.Name,
            ContactName = dto.ContactName,
            Phone = dto.Phone,
            Email = dto.Email,
            Address = dto.Address
        };
        _db.Suppliers.Add(supplier);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = supplier.Id }, supplier);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, SupplierDto dto)
    {
        var supplier = await _db.Suppliers.FindAsync(id);
        if (supplier is null) return NotFound();
        supplier.Name = dto.Name;
        supplier.ContactName = dto.ContactName;
        supplier.Phone = dto.Phone;
        supplier.Email = dto.Email;
        supplier.Address = dto.Address;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var supplier = await _db.Suppliers.FindAsync(id);
        if (supplier is null) return NotFound();
        _db.Suppliers.Remove(supplier);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
