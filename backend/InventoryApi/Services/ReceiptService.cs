using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using InventoryApi.DTOs;

namespace InventoryApi.Services;

public class ReceiptService : IReceiptService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".pdf", ".webp", ".heic" };
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;
    private const decimal ReceiptTotalTolerance = 0.02m;

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
        return await query.Include(r => r.Items).ThenInclude(i => i.Product).OrderByDescending(r => r.PurchaseDate).ToListAsync();
    }

    public async Task<Receipt?> Get(int id)
    {
        return await _db.Receipts.Include(r => r.Supplier).Include(r => r.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(r => r.Id == id);
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

    public async Task<Receipt?> Upload(IFormFile file, string title, string? notes, decimal? totalAmount, decimal? deliveryCost, decimal? packageCost, DateTime? purchaseDate, int? supplierId, IReadOnlyList<ReceiptItemDto>? items = null)
    {
        if (file is null || file.Length == 0) return null;
        if (file.Length > MaxFileSizeBytes) return null;

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) return null;

        if (supplierId.HasValue && !await _db.Suppliers.AnyAsync(s => s.Id == supplierId)) return null;

        var receiptItems = await ValidateItemsAsync(items ?? Array.Empty<ReceiptItemDto>());
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
        ApplyTotalWarning(receipt, receiptItems);
        receipt.Items = receiptItems.Select(x => new ReceiptItem
        {
            ProductId = x.ProductId,
            Quantity = x.Quantity,
            UnitCost = x.UnitCost
        }).ToList();

        try
        {
            await using var transaction = await BeginTransactionAsync();
            foreach (var item in receipt.Items)
                AddStockMovement(item.ProductId, ToStockQuantity(item.Quantity), StockAdjustmentReason.Restock, item, $"Receipt {receipt.Id}");
            _db.Receipts.Add(receipt);
            await _db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
        }
        catch
        {
            if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
            throw;
        }

        return receipt;
    }

    public async Task<Receipt?> Update(int id, string? title, string? notes, decimal? totalAmount, decimal? deliveryCost, decimal? packageCost, DateTime? purchaseDate, int? supplierId, IReadOnlyList<ReceiptItemDto>? items = null)
    {
        var receipt = await _db.Receipts.Include(r => r.Supplier).Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (receipt is null) return null;

        if (supplierId.HasValue && !await _db.Suppliers.AnyAsync(s => s.Id == supplierId)) return null;

        receipt.Title = string.IsNullOrWhiteSpace(title) ? receipt.Title : title;
        receipt.Notes = notes;
        receipt.TotalAmount = totalAmount;
        receipt.DeliveryCost = deliveryCost;
        receipt.PackageCost = packageCost;
        receipt.PurchaseDate = purchaseDate ?? receipt.PurchaseDate;
        receipt.SupplierId = supplierId;
        if (items is not null)
        {
            var validated = await ValidateItemsAsync(items);
            ApplyTotalWarning(receipt, validated);
            var oldByProduct = receipt.Items.GroupBy(x => x.ProductId).ToDictionary(x => x.Key, x => x.Sum(i => i.Quantity));
            var newByProduct = validated.GroupBy(x => x.ProductId).ToDictionary(x => x.Key, x => x.Sum(i => i.Quantity));
            foreach (var productId in oldByProduct.Keys.Union(newByProduct.Keys))
            {
                var delta = newByProduct.GetValueOrDefault(productId) - oldByProduct.GetValueOrDefault(productId);
                if (delta != 0)
                    AddStockMovement(productId, ToStockQuantity(delta), StockAdjustmentReason.Restock, null, $"Receipt {receipt.Id} adjustment");
            }
            _db.ReceiptItems.RemoveRange(receipt.Items);
            receipt.Items = validated.Select(x => new ReceiptItem
            {
                ReceiptId = receipt.Id, ProductId = x.ProductId, Quantity = x.Quantity, UnitCost = x.UnitCost
            }).ToList();
        }
        await using var transaction = await BeginTransactionAsync();
        await _db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        return receipt;
    }

    public async Task<bool> Delete(int id)
    {
        var receipt = await _db.Receipts.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == id);
        if (receipt is null) return false;

        var path = Path.Combine(ReceiptsFolder, receipt.StoredFileName);
        await using var transaction = await BeginTransactionAsync();
        foreach (var item in receipt.Items)
            AddStockMovement(item.ProductId, -ToStockQuantity(item.Quantity), StockAdjustmentReason.Restock, null, $"Receipt {receipt.Id} reversal");
        _db.Receipts.Remove(receipt);
        await _db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        return true;
    }

    private async Task<List<ReceiptItemDto>> ValidateItemsAsync(IReadOnlyList<ReceiptItemDto> items)
    {
        if (items.Any(i => i.ProductId <= 0 || i.Quantity <= 0 || i.UnitCost < 0 ||
            i.Quantity != decimal.Truncate(i.Quantity)))
            throw new InvalidOperationException("Receipt item products, quantities, and costs are invalid.");
        var ids = items.Select(i => i.ProductId).Distinct().ToList();
        var count = await _db.Products.CountAsync(p => ids.Contains(p.Id));
        if (count != ids.Count) throw new InvalidOperationException("One or more receipt products do not exist.");
        return items.ToList();
    }

    private static void ApplyTotalWarning(Receipt receipt, IEnumerable<ReceiptItemDto> items)
    {
        if (!receipt.TotalAmount.HasValue) return;
        var subtotal = items.Sum(i => i.Quantity * i.UnitCost);
        if (Math.Abs(subtotal - receipt.TotalAmount.Value) > ReceiptTotalTolerance)
            receipt.Notes = string.IsNullOrWhiteSpace(receipt.Notes)
                ? $"Warning: item subtotal {subtotal:0.00} differs from receipt total {receipt.TotalAmount.Value:0.00}."
                : $"{receipt.Notes} Warning: item subtotal {subtotal:0.00} differs from receipt total {receipt.TotalAmount.Value:0.00}.";
    }

    private void AddStockMovement(long productId, int quantityChange, StockAdjustmentReason reason, ReceiptItem? receiptItem, string notes)
    {
        var product = _db.Products.Local.FirstOrDefault(p => p.Id == productId) ??
            _db.Products.Find(productId);
        if (product is null) throw new InvalidOperationException($"Product {productId} was not loaded.");
        var newQuantity = product.QuantityInStock + quantityChange;
        if (newQuantity < 0) throw new InsufficientStockException(product.QuantityInStock);
        product.QuantityInStock = newQuantity;
        product.UpdatedAt = DateTime.UtcNow;
        _db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = productId, QuantityChange = quantityChange, QuantityAfter = newQuantity,
            Reason = reason, ReceiptItem = receiptItem, Notes = notes
        });
    }

    private static int ToStockQuantity(decimal quantity) =>
        checked((int)quantity);

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync()
    {
        return _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
    }
}
