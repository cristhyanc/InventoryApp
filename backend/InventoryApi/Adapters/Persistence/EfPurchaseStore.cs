using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;
using Inventory.Domain.SupplierOrders;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IPurchaseStore"/>. It lives in InventoryApi, not
/// Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/> and persistence
/// models that still live in InventoryApi, following the same precedent as
/// <c>EfOperatingExpenseStore</c>/<c>EfProductStore</c>. Move it into Inventory.Infrastructure once
/// <see cref="AppDbContext"/> and the shared persistence models relocate there.
///
/// Its multi-step writes - the purchase/supplier-order-fulfillment/stock-movement transaction in
/// <see cref="CreateAsync"/>/<see cref="UpdateAsync"/>/<see cref="DeleteAsync"/> - stay here rather
/// than being decomposed into Application-level orchestration, per the issue's target ownership:
/// EF queries, transactions, receipt allocations, stock-movement persistence, and costing-rebuild
/// calls remain Infrastructure/adapter concerns until #153 consolidates persistence. The
/// deterministic decisions within them - item format validity
/// (<see cref="PurchaseItemFormatPolicy"/>), the pre-cutover history guards
/// (<see cref="PurchaseCostTransitionPolicy"/>), restock movement arithmetic
/// (<see cref="PurchaseStockMovementPolicy"/>), the fulfillment-allocation algorithm
/// (<see cref="SupplierOrderFulfillmentAllocationPolicy"/>), and the fulfillment status rollup
/// (<see cref="SupplierOrderStatusPolicy"/>) - are Domain policies this adapter calls rather than
/// recomputing inline, mirroring the former
/// <c>InventoryApi.Services.PurchaseService</c> step for step.
/// </summary>
public sealed class EfPurchaseStore : IPurchaseStore
{
    private readonly AppDbContext _db;
    private readonly IInventoryCostRebuildService _rebuild;

    public EfPurchaseStore(AppDbContext db, IInventoryCostRebuildService rebuild)
    {
        _db = db;
        _rebuild = rebuild;
    }

    public async Task<IReadOnlyList<PurchaseRecord>> ListAsync(int? supplierId, CancellationToken cancellationToken)
    {
        var query = _db.Receipts.Include(r => r.Supplier).AsQueryable();
        if (supplierId.HasValue) query = query.Where(r => r.SupplierId == supplierId);

        var purchases = await query.Include(r => r.Items).ThenInclude(i => i.Product)
            .OrderByDescending(r => r.PurchaseDate).ToListAsync(cancellationToken);
        return purchases.Select(ToRecord).ToList();
    }

    public async Task<PurchaseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken)
    {
        var purchase = await _db.Receipts.Include(r => r.Supplier).Include(r => r.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return purchase is null ? null : ToRecord(purchase);
    }

    public async Task<PurchaseFileMetadata?> FindFileMetadataAsync(int id, CancellationToken cancellationToken)
    {
        var purchase = await _db.Receipts.FindAsync([id], cancellationToken);
        return purchase is null
            ? null
            : new PurchaseFileMetadata(purchase.StoredFileName, purchase.ContentType, purchase.FileName, purchase.FileSizeBytes);
    }

    public Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken) =>
        _db.Suppliers.AnyAsync(supplier => supplier.Id == supplierId, cancellationToken);

    public async Task<bool> AllProductsExistAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0) return true;
        var existingCount = await _db.Products.CountAsync(product => productIds.Contains(product.Id), cancellationToken);
        return existingCount == productIds.Count;
    }

    public async Task<(long ProductId, DateTime CutoffAt)?> FindConflictingCostTransitionBaselineAsync(
        IReadOnlyCollection<long> productIds, DateTime purchaseDate, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0) return null;
        var cutoffsByProduct = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => productIds.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, cancellationToken);
        return PurchaseCostTransitionPolicy.FindConflictingBaseline(productIds, purchaseDate, cutoffsByProduct);
    }

    public async Task<PurchaseRecord> CreateAsync(
        PurchaseFields fields, IReadOnlyList<PurchaseItemInput> items, PurchaseFileMetadata file, CancellationToken cancellationToken)
    {
        var purchase = new Purchase
        {
            Title = fields.Title ?? string.Empty,
            Notes = fields.Notes,
            TotalAmount = fields.TotalAmount,
            DeliveryCost = fields.DeliveryCost,
            PackageCost = fields.PackageCost,
            PurchaseDate = fields.PurchaseDate ?? DateTime.UtcNow,
            SupplierId = fields.SupplierId,
            FileName = file.FileName,
            StoredFileName = file.StoredFileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.FileSizeBytes,
        };
        purchase.Items = items.Select(x => new PurchaseItem
        {
            ProductId = x.ProductId,
            Quantity = x.Quantity,
            UnitCost = x.UnitCost,
        }).ToList();

        await using var transaction = await BeginTransactionAsync();
        _db.Receipts.Add(purchase);
        await _db.SaveChangesAsync(cancellationToken);
        await AllocateSupplierOrderFulfillmentAsync(purchase, cancellationToken);
        foreach (var item in purchase.Items)
            _db.StockAdjustments.Add(CreatePurchaseMovement(item, purchase.PurchaseDate));
        await _db.SaveChangesAsync(cancellationToken);
        await RebuildAffectedAsync(purchase.Items.Select(item => (item.ProductId, purchase.PurchaseDate)), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return ToRecord(purchase);
    }

    public async Task<PurchaseRecord?> UpdateAsync(
        int id, PurchaseFields fields, IReadOnlyList<PurchaseItemInput>? items, CancellationToken cancellationToken)
    {
        var purchase = await _db.Receipts.Include(r => r.Supplier).Include(r => r.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (purchase is null) return null;

        if (fields.SupplierId.HasValue && !await SupplierExistsAsync(fields.SupplierId.Value, cancellationToken))
            return null;

        var originalPurchaseDate = purchase.PurchaseDate;
        var originalSupplierId = purchase.SupplierId;
        var originalItems = purchase.Items.ToList();
        purchase.Title = string.IsNullOrWhiteSpace(fields.Title) ? purchase.Title : fields.Title;
        purchase.Notes = fields.Notes;
        purchase.TotalAmount = fields.TotalAmount;
        purchase.DeliveryCost = fields.DeliveryCost;
        purchase.PackageCost = fields.PackageCost;
        purchase.PurchaseDate = fields.PurchaseDate ?? purchase.PurchaseDate;
        purchase.SupplierId = fields.SupplierId;
        var requiresFulfillmentReconciliation = items is not null ||
            originalSupplierId != purchase.SupplierId || originalPurchaseDate != purchase.PurchaseDate;
        var affected = new Dictionary<long, DateTime>();

        await using var transaction = await BeginTransactionAsync();
        if (requiresFulfillmentReconciliation)
            await RemoveSupplierOrderFulfillmentAsync(originalItems.Select(item => item.Id), cancellationToken);

        if (items is not null)
        {
            if (PurchaseItemFormatPolicy.HasInvalidItem(items.Select(ToCandidate)))
                throw new InvalidOperationException(PurchaseItemFormatPolicy.InvalidItemsMessage);

            var requestedProductIds = items.Select(x => x.ProductId).Distinct().ToList();
            if (!await AllProductsExistAsync(requestedProductIds, cancellationToken))
                throw new InvalidOperationException("One or more purchase products do not exist.");

            await EnsureLegacyPurchaseMovementsArePreservedAsync(
                originalItems, items, originalPurchaseDate, purchase.PurchaseDate, cancellationToken);
            await EnsureDateAfterBaselinesAsync(requestedProductIds, purchase.PurchaseDate, cancellationToken);

            var existingItems = purchase.Items.ToList();
            var existingMovements = await _db.StockAdjustments
                .Where(movement => movement.ReceiptItemId.HasValue &&
                    existingItems.Select(item => item.Id).Contains(movement.ReceiptItemId.Value))
                .ToListAsync(cancellationToken);
            var movementByPurchaseItem = existingMovements
                .GroupBy(movement => movement.ReceiptItemId!.Value)
                .ToDictionary(group => group.Key, group => group.Single());
            var availableByProduct = existingItems
                .GroupBy(item => item.ProductId)
                .ToDictionary(group => group.Key, group => new Queue<PurchaseItem>(group));
            var newItems = new List<PurchaseItem>();

            foreach (var requested in items)
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
                        movement.QuantityChange = PurchaseStockMovementPolicy.ToStockQuantity(requested.Quantity);
                        movement.UnitCost = requested.UnitCost;
                        movement.TotalCost = PurchaseStockMovementPolicy.TotalCost(requested.Quantity, requested.UnitCost);
                        movement.EffectiveAt = purchase.PurchaseDate;
                        movement.Notes = PurchaseStockMovementPolicy.RestockMovementNotes;
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
                        UnitCost = requested.UnitCost,
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

            await _db.SaveChangesAsync(cancellationToken);
            foreach (var item in newItems)
                _db.StockAdjustments.Add(CreatePurchaseMovement(item, purchase.PurchaseDate));
        }
        else if (purchase.PurchaseDate != originalPurchaseDate)
        {
            var existingItems = purchase.Items.ToList();
            await EnsureLegacyPurchaseMovementsArePreservedAsync(
                originalItems, null, originalPurchaseDate, purchase.PurchaseDate, cancellationToken);
            await EnsureDateAfterBaselinesAsync(
                existingItems.Select(x => x.ProductId).Distinct().ToList(), purchase.PurchaseDate, cancellationToken);
            var movements = await _db.StockAdjustments
                .Where(movement => movement.ReceiptItemId.HasValue &&
                    existingItems.Select(item => item.Id).Contains(movement.ReceiptItemId.Value))
                .ToListAsync(cancellationToken);
            foreach (var movement in movements)
            {
                AddAffected(affected, movement.ProductId, movement.EffectiveAt);
                AddAffected(affected, movement.ProductId, purchase.PurchaseDate);
                movement.EffectiveAt = purchase.PurchaseDate;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        if (requiresFulfillmentReconciliation)
            await AllocateSupplierOrderFulfillmentAsync(purchase, cancellationToken);
        await RebuildAffectedAsync(affected.Select(item => (item.Key, item.Value)), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return ToRecord(purchase);
    }

    public async Task<string?> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var purchase = await _db.Receipts.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (purchase is null) return null;

        var storedFileName = purchase.StoredFileName;
        var movements = await _db.StockAdjustments
            .Where(movement => movement.ReceiptItemId.HasValue &&
                purchase.Items.Select(item => item.Id).Contains(movement.ReceiptItemId.Value))
            .ToListAsync(cancellationToken);
        await EnsureNoPreCutoffMovementsAsync(movements, cancellationToken);
        var affected = movements.Select(movement => (movement.ProductId, movement.EffectiveAt)).ToList();

        await using var transaction = await BeginTransactionAsync();
        await RemoveSupplierOrderFulfillmentAsync(purchase.Items.Select(item => item.Id), cancellationToken);
        _db.StockAdjustments.RemoveRange(movements);
        _db.Receipts.Remove(purchase);
        await _db.SaveChangesAsync(cancellationToken);
        await RebuildAffectedAsync(affected, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return storedFileName;
    }

    private async Task AllocateSupplierOrderFulfillmentAsync(Purchase purchase, CancellationToken cancellationToken)
    {
        if (!purchase.SupplierId.HasValue) return;

        var purchaseDayEnd = purchase.PurchaseDate.Date.AddDays(1);
        var candidateLinesByProduct = new Dictionary<long, IReadOnlyList<CandidateOrderLine>>();
        foreach (var productId in purchase.Items.Select(item => item.ProductId).Distinct())
        {
            var lines = await _db.SupplierOrderLines
                .Include(line => line.SupplierOrder)
                .Where(line => line.ProductId == productId &&
                    line.SupplierOrder.SupplierId == purchase.SupplierId &&
                    line.SupplierOrder.OrderDate < purchaseDayEnd &&
                    line.SupplierOrder.Status != Models.SupplierOrderStatus.Cancelled &&
                    line.SupplierOrder.Status != Models.SupplierOrderStatus.Received &&
                    line.QuantityReceived < line.QuantityOrdered)
                .OrderBy(line => line.SupplierOrder.OrderDate)
                .ThenBy(line => line.SupplierOrder.Id)
                .ThenBy(line => line.Id)
                .ToListAsync(cancellationToken);

            candidateLinesByProduct[productId] = lines
                .Select(line => new CandidateOrderLine(line.Id, line.SupplierOrderId, line.QuantityOrdered - line.QuantityReceived))
                .ToList();
        }

        var purchaseItemsToAllocate = purchase.Items
            .Select(item => new PurchaseItemToAllocate(item.Id, item.ProductId, item.Quantity))
            .ToList();
        var allocations = SupplierOrderFulfillmentAllocationPolicy.Allocate(purchaseItemsToAllocate, candidateLinesByProduct);
        if (allocations.Count == 0) return;

        foreach (var allocation in allocations)
        {
            _db.SupplierOrderReceiptAllocations.Add(new SupplierOrderReceiptAllocation
            {
                SupplierOrderLineId = allocation.SupplierOrderLineId,
                ReceiptItemId = allocation.ReceiptItemId,
                QuantityApplied = allocation.QuantityApplied,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        await RecalculateSupplierOrderFulfillmentAsync(allocations.Select(allocation => allocation.SupplierOrderId), cancellationToken);
    }

    private async Task RemoveSupplierOrderFulfillmentAsync(IEnumerable<int> purchaseItemIds, CancellationToken cancellationToken)
    {
        var ids = purchaseItemIds.ToList();
        var allocations = await _db.SupplierOrderReceiptAllocations
            .Where(allocation => ids.Contains(allocation.ReceiptItemId))
            .ToListAsync(cancellationToken);
        if (allocations.Count == 0) return;

        var orderIds = await _db.SupplierOrderLines
            .Where(line => allocations.Select(allocation => allocation.SupplierOrderLineId).Contains(line.Id))
            .Select(line => line.SupplierOrderId)
            .Distinct()
            .ToListAsync(cancellationToken);
        _db.SupplierOrderReceiptAllocations.RemoveRange(allocations);
        await _db.SaveChangesAsync(cancellationToken);
        await RecalculateSupplierOrderFulfillmentAsync(orderIds, cancellationToken);
    }

    private async Task RecalculateSupplierOrderFulfillmentAsync(IEnumerable<int> orderIds, CancellationToken cancellationToken)
    {
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0) return;

        var orders = await _db.SupplierOrders
            .Include(order => order.Lines)
                .ThenInclude(line => line.ReceiptAllocations)
            .Where(order => ids.Contains(order.Id))
            .ToListAsync(cancellationToken);
        foreach (var order in orders)
        {
            foreach (var line in order.Lines)
                line.QuantityReceived = line.ReceiptAllocations.Sum(allocation => allocation.QuantityApplied);
            if (order.Status != Models.SupplierOrderStatus.Cancelled)
                order.Status = (Models.SupplierOrderStatus)SupplierOrderStatusPolicy.Resolve(
                    order.Lines.Select(line => (line.QuantityOrdered, line.QuantityReceived)));
            order.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureDateAfterBaselinesAsync(
        IReadOnlyCollection<long> productIds, DateTime purchaseDate, CancellationToken cancellationToken)
    {
        var conflict = await FindConflictingCostTransitionBaselineAsync(productIds, purchaseDate, cancellationToken);
        if (conflict is { } found)
            throw new InvalidOperationException(PurchaseCostTransitionPolicy.DateBeforeCutoffMessage(found.CutoffAt, found.ProductId));
    }

    private async Task EnsureLegacyPurchaseMovementsArePreservedAsync(
        IReadOnlyCollection<PurchaseItem> existingItems,
        IReadOnlyList<PurchaseItemInput>? requestedItems,
        DateTime originalPurchaseDate,
        DateTime proposedPurchaseDate,
        CancellationToken cancellationToken)
    {
        var itemIds = existingItems.Select(x => x.Id).ToList();
        var movements = await _db.StockAdjustments.AsNoTracking()
            .Where(x => x.ReceiptItemId.HasValue && itemIds.Contains(x.ReceiptItemId.Value))
            .ToListAsync(cancellationToken);
        var productIds = movements.Select(x => x.ProductId).Distinct().ToList();
        var cutoffs = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => productIds.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, cancellationToken);

        if (!PurchaseCostTransitionPolicy.HasPreservedMovement(movements.Select(x => (x.ProductId, x.EffectiveAt)), cutoffs))
            return;

        var itemsChanged = requestedItems is not null &&
            !existingItems
                .Select(x => (x.ProductId, x.Quantity, x.UnitCost))
                .OrderBy(x => x.ProductId).ThenBy(x => x.Quantity).ThenBy(x => x.UnitCost)
                .SequenceEqual(requestedItems
                    .Select(x => (x.ProductId, x.Quantity, x.UnitCost))
                    .OrderBy(x => x.ProductId).ThenBy(x => x.Quantity).ThenBy(x => x.UnitCost));
        var dateChanged = proposedPurchaseDate != originalPurchaseDate;

        if (!PurchaseCostTransitionPolicy.IsPreservedChangeAllowed(dateChanged, itemsChanged))
            throw new InvalidOperationException(PurchaseCostTransitionPolicy.PreservedMovementsMessage);
    }

    private async Task EnsureNoPreCutoffMovementsAsync(IReadOnlyCollection<StockAdjustment> movements, CancellationToken cancellationToken)
    {
        var productIds = movements.Select(x => x.ProductId).Distinct().ToList();
        var cutoffs = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => productIds.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, cancellationToken);
        var protectedMovement = PurchaseCostTransitionPolicy.FindProtectedMovement(
            movements.Select(x => (x.Id, x.ProductId, x.EffectiveAt)), cutoffs);
        if (protectedMovement is { } found)
            throw new InvalidOperationException(PurchaseCostTransitionPolicy.ProtectedMovementMessage(found.MovementId));
    }

    private async Task RebuildAffectedAsync(IEnumerable<(long ProductId, DateTime ChangedAt)> affected, CancellationToken cancellationToken)
    {
        foreach (var product in affected.GroupBy(x => x.ProductId)
                     .Select(group => (ProductId: group.Key, ChangedAt: group.Min(x => x.ChangedAt))))
            await _rebuild.RebuildAsync(product.ProductId, product.ChangedAt, cancellationToken: cancellationToken);
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync()
    {
        return _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
    }

    private static PurchaseItemCandidate ToCandidate(PurchaseItemInput item) => new(item.ProductId, item.Quantity, item.UnitCost);

    private static void AddAffected(IDictionary<long, DateTime> affected, long productId, DateTime changedAt)
    {
        if (!affected.TryGetValue(productId, out var existing) || changedAt < existing)
            affected[productId] = changedAt;
    }

    private static StockAdjustment CreatePurchaseMovement(PurchaseItem item, DateTime purchaseDate) => new()
    {
        ProductId = item.ProductId,
        ReceiptItemId = item.Id,
        QuantityChange = PurchaseStockMovementPolicy.ToStockQuantity(item.Quantity),
        Reason = StockAdjustmentReason.Restock,
        UnitCost = item.UnitCost,
        TotalCost = PurchaseStockMovementPolicy.TotalCost(item.Quantity, item.UnitCost),
        Notes = PurchaseStockMovementPolicy.RestockMovementNotes,
        EffectiveAt = purchaseDate,
    };

    private static PurchaseRecord ToRecord(Purchase purchase) => new(
        purchase.Id,
        purchase.BusinessId,
        purchase.Title,
        purchase.Notes,
        purchase.TotalAmount,
        purchase.DeliveryCost,
        purchase.PackageCost,
        purchase.PurchaseDate,
        purchase.SupplierId,
        purchase.Supplier is null ? null : ToSupplierRecord(purchase.Supplier),
        purchase.Items.Select(ToItemRecord).ToList(),
        purchase.FileName,
        purchase.StoredFileName,
        purchase.ContentType,
        purchase.FileSizeBytes,
        purchase.CreatedAt);

    private static PurchaseSupplierRecord ToSupplierRecord(Supplier supplier) => new(
        supplier.Id, supplier.Name, supplier.ContactName, supplier.Phone, supplier.Email, supplier.Address);

    private static PurchaseItemRecord ToItemRecord(PurchaseItem item) => new(
        item.Id, item.ReceiptId, item.ProductId, item.Quantity, item.UnitCost,
        item.Product is null ? null : ToProductSummary(item.Product));

    private static PurchaseProductSummaryRecord ToProductSummary(Product product) => new(
        product.Id, product.Name, product.Sku, product.Description, product.UnitPrice, product.AverageUnitCost,
        product.CostingQuantity, product.InventoryValue, product.QuantityInStock, product.LowStockThreshold,
        product.RestockTo, product.Unit, product.IsActive, product.CreatedAt, product.UpdatedAt,
        product.CategoryId, product.SupplierId);
}
