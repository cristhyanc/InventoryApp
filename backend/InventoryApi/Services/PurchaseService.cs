using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using InventoryApi.DTOs;
using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;

namespace InventoryApi.Services;

public class PurchaseService : IPurchaseService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IInventoryCostRebuildService _rebuild;
    private readonly ComputePurchaseTotalValidation _computeValidation;

    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".pdf", ".webp", ".heic" };
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    public PurchaseService(
        AppDbContext db,
        IWebHostEnvironment env,
        IInventoryCostRebuildService? rebuild = null,
        ComputePurchaseTotalValidation? computeValidation = null)
    {
        _db = db;
        _env = env;
        _rebuild = rebuild ?? new InventoryCostRebuildService(db);
        _computeValidation = computeValidation ?? new ComputePurchaseTotalValidation();
    }

    // Physical storage for the uploaded scan/photo (the supporting document), distinct from
    // the purchase business record; see Purchase.StoredFileName. The documents live outside
    // the static web root and are only reachable through this service's [Authorize]d
    // controller. The category folder keeps its legacy "receipts" name so already-uploaded
    // files stay reachable; see ProtectedFileStorage.
    private string StoragePathFor(string storedFileName) =>
        ProtectedFileStorage.StoragePath(_env, ProtectedFileStorage.PurchaseDocumentsCategory, storedFileName);

    private string? ExistingPathFor(string? storedFileName) =>
        ProtectedFileStorage.ExistingPath(_env, ProtectedFileStorage.PurchaseDocumentsCategory, storedFileName);

    public async Task<IEnumerable<Purchase>> GetAll(int? supplierId)
    {
        var query = _db.Receipts.Include(r => r.Supplier).AsQueryable();
        if (supplierId.HasValue) query = query.Where(r => r.SupplierId == supplierId);
        return await query.Include(r => r.Items).ThenInclude(i => i.Product).OrderByDescending(r => r.PurchaseDate).ToListAsync();
    }

    public async Task<Purchase?> Get(int id)
    {
        return await _db.Receipts.Include(r => r.Supplier).Include(r => r.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<(byte[]? Content, string? ContentType, string? FileName)> GetFile(int id)
    {
        var purchase = await _db.Receipts.FindAsync(id);
        if (purchase is null) return (null, null, null);

        var path = ExistingPathFor(purchase.StoredFileName);
        if (path is null) return (null, null, null);

        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        return (bytes, purchase.ContentType, purchase.FileName);
    }

    public async Task<Purchase?> Upload(IFormFile file, string title, string? notes, decimal? totalAmount, decimal? deliveryCost, decimal? packageCost, DateTime? purchaseDate, int? supplierId, IReadOnlyList<PurchaseItemDto>? items = null)
    {
        if (file is null || file.Length == 0) return null;
        if (file.Length > MaxFileSizeBytes) return null;

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) return null;

        if (supplierId.HasValue && !await _db.Suppliers.AnyAsync(s => s.Id == supplierId)) return null;

        var purchaseItems = await ValidateItemsAsync(items ?? Array.Empty<PurchaseItemDto>());
        var effectivePurchaseDate = purchaseDate ?? DateTime.UtcNow;
        await ValidatePurchaseDatesAfterBaselinesAsync(
            purchaseItems.Select(x => x.ProductId),
            effectivePurchaseDate);
        var storedFileName = $"{Guid.NewGuid()}{ext}";
        var fullPath = StoragePathFor(storedFileName);

        await using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var purchase = new Purchase
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
        purchase.Items = purchaseItems.Select(x => new PurchaseItem
        {
            ProductId = x.ProductId,
            Quantity = x.Quantity,
            UnitCost = x.UnitCost
        }).ToList();

        try
        {
            await using var transaction = await BeginTransactionAsync();
            _db.Receipts.Add(purchase);
            await _db.SaveChangesAsync();
            await AllocateSupplierOrderFulfillmentAsync(purchase);
            foreach (var item in purchase.Items)
                _db.StockAdjustments.Add(CreatePurchaseMovement(item, purchase.PurchaseDate));
            await _db.SaveChangesAsync();
            await RebuildAffectedAsync(purchase.Items.Select(item => (item.ProductId, purchase.PurchaseDate)));
            await _db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
        }
        catch
        {
            if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
            throw;
        }

        return purchase;
    }

    private async Task AllocateSupplierOrderFulfillmentAsync(Purchase purchase)
    {
        if (!purchase.SupplierId.HasValue) return;
        var affectedOrderIds = new HashSet<int>();
        var allocatedByLineId = new Dictionary<int, decimal>();
        var purchaseDayEnd = purchase.PurchaseDate.Date.AddDays(1);
        foreach (var purchaseItem in purchase.Items)
        {
            var remainingQuantity = purchaseItem.Quantity;
            var lines = await _db.SupplierOrderLines
                .Include(line => line.SupplierOrder)
                .Where(line => line.ProductId == purchaseItem.ProductId &&
                    line.SupplierOrder.SupplierId == purchase.SupplierId &&
                    line.SupplierOrder.OrderDate < purchaseDayEnd &&
                    line.SupplierOrder.Status != SupplierOrderStatus.Cancelled &&
                    line.SupplierOrder.Status != SupplierOrderStatus.Received &&
                    line.QuantityReceived < line.QuantityOrdered)
                .OrderBy(line => line.SupplierOrder.OrderDate)
                .ThenBy(line => line.SupplierOrder.Id)
                .ThenBy(line => line.Id)
                .ToListAsync();

            foreach (var line in lines)
            {
                if (remainingQuantity <= 0) break;
                var outstandingQuantity = line.QuantityOrdered - line.QuantityReceived -
                    allocatedByLineId.GetValueOrDefault(line.Id);
                if (outstandingQuantity <= 0) continue;
                var receivedQuantity = Math.Min(remainingQuantity, outstandingQuantity);
                _db.SupplierOrderReceiptAllocations.Add(new SupplierOrderReceiptAllocation
                {
                    SupplierOrderLineId = line.Id,
                    ReceiptItemId = purchaseItem.Id,
                    QuantityApplied = receivedQuantity
                });
                affectedOrderIds.Add(line.SupplierOrderId);
                allocatedByLineId[line.Id] = allocatedByLineId.GetValueOrDefault(line.Id) + receivedQuantity;
                remainingQuantity -= receivedQuantity;
            }
        }

        await _db.SaveChangesAsync();
        await RecalculateSupplierOrderFulfillmentAsync(affectedOrderIds);
    }

    private async Task RemoveSupplierOrderFulfillmentAsync(IEnumerable<int> purchaseItemIds)
    {
        var allocations = await _db.SupplierOrderReceiptAllocations
            .Where(allocation => purchaseItemIds.Contains(allocation.ReceiptItemId))
            .ToListAsync();
        if (allocations.Count == 0) return;

        var orderIds = await _db.SupplierOrderLines
            .Where(line => allocations.Select(allocation => allocation.SupplierOrderLineId).Contains(line.Id))
            .Select(line => line.SupplierOrderId)
            .Distinct()
            .ToListAsync();
        _db.SupplierOrderReceiptAllocations.RemoveRange(allocations);
        await _db.SaveChangesAsync();
        await RecalculateSupplierOrderFulfillmentAsync(orderIds);
    }

    private async Task RecalculateSupplierOrderFulfillmentAsync(IEnumerable<int> orderIds)
    {
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var orders = await _db.SupplierOrders
            .Include(order => order.Lines)
                .ThenInclude(line => line.ReceiptAllocations)
            .Where(order => ids.Contains(order.Id))
            .ToListAsync();
        foreach (var order in orders)
        {
            foreach (var line in order.Lines)
                line.QuantityReceived = line.ReceiptAllocations.Sum(allocation => allocation.QuantityApplied);
            if (order.Status != SupplierOrderStatus.Cancelled)
                order.Status = order.Lines.All(line => line.QuantityReceived >= line.QuantityOrdered)
                    ? SupplierOrderStatus.Received
                    : order.Lines.Any(line => line.QuantityReceived > 0)
                        ? SupplierOrderStatus.PartiallyReceived
                        : SupplierOrderStatus.Ordered;
            order.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    public async Task<Purchase?> Update(int id, string? title, string? notes, decimal? totalAmount, decimal? deliveryCost, decimal? packageCost, DateTime? purchaseDate, int? supplierId, IReadOnlyList<PurchaseItemDto>? items = null)
    {
        var purchase = await _db.Receipts.Include(r => r.Supplier).Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (purchase is null) return null;

        if (supplierId.HasValue && !await _db.Suppliers.AnyAsync(s => s.Id == supplierId)) return null;

        var originalPurchaseDate = purchase.PurchaseDate;
        var originalSupplierId = purchase.SupplierId;
        var originalItems = purchase.Items.ToList();
        purchase.Title = string.IsNullOrWhiteSpace(title) ? purchase.Title : title;
        purchase.Notes = notes;
        purchase.TotalAmount = totalAmount;
        purchase.DeliveryCost = deliveryCost;
        purchase.PackageCost = packageCost;
        purchase.PurchaseDate = purchaseDate ?? purchase.PurchaseDate;
        purchase.SupplierId = supplierId;
        var requiresFulfillmentReconciliation = items is not null ||
            originalSupplierId != purchase.SupplierId || originalPurchaseDate != purchase.PurchaseDate;
        var affected = new Dictionary<long, DateTime>();
        await using var transaction = await BeginTransactionAsync();
        if (requiresFulfillmentReconciliation)
            await RemoveSupplierOrderFulfillmentAsync(originalItems.Select(item => item.Id));
        if (items is not null)
        {
            var validated = await ValidateItemsAsync(items);
            await EnsureLegacyPurchaseMovementsArePreservedAsync(
                originalItems,
                validated,
                originalPurchaseDate,
                purchase.PurchaseDate);
            await ValidatePurchaseDatesAfterBaselinesAsync(
                validated.Select(x => x.ProductId),
                purchase.PurchaseDate);
            var existingItems = purchase.Items.ToList();
            var existingMovements = await _db.StockAdjustments
                .Where(movement => movement.ReceiptItemId.HasValue &&
                    existingItems.Select(item => item.Id).Contains(movement.ReceiptItemId.Value))
                .ToListAsync();
            var movementByPurchaseItem = existingMovements
                .GroupBy(movement => movement.ReceiptItemId!.Value)
                .ToDictionary(group => group.Key, group => group.Single());
            var availableByProduct = existingItems
                .GroupBy(item => item.ProductId)
                .ToDictionary(group => group.Key, group => new Queue<PurchaseItem>(group));
            var newItems = new List<PurchaseItem>();

            foreach (var requested in validated)
            {
                if (availableByProduct.TryGetValue(requested.ProductId, out var matches) && matches.Count > 0)
                {
                    var item = matches.Dequeue();
                    AddAffected(affected, item.ProductId, movementByPurchaseItem.TryGetValue(item.Id, out var movement)
                        ? movement.EffectiveAt : originalPurchaseDate);
                    AddAffected(affected, item.ProductId, purchase.PurchaseDate);
                    item.Quantity = requested.Quantity;
                    item.UnitCost = requested.UnitCost;
                    if (movement is not null)
                    {
                        movement.QuantityChange = ToStockQuantity(requested.Quantity);
                        movement.UnitCost = requested.UnitCost;
                        movement.TotalCost = requested.Quantity * requested.UnitCost;
                        movement.EffectiveAt = purchase.PurchaseDate;
                        movement.Notes = "Receipt purchase";
                    }
                    else
                    {
                        newItems.Add(item);
                    }
                }
                else
                {
                    var item = new PurchaseItem
                    {
                        ReceiptId = purchase.Id,
                        ProductId = requested.ProductId,
                        Quantity = requested.Quantity,
                        UnitCost = requested.UnitCost
                    };
                    purchase.Items.Add(item);
                    newItems.Add(item);
                    AddAffected(affected, item.ProductId, purchase.PurchaseDate);
                }
            }

            foreach (var remaining in availableByProduct.Values.SelectMany(queue => queue))
            {
                AddAffected(affected, remaining.ProductId,
                    movementByPurchaseItem.TryGetValue(remaining.Id, out var movement) ? movement.EffectiveAt : originalPurchaseDate);
                if (movement is not null)
                    _db.StockAdjustments.Remove(movement);
                _db.ReceiptItems.Remove(remaining);
                purchase.Items.Remove(remaining);
            }

            await _db.SaveChangesAsync();
            foreach (var item in newItems)
                _db.StockAdjustments.Add(CreatePurchaseMovement(item, purchase.PurchaseDate));
        }
        else if (purchase.PurchaseDate != originalPurchaseDate)
        {
            var existingItems = purchase.Items.ToList();
            await EnsureLegacyPurchaseMovementsArePreservedAsync(
                originalItems,
                null,
                originalPurchaseDate,
                purchase.PurchaseDate);
            await ValidatePurchaseDatesAfterBaselinesAsync(
                existingItems.Select(x => x.ProductId),
                purchase.PurchaseDate);
            var movements = await _db.StockAdjustments
                .Where(movement => movement.ReceiptItemId.HasValue &&
                    existingItems.Select(item => item.Id).Contains(movement.ReceiptItemId.Value))
                .ToListAsync();
            foreach (var movement in movements)
            {
                AddAffected(affected, movement.ProductId, movement.EffectiveAt);
                AddAffected(affected, movement.ProductId, purchase.PurchaseDate);
                movement.EffectiveAt = purchase.PurchaseDate;
            }
        }
        await _db.SaveChangesAsync();
        if (requiresFulfillmentReconciliation)
            await AllocateSupplierOrderFulfillmentAsync(purchase);
        await RebuildAffectedAsync(affected.Select(item => (item.Key, item.Value)));
        await _db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        return purchase;
    }

    public async Task<bool> Delete(int id)
    {
        var purchase = await _db.Receipts.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == id);
        if (purchase is null) return false;

        var path = ExistingPathFor(purchase.StoredFileName);
        var movements = await _db.StockAdjustments
            .Where(movement => movement.ReceiptItemId.HasValue &&
                purchase.Items.Select(item => item.Id).Contains(movement.ReceiptItemId.Value))
            .ToListAsync();
        await EnsureNoPreCutoffMovementsAsync(movements);
        var affected = movements.Select(movement => (movement.ProductId, movement.EffectiveAt)).ToList();
        await using var transaction = await BeginTransactionAsync();
        await RemoveSupplierOrderFulfillmentAsync(purchase.Items.Select(item => item.Id));
        _db.StockAdjustments.RemoveRange(movements);
        _db.Receipts.Remove(purchase);
        await _db.SaveChangesAsync();
        await RebuildAffectedAsync(affected);
        await _db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        if (path is not null && System.IO.File.Exists(path)) System.IO.File.Delete(path);
        return true;
    }

    private async Task<List<PurchaseItemDto>> ValidateItemsAsync(IReadOnlyList<PurchaseItemDto> items)
    {
        if (items.Any(i => i.ProductId <= 0 || i.Quantity <= 0 || i.UnitCost < 0 ||
            i.Quantity != decimal.Truncate(i.Quantity)))
            throw new InvalidOperationException("Purchase item products, quantities, and costs are invalid.");
        var ids = items.Select(i => i.ProductId).Distinct().ToList();
        var count = await _db.Products.CountAsync(p => ids.Contains(p.Id));
        if (count != ids.Count) throw new InvalidOperationException("One or more purchase products do not exist.");
        return items.ToList();
    }

    public PurchaseValidationDto? ComputeValidation(Purchase purchase)
    {
        var items = purchase.Items ?? new List<PurchaseItem>();
        var validationItems = items.Select(i => new PurchaseTotalValidationItem(i.Quantity, i.UnitCost));
        var result = _computeValidation.Handle(purchase.TotalAmount, purchase.DeliveryCost, purchase.PackageCost, validationItems);
        return new PurchaseValidationDto(
            result.HasMismatch,
            result.ItemSubtotal,
            result.CalculatedTotal,
            result.Difference);
    }

    private static int ToStockQuantity(decimal quantity) =>
        checked((int)quantity);

    private static StockAdjustment CreatePurchaseMovement(PurchaseItem item, DateTime purchaseDate) =>
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

    private async Task EnsureLegacyPurchaseMovementsArePreservedAsync(
        IReadOnlyCollection<PurchaseItem> existingItems,
        IReadOnlyCollection<PurchaseItemDto>? requestedItems,
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
                "This purchase contains preserved pre-cutover inventory movements. Its date, products, quantities, and costs cannot be changed.");
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
                $"Purchase movement {protectedMovement.Id} is part of preserved pre-cutover history and cannot be changed or deleted.");
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync()
    {
        return _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
    }
}
