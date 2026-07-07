using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InventoryApi.Data;
using InventoryApi.Models;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReceiptsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".pdf", ".webp", ".heic" };
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    public ReceiptsController(AppDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    private string ReceiptsFolder
    {
        get
        {
            var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var folder = Path.Combine(webRoot, "receipts");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Receipt>>> GetAll([FromQuery] int? supplierId)
    {
        var query = _db.Receipts.Include(r => r.Supplier).AsQueryable();
        if (supplierId.HasValue) query = query.Where(r => r.SupplierId == supplierId);
        var receipts = await query.OrderByDescending(r => r.PurchaseDate).ToListAsync();
        return Ok(receipts);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Receipt>> Get(int id)
    {
        var receipt = await _db.Receipts.Include(r => r.Supplier).FirstOrDefaultAsync(r => r.Id == id);
        return receipt is null ? NotFound() : Ok(receipt);
    }

    [HttpGet("{id:int}/file")]
    public async Task<IActionResult> GetFile(int id)
    {
        var receipt = await _db.Receipts.FindAsync(id);
        if (receipt is null) return NotFound();

        var path = Path.Combine(ReceiptsFolder, receipt.StoredFileName);
        if (!System.IO.File.Exists(path)) return NotFound("File missing on disk.");

        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        return File(bytes, receipt.ContentType, receipt.FileName);
    }

    // multipart/form-data: file + title + notes + totalAmount + purchaseDate + supplierId
    [HttpPost]
    [RequestSizeLimit(MaxFileSizeBytes)]
    public async Task<ActionResult<Receipt>> Upload(
        [FromForm] IFormFile file,
        [FromForm] string title,
        [FromForm] string? notes,
        [FromForm] decimal? totalAmount,
        [FromForm] DateTime? purchaseDate,
        [FromForm] int? supplierId)
    {
        if (file is null || file.Length == 0)
            return BadRequest("A receipt file (image or PDF) is required.");

        if (file.Length > MaxFileSizeBytes)
            return BadRequest("File exceeds the 10 MB limit.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return BadRequest($"Unsupported file type '{ext}'. Allowed: {string.Join(", ", AllowedExtensions)}");

        if (supplierId.HasValue && !await _db.Suppliers.AnyAsync(s => s.Id == supplierId))
            return BadRequest("Supplier not found.");

        var storedFileName = $"{Guid.NewGuid()}{ext}";
        var fullPath = Path.Combine(ReceiptsFolder, storedFileName);

        await using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var receipt = new Receipt
        {
            Title = string.IsNullOrWhiteSpace(title) ? file.FileName : title,
            Notes = notes,
            TotalAmount = totalAmount,
            PurchaseDate = purchaseDate ?? DateTime.UtcNow,
            SupplierId = supplierId,
            FileName = file.FileName,
            StoredFileName = storedFileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length
        };

        _db.Receipts.Add(receipt);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = receipt.Id }, receipt);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var receipt = await _db.Receipts.FindAsync(id);
        if (receipt is null) return NotFound();

        var path = Path.Combine(ReceiptsFolder, receipt.StoredFileName);
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        _db.Receipts.Remove(receipt);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
