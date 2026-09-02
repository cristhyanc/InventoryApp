using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public class ReceiptService : IReceiptService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".pdf", ".webp", ".heic" };
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    public ReceiptService(AppDbContext db, IWebHostEnvironment env)
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

    public async Task<IEnumerable<Receipt>> GetAll(int? supplierId)
    {
        var query = _db.Receipts.Include(r => r.Supplier).AsQueryable();
        if (supplierId.HasValue) query = query.Where(r => r.SupplierId == supplierId);
        return await query.OrderByDescending(r => r.PurchaseDate).ToListAsync();
    }

    public async Task<Receipt?> Get(int id)
    {
        return await _db.Receipts.Include(r => r.Supplier).FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<(byte[]? Content, string? ContentType, string? FileName)> GetFile(int id)
    {
        var receipt = await _db.Receipts.FindAsync(id);
        if (receipt is null) return (null, null, null);

        var path = Path.Combine(ReceiptsFolder, receipt.StoredFileName);
        if (!System.IO.File.Exists(path)) return (null, null, null);

        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        return (bytes, receipt.ContentType, receipt.FileName);
    }

    public async Task<Receipt?> Upload(IFormFile file, string title, string? notes, decimal? totalAmount, decimal? deliveryCost, decimal? packageCost, DateTime? purchaseDate, int? supplierId)
    {
        if (file is null || file.Length == 0) return null;
        if (file.Length > MaxFileSizeBytes) return null;

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) return null;

        if (supplierId.HasValue && !await _db.Suppliers.AnyAsync(s => s.Id == supplierId)) return null;

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
            DeliveryCost = deliveryCost,
            PackageCost = packageCost,
            PurchaseDate = purchaseDate ?? DateTime.UtcNow,
            SupplierId = supplierId,
            FileName = file.FileName,
            StoredFileName = storedFileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length
        };

        _db.Receipts.Add(receipt);
        await _db.SaveChangesAsync();

        return receipt;
    }

    public async Task<Receipt?> Update(int id, string? title, string? notes, decimal? totalAmount, decimal? deliveryCost, decimal? packageCost, DateTime? purchaseDate, int? supplierId)
    {
        var receipt = await _db.Receipts.Include(r => r.Supplier).FirstOrDefaultAsync(r => r.Id == id);
        if (receipt is null) return null;

        if (supplierId.HasValue && !await _db.Suppliers.AnyAsync(s => s.Id == supplierId)) return null;

        receipt.Title = string.IsNullOrWhiteSpace(title) ? receipt.Title : title;
        receipt.Notes = notes;
        receipt.TotalAmount = totalAmount;
        receipt.DeliveryCost = deliveryCost;
        receipt.PackageCost = packageCost;
        receipt.PurchaseDate = purchaseDate ?? receipt.PurchaseDate;
        receipt.SupplierId = supplierId;

        await _db.SaveChangesAsync();
        return receipt;
    }

    public async Task<bool> Delete(int id)
    {
        var receipt = await _db.Receipts.FindAsync(id);
        if (receipt is null) return false;

        var path = Path.Combine(ReceiptsFolder, receipt.StoredFileName);
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        _db.Receipts.Remove(receipt);
        await _db.SaveChangesAsync();
        return true;
    }
}
