using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.DTOs;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/products/{productId:int}/stock")]
public class StockController : ControllerBase
{
    private readonly AppDbContext _db;
    public StockController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StockAdjustment>>> History(int productId)
    {
        var exists = await _db.Products.AnyAsync(p => p.Id == productId);
        if (!exists) return NotFound("Product not found");

        var history = await _db.StockAdjustments
            .Where(sa => sa.ProductId == productId)
            .OrderByDescending(sa => sa.CreatedAt)
            .ToListAsync();
        return Ok(history);
    }

    [HttpPost]
    public async Task<ActionResult<StockAdjustment>> Adjust(int productId, StockAdjustmentDto dto)
    {
        var product = await _db.Products.FindAsync(productId);
        if (product is null) return NotFound("Product not found");

        var newQuantity = product.QuantityInStock + dto.QuantityChange;
        if (newQuantity < 0)
            return BadRequest("Resulting stock quantity cannot be negative.");

        product.QuantityInStock = newQuantity;
        product.UpdatedAt = DateTime.UtcNow;

        var adjustment = new StockAdjustment
        {
            ProductId = productId,
            QuantityChange = dto.QuantityChange,
            QuantityAfter = newQuantity,
            Reason = dto.Reason,
            Notes = dto.Notes
        };
        _db.StockAdjustments.Add(adjustment);
        await _db.SaveChangesAsync();

        return Ok(adjustment);
    }
}
