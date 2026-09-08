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
    private readonly IInventoryCostRebuildService _rebuild;

    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".pdf", ".webp", ".heic" };
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;
    private const decimal ReceiptTotalTolerance = 0.02m;

    public ReceiptService(AppDbContext db, IWebHostEnvironment env, IInventoryCostRebuildService? rebuild = null)
    {
        _db = db;
        _env = env;
        _rebuild = rebuild ?? new InventoryCostRebuildService(db);
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
        var effectivePurchaseDate = purchaseDate ?? DateTime.UtcNow;
        await ValidatePurchaseDatesAfterBaselinesAsync(
            receiptItems.Select(x => x.ProductId),
            effectivePurchaseDate);
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
            PurchaseDate = effectivePurchaseDate,
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
            _db.Receipts.Add(receipt);
            await _db.SaveChangesAsync();
            foreach (var item in receipt.Items)
                _db.StockAdjustments.Add(CreatePurchaseMovement(item, receipt.PurchaseDate));
            await _db.SaveChangesAsync();
            await RebuildAffectedAsync(receipt.Items.Select(item => (item.ProductId, receipt.PurchaseDate)));
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

        var originalPurchaseDate = receipt.PurchaseDate;
        var originalItems = receipt.Items.ToList();
        receipt.Title = string.IsNullOrWhiteSpace(title) ? receipt.Title : title;
        receipt.Notes = notes;
        receipt.TotalAmount = totalAmount;
        receipt.DeliveryCost = deliveryCost;
        receipt.PackageCost = packageCost;
        receipt.PurchaseDate = purchaseDate ?? receipt.PurchaseDate;
        receipt.SupplierId = supplierId;
        var affected = new Dictionary<long, DateTime>();
        await using var transaction = await BeginTransactionAsync();
        if (items is not null)
        {
            var validated = await ValidateItemsAsync(items);
            await EnsureLegacyReceiptMovementsArePreservedAsync(
                originalItems,
                validated,
                originalPurchaseDate,
                receipt.PurchaseDate);
            await ValidatePurchaseDatesAfterBaselinesAsync(
                validated.Select(x => x.ProductId),
                receipt.PurchaseDate);
            ApplyTotalWarning(receipt, validated);
            var existingItems = receipt.Items.ToList();
            var existingMovements = await _db.StockAdjustments
                .Where(movement => movement.ReceiptItemId.HasValue &&
                    existingItems.Select(item => item.Id).Contains(movement.ReceiptItemId.Value))
                .ToListAsync();
            var movementByReceiptItem = existingMovements
                .GroupBy(movement => movement.ReceiptItemId!.Value)
                .ToDictionary(group => group.Key, group => group.Single());
            var availableByProduct = existingItems
                .GroupBy(item => item.ProductId)
                .ToDictionary(group => group.Key, group => new Queue<ReceiptItem>(group));
            var newItems = new List<ReceiptItem>();

            foreach (var requested in validated)
            {
                if (availableByProduct.TryGetValue(requested.ProductId, out var matches) && matches.Count > 0)
                {
                    var item = matches.Dequeue();
                    AddAffected(affected, item.ProductId, movementByReceiptItem.TryGetValue(item.Id, out var movement)
                        ? movement.EffectiveAt : originalPurchaseDate);
                    AddAffected(affected, item.ProductId, receipt.PurchaseDate);
                    item.Quantity = requested.Quantity;
                    item.UnitCost = requested.UnitCost;
                    if (movement is not null)
                    {
                        movement.QuantityChange = ToStockQuantity(requested.Quantity);
                        movement.UnitCost = requested.UnitCost;
                        movement.TotalCost = requested.Quantity * requested.UnitCost;
                        movement.EffectiveAt = receipt.PurchaseDate;
                        movement.Notes = "Receipt purchase";
                    }
                    else
                    {
                        newItems.Add(item);
                    }
                }
                else
                {
                    var item = new ReceiptItem
                    {
                        ReceiptId = receipt.Id,
                        ProductId = requested.ProductId,
                        Quantity = requested.Quantity,
                        UnitCost = requested.UnitCost
                    };
                    receipt.Items.Add(item);
                    newItems.Add(item);
                    AddAffected(affected, item.ProductId, receipt.PurchaseDate);
                }
            }

            foreach (var remaining in availableByProduct.Values.SelectMany(queue => queue))
            {
                AddAffected(affected, remaining.ProductId,
                    movementByReceiptItem.TryGetValue(remaining.Id, out var movement) ? movement.EffectiveAt : originalPurchaseDate);
                if (movement is not null)
                    _db.StockAdjustments.Remove(movement);
                _db.ReceiptItems.Remove(remaining);
                receipt.Items.Remove(remaining);
            }

            await _db.SaveChangesAsync();
            foreach (var item in newItems)
                _db.StockAdjustments.Add(CreatePurchaseMovement(item, receipt.PurchaseDate));
        }
        else if (receipt.PurchaseDate != originalPurchaseDate)
        {
            var existingItems = receipt.Items.ToList();
            await EnsureLegacyReceiptMovementsArePreservedAsync(
                originalItems,
                null,
                originalPurchaseDate,
                receipt.PurchaseDate);
            await ValidatePurchaseDatesAfterBaselinesAsync(
                existingItems.Select(x => x.ProductId),
                receipt.PurchaseDate);
            var movements = await _db.StockAdjustments
                .Where(movement => movement.ReceiptItemId.HasValue &&
                    existingItems.Select(item => item.Id).Contains(movement.ReceiptItemId.Value))
                .ToListAsync();
            foreach (var movement in movements)
            {
                AddAffected(affected, movement.ProductId, movement.EffectiveAt);
                AddAffected(affected, movement.ProductId, receipt.PurchaseDate);
                movement.EffectiveAt = receipt.PurchaseDate;
            }
        }
        await _db.SaveChangesAsync();
        await RebuildAffectedAsync(affected.Select(item => (item.Key, item.Value)));
        await _db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        return receipt;
    }

    public async Task<bool> Delete(int id)
    {
        var receipt = await _db.Receipts.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == id);
        if (receipt is null) return false;

        var path = Path.Combine(ReceiptsFolder, receipt.StoredFileName);
        var movements = await _db.StockAdjustments
            .Where(movement => movement.ReceiptItemId.HasValue &&
                receipt.Items.Select(item => item.Id).Contains(movement.ReceiptItemId.Value))
            .ToListAsync();
        await EnsureNoPreCutoffMovementsAsync(movements);
        var affected = movements.Select(movement => (movement.ProductId, movement.EffectiveAt)).ToList();
        await using var transaction = await BeginTransactionAsync();
        _db.StockAdjustments.RemoveRange(movements);
        _db.Receipts.Remove(receipt);
        await _db.SaveChangesAsync();
        await RebuildAffectedAsync(affected);
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

    private static int ToStockQuantity(decimal quantity) =>
        checked((int)quantity);

    private static StockAdjustment CreatePurchaseMovement(ReceiptItem item, DateTime purchaseDate) =>
        new()
        {
            ProductId = item.ProductId,
            ReceiptItemId = item.Id,
            QuantityChange = ToStockQuantity(item.Quantity),
            Reason = StockAdjustmentReason.Restock,
            UnitCost = item.UnitCost,
            TotalCost = item.Quantity * item.UnitCost,
            Notes = "Receipt purchase",
            EffectiveAt = purchaseDate
        };

    private static void AddAffected(IDictionary<long, DateTime> affected, long productId, DateTime changedAt)
    {
        if (!affected.TryGetValue(productId, out var existing) || changedAt < existing)
            affected[productId] = changedAt;
    }

    private async Task RebuildAffectedAsync(IEnumerable<(long ProductId, DateTime ChangedAt)> affected)
    {
        foreach (var product in affected.GroupBy(x => x.ProductId)
                     .Select(group => (ProductId: group.Key, ChangedAt: group.Min(x => x.ChangedAt))))
            await _rebuild.RebuildAsync(product.ProductId, product.ChangedAt);
    }

    private async Task ValidatePurchaseDatesAfterBaselinesAsync(
        IEnumerable<long> productIds,
        DateTime purchaseDate)
    {
        var ids = productIds.Distinct().ToList();
        var conflicting = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => ids.Contains(x.ProductId) && purchaseDate <= x.CutoffAt)
            .Select(x => new { x.ProductId, x.CutoffAt })
            .FirstOrDefaultAsync();
        if (conflicting is not null)
            throw new InvalidOperationException(
                $"Purchase date must be after the inventory-cost transition cutoff {conflicting.CutoffAt:O} for product {conflicting.ProductId}.");
    }

    private async Task EnsureLegacyReceiptMovementsArePreservedAsync(
        IReadOnlyCollection<ReceiptItem> existingItems,
        IReadOnlyCollection<ReceiptItemDto>? requestedItems,
        DateTime originalPurchaseDate,
        DateTime proposedPurchaseDate)
    {
        var itemIds = existingItems.Select(x => x.Id).ToList();
        var movements = await _db.StockAdjustments.AsNoTracking()
            .Where(x => x.ReceiptItemId.HasValue && itemIds.Contains(x.ReceiptItemId.Value))
            .ToListAsync();
        var productIds = movements.Select(x => x.ProductId).Distinct().ToList();
        var cutoffs = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => productIds.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt);
        if (!movements.Any(x => cutoffs.TryGetValue(x.ProductId, out var cutoff) && x.EffectiveAt <= cutoff))
            return;
        var itemsChanged = requestedItems is not null &&
            !existingItems
                .Select(x => (x.ProductId, x.Quantity, x.UnitCost))
                .OrderBy(x => x.ProductId).ThenBy(x => x.Quantity).ThenBy(x => x.UnitCost)
                .SequenceEqual(requestedItems
                    .Select(x => (x.ProductId, x.Quantity, x.UnitCost))
                    .OrderBy(x => x.ProductId).ThenBy(x => x.Quantity).ThenBy(x => x.UnitCost));
        if (proposedPurchaseDate != originalPurchaseDate || itemsChanged)
            throw new InvalidOperationException(
                "This receipt contains preserved pre-cutover inventory movements. Its date, products, quantities, and costs cannot be changed.");
    }

    private async Task EnsureNoPreCutoffMovementsAsync(IReadOnlyCollection<StockAdjustment> movements)
    {
        var productIds = movements.Select(x => x.ProductId).Distinct().ToList();
        var cutoffs = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => productIds.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt);
        var protectedMovement = movements.FirstOrDefault(x =>
            cutoffs.TryGetValue(x.ProductId, out var cutoff) && x.EffectiveAt <= cutoff);
        if (protectedMovement is not null)
            throw new InvalidOperationException(
                $"Receipt movement {protectedMovement.Id} is part of preserved pre-cutover history and cannot be changed or deleted.");
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync()
    {
        return _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
    }
}
