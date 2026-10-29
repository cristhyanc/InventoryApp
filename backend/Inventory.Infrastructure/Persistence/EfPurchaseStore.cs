using Inventory.Application.Costing;
using Inventory.Application.Purchases;
using Inventory.Application.SupplierOrders;
using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
using Inventory.Domain.SupplierOrders;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
// Inventory.Domain.SupplierOrders declares its own SupplierOrderStatus, so the persisted enum the
// EF query compares against is named explicitly rather than relying on which using wins, and the
// Domain enum the policies speak is named through its own alias.
using DomainStatus = Inventory.Domain.SupplierOrders.SupplierOrderStatus;
using PersistenceModels = Inventory.Infrastructure.Models;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPurchaseStore"/>. It lives in
/// Inventory.Infrastructure, following the same precedent as
/// <see cref="EfOperatingExpenseStore"/>/<see cref="EfProductStore"/>: <see cref="AppDbContext"/>
/// and the persistence models it depends on moved there in issue #307, and this adapter family
/// followed them in issue #309 (Persistence 8/8 of #153).
///
/// Its multi-step writes - the purchase/supplier-order-fulfillment/stock-movement transaction in
/// <see cref="CreateAsync"/>/<see cref="UpdateAsync"/>/<see cref="DeleteAsync"/> - stay here rather
/// than being decomposed into Application-level orchestration, per the issue's target ownership:
/// EF queries, transactions, receipt allocations, stock-movement persistence, and costing-rebuild
/// calls are Infrastructure/adapter concerns. The
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
    private readonly IRebuildProductCost _rebuild;

    public EfPurchaseStore(AppDbContext db, IRebuildProductCost rebuild)
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

    public async Task<(long ProductId, string? ProductName, DateTime CutoffAt)?> FindConflictingCostTransitionBaselineAsync(
        IReadOnlyCollection<long> productIds, DateTime purchaseDate, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0) return null;
        var cutoffsByProduct = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => productIds.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, cancellationToken);
        if (PurchaseCostTransitionPolicy.FindConflictingBaseline(productIds, purchaseDate, cutoffsByProduct)
            is not { } conflict)
        {
            return null;
        }

        // Only the refused product's name, and only on the refusal path, so the ordinary write is
        // unchanged (issue #833). Which product conflicts is still decided by the policy above.
        var productName = await _db.Products.AsNoTracking()
            .Where(product => product.Id == conflict.ProductId)
            .Select(product => product.Name)
            .FirstOrDefaultAsync(cancellationToken);
        return (conflict.ProductId, productName, conflict.CutoffAt);
    }

    public async Task<PurchaseRecord> CreateAsync(
        PurchaseFields fields,
        IReadOnlyList<PurchaseItemInput> items,
        PurchaseFileMetadata file,
        SupplierOrderCompletionDecision? supplierOrderCompletion,
        CancellationToken cancellationToken)
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
        ApplyChargeClassifications(purchase, fields, GstClassificationState.Unclassified, GstClassificationState.Unclassified);
        purchase.Items = items.Select(x =>
        {
            var item = new PurchaseItem
            {
                ProductId = x.ProductId,
                Quantity = x.Quantity,
                UnitCost = x.UnitCost,
            };
            ApplyItemClassification(item, x.GstClassification);
            return item;
        }).ToList();

        // BEGIN IMMEDIATE when a completion decision rides along (issue #974), the deferred
        // transaction this write has always used otherwise. The decision's authoritative read, its
        // fingerprint comparison and the status write have to be one operation against a competing
        // receipt, and a deferred transaction takes SQLite's write lock only at the first write -
        // which is after that read. An ordinary purchase save is unchanged.
        await using var writeLock = supplierOrderCompletion is null
            ? null
            : await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);
        await using var transaction = supplierOrderCompletion is null ? await BeginTransactionAsync() : null;

        // Read, and refused, before anything at all is written: a stale decision must leave the
        // purchase unsaved rather than roll a committed-looking one back.
        var completionTarget = supplierOrderCompletion is null
            ? null
            : await ReadCompletionTargetAsync(supplierOrderCompletion, cancellationToken);

        _db.Receipts.Add(purchase);
        await _db.SaveChangesAsync(cancellationToken);
        await AllocateSupplierOrderFulfillmentAsync(purchase, cancellationToken);
        var physicalChanges = new Dictionary<long, int>();
        foreach (var item in purchase.Items)
        {
            var movement = CreatePurchaseMovement(item, purchase.PurchaseDate);
            _db.StockAdjustments.Add(movement);
            AddPhysicalChange(physicalChanges, item.ProductId, movement.QuantityChange);
        }
        await StagePhysicalStockAsync(physicalChanges, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await RebuildAffectedAsync(purchase.Items.Select(item => (item.ProductId, purchase.PurchaseDate)), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        // After the allocation and its status rollup, so the balance that is closed is the one this
        // purchase actually leaves, and still inside the same transaction.
        if (completionTarget is not null)
            await CompleteSupplierOrderAsync(completionTarget, supplierOrderCompletion!, cancellationToken);

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        if (writeLock is not null) await writeLock.CommitAsync(cancellationToken);

        return ToRecord(purchase);
    }

    /// <summary>
    /// Re-reads the order the completion decision names, inside the write transaction, and refuses
    /// the save unless it is still the order the decision was shown over (issue #974).
    ///
    /// <para>Every check is made from the stored rows: the order is reached through the ordinary
    /// tenant query filters, so one belonging to another business is simply not found; its
    /// eligibility is read from its own status; and the fingerprint is recomputed from its own
    /// lines' ordered and received quantities. The request contributes the id and a value to compare
    /// against - never a receipt total and never a status.</para>
    /// </summary>
    private async Task<SupplierOrder> ReadCompletionTargetAsync(
        SupplierOrderCompletionDecision decision, CancellationToken cancellationToken)
    {
        var order = await _db.SupplierOrders
            .Include(candidate => candidate.Lines)
            .FirstOrDefaultAsync(candidate => candidate.Id == decision.SupplierOrderId, cancellationToken)
            ?? throw new InvalidOperationException(SupplierOrderCompletionPolicy.UnknownOrderMessage);

        var status = (DomainStatus)order.Status;
        if (!SupplierOrderCompletionPolicy.IsEligibleForCompletion(status))
            throw new InvalidOperationException(SupplierOrderCompletionPolicy.NotEligibleMessage);

        var fingerprint = SupplierOrderCompletionFingerprint.Compute(
            order.BusinessId, order.Id, status, ToFulfillments(order));
        if (!string.Equals(fingerprint, decision.DecisionFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(SupplierOrderCompletionPolicy.StaleDecisionMessage);

        return order;
    }

    /// <summary>
    /// Closes the order's remaining undelivered balance, with the actor and instant the Application
    /// resolved. It writes nothing but the order's own status and that attribution: no quantity, no
    /// receipt, no stock movement and no cost, because none of those goods arrived.
    /// </summary>
    private async Task CompleteSupplierOrderAsync(
        SupplierOrder order, SupplierOrderCompletionDecision decision, CancellationToken cancellationToken)
    {
        var status = (DomainStatus)order.Status;

        // The rollup above may have moved the order on - a purchase that happened to complete it in
        // full leaves it Received - and a balance that no longer exists must not be "closed".
        if (!SupplierOrderCompletionPolicy.IsEligibleForCompletion(status))
            throw new InvalidOperationException(SupplierOrderCompletionPolicy.NoOutstandingBalanceMessage);

        var projected = SupplierOrderCompletionPolicy.Project(
            status, ToFulfillments(order), new Dictionary<int, decimal>());
        if (!SupplierOrderCompletionPolicy.HasOutstandingBalance(projected))
            throw new InvalidOperationException(SupplierOrderCompletionPolicy.NoOutstandingBalanceMessage);

        order.Status = (PersistenceModels.SupplierOrderStatus)SupplierOrderCompletionPolicy.CompletedStatus;
        order.CompletedWithUndeliveredAt = decision.DecidedAtUtc;
        order.CompletedByDirectoryTenantId = decision.DecidedByDirectoryTenantId;
        order.CompletedByObjectId = decision.DecidedByObjectId;
        order.UpdatedAt = decision.DecidedAtUtc;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<SupplierOrderLineFulfillment> ToFulfillments(SupplierOrder order) =>
        order.Lines
            .Select(line => new SupplierOrderLineFulfillment(line.Id, line.QuantityOrdered, line.QuantityReceived))
            .ToList();

    /// <summary>
    /// Refuses a correction that would rewrite or remove the receipts a completed supplier order was
    /// closed on (issue #974). Reopening such an order is out of scope, and the two alternatives are
    /// both worse: detaching its allocations would leave it recording deliveries no purchase holds,
    /// and re-allocating onto it would make a closed order accept a receipt. So the whole correction
    /// is refused, and the explicit decision stands.
    /// </summary>
    private async Task EnsureNoCompletedSupplierOrderAsync(
        IEnumerable<int> purchaseItemIds, CancellationToken cancellationToken)
    {
        var ids = purchaseItemIds.ToList();
        if (ids.Count == 0) return;

        var completedOrderIds = await _db.SupplierOrderReceiptAllocations
            .Where(allocation => ids.Contains(allocation.ReceiptItemId) &&
                allocation.SupplierOrderLine.SupplierOrder.Status ==
                    PersistenceModels.SupplierOrderStatus.ClosedPartiallyDelivered)
            .Select(allocation => allocation.SupplierOrderLine.SupplierOrderId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (completedOrderIds.Count > 0)
            throw new InvalidOperationException(
                SupplierOrderCompletionPolicy.LinkedToCompletedOrderMessage(completedOrderIds));
    }

    public async Task<PurchaseRecord?> UpdateAsync(
        int id, PurchaseFields fields, IReadOnlyList<PurchaseItemInput>? items, CancellationToken cancellationToken)
    {
        // The write lock first, before anything is read (issue #974). The completed-order guard below
        // is a check-then-write rule, and the write it protects is this whole correction: the guard
        // used to answer before the transaction opened at all, so a purchase save carrying a
        // completion decision could close the linked order in between - and the correction would then
        // rewrite the very receipts that order was closed on, while the rollup, which deliberately
        // preserves ClosedPartiallyDelivered, left it reading completed. This is the same lock the
        // completion path takes, which is what serialises the two against each other.
        await using var transaction = await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        var purchase = await _db.Receipts.Include(r => r.Supplier).Include(r => r.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (purchase is null) return null;

        if (fields.SupplierId.HasValue && !await SupplierExistsAsync(fields.SupplierId.Value, cancellationToken))
            return null;

        var originalPurchaseDate = purchase.PurchaseDate;
        var originalSupplierId = purchase.SupplierId;
        var originalItems = purchase.Items.ToList();

        // Decided before anything is written: a submission whose lines cannot be identified safely
        // must leave every persisted row untouched, which is now the transaction above rolling back
        // rather than there being no transaction yet.
        var lineMatch = items is null ? default : MatchSubmittedLines(originalItems, items);
        if (lineMatch.Error is { } identityError) throw new InvalidOperationException(identityError);

        // A purchase a completed supplier order was closed on cannot be corrected at all (issue
        // #974). Read with the write lock held, so no completion can commit between this answer and
        // the correction it permits; the refusal still writes nothing.
        await EnsureNoCompletedSupplierOrderAsync(originalItems.Select(item => item.Id), cancellationToken);

        var storedDeliveryGst = new GstClassificationState(
            purchase.DeliveryGstClassification, purchase.DeliveryGstClassificationSource);
        var storedPackageGst = new GstClassificationState(
            purchase.PackageGstClassification, purchase.PackageGstClassificationSource);
        purchase.Title = string.IsNullOrWhiteSpace(fields.Title) ? purchase.Title : fields.Title;
        purchase.Notes = fields.Notes;
        purchase.TotalAmount = fields.TotalAmount;
        purchase.DeliveryCost = fields.DeliveryCost;
        purchase.PackageCost = fields.PackageCost;
        ApplyChargeClassifications(purchase, fields, storedDeliveryGst, storedPackageGst);
        purchase.PurchaseDate = fields.PurchaseDate ?? purchase.PurchaseDate;
        purchase.SupplierId = fields.SupplierId;
        var requiresFulfillmentReconciliation = items is not null ||
            originalSupplierId != purchase.SupplierId || originalPurchaseDate != purchase.PurchaseDate;
        var affected = new Dictionary<long, DateTime>();
        var physicalChanges = new Dictionary<long, int>();

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
            var existingById = existingItems.ToDictionary(item => item.Id);
            var matchedItemIds = new HashSet<int>();
            var newItems = new List<PurchaseItem>();

            // The pairing of submitted line to stored line is the Domain policy's decision, taken
            // above over the lines as they were loaded; this only applies it.
            for (var index = 0; index < items.Count; index++)
            {
                var requested = items[index];
                if (lineMatch.StoredLineIds[index] is { } matchedId)
                {
                    var item = existingById[matchedId];
                    matchedItemIds.Add(matchedId);
                    movementByPurchaseItem.TryGetValue(item.Id, out var movement);
                    AddAffected(affected, item.ProductId, movement?.EffectiveAt ?? originalPurchaseDate);
                    AddAffected(affected, item.ProductId, purchase.PurchaseDate);
                    item.Quantity = requested.Quantity;
                    item.UnitCost = requested.UnitCost;
                    ApplyItemClassification(item, requested.GstClassification);
                    if (movement is not null)
                    {
                        var quantityChange = PurchaseStockMovementPolicy.ToStockQuantity(requested.Quantity);
                        AddPhysicalChange(physicalChanges, movement.ProductId, quantityChange - movement.QuantityChange);
                        movement.QuantityChange = quantityChange;
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
                    ApplyItemClassification(item, requested.GstClassification);
                    purchase.Items.Add(item);
                    newItems.Add(item);
                    AddAffected(affected, item.ProductId, purchase.PurchaseDate);
                }
            }

            foreach (var remaining in existingItems.Where(item => !matchedItemIds.Contains(item.Id)))
            {
                AddAffected(affected, remaining.ProductId,
                    movementByPurchaseItem.TryGetValue(remaining.Id, out var movement) ? movement.EffectiveAt : originalPurchaseDate);
                if (movement is not null)
                {
                    AddPhysicalChange(physicalChanges, movement.ProductId, -movement.QuantityChange);
                    _db.StockAdjustments.Remove(movement);
                }
                _db.ReceiptItems.Remove(remaining);
                purchase.Items.Remove(remaining);
            }

            await _db.SaveChangesAsync(cancellationToken);
            foreach (var item in newItems)
            {
                var movement = CreatePurchaseMovement(item, purchase.PurchaseDate);
                _db.StockAdjustments.Add(movement);
                AddPhysicalChange(physicalChanges, item.ProductId, movement.QuantityChange);
            }
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

        await StagePhysicalStockAsync(physicalChanges, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        if (requiresFulfillmentReconciliation)
            await AllocateSupplierOrderFulfillmentAsync(purchase, cancellationToken);
        await RebuildAffectedAsync(affected.Select(item => (item.Key, item.Value)), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToRecord(purchase);
    }

    public async Task<string?> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        // The write lock first, for the reason UpdateAsync takes it there (issue #974): the
        // completed-order guard below and the removal it permits have to be one operation against a
        // purchase save that is completing the very order this purchase delivered. A delete is the
        // worse half - it would leave a completed order recording a delivery no purchase holds.
        await using var transaction = await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        var purchase = await _db.Receipts.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (purchase is null) return null;

        var storedFileName = purchase.StoredFileName;
        await EnsureNoCompletedSupplierOrderAsync(purchase.Items.Select(item => item.Id), cancellationToken);
        var movements = await _db.StockAdjustments
            .Where(movement => movement.ReceiptItemId.HasValue &&
                purchase.Items.Select(item => item.Id).Contains(movement.ReceiptItemId.Value))
            .ToListAsync(cancellationToken);
        await EnsureNoPreCutoffMovementsAsync(movements, cancellationToken);
        var affected = movements.Select(movement => (movement.ProductId, movement.EffectiveAt)).ToList();
        var physicalChanges = new Dictionary<long, int>();
        foreach (var movement in movements)
            AddPhysicalChange(physicalChanges, movement.ProductId, -movement.QuantityChange);

        await RemoveSupplierOrderFulfillmentAsync(purchase.Items.Select(item => item.Id), cancellationToken);
        _db.StockAdjustments.RemoveRange(movements);
        await StagePhysicalStockAsync(physicalChanges, cancellationToken);
        _db.Receipts.Remove(purchase);
        await _db.SaveChangesAsync(cancellationToken);
        await RebuildAffectedAsync(affected, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return storedFileName;
    }

    private async Task AllocateSupplierOrderFulfillmentAsync(Purchase purchase, CancellationToken cancellationToken)
    {
        if (!purchase.SupplierId.HasValue) return;

        // The same query the completion preview projects over (issue #974), so the balance the
        // operator was shown and the balance this write leaves are one calculation.
        var candidateLinesByProduct = await EfSupplierOrderFulfillmentQueries.CandidateLinesByProductAsync(
            _db,
            purchase.SupplierId,
            purchase.PurchaseDate,
            purchase.Items.Select(item => item.ProductId),
            cancellationToken);

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

            // The rollup is applied through the status-preserving overload (issue #974), which
            // leaves both of the statuses a person decides - Cancelled, and the explicit
            // completed-with-undelivered state - exactly as they are.
            order.Status = (PersistenceModels.SupplierOrderStatus)SupplierOrderStatusPolicy.Resolve(
                (DomainStatus)order.Status,
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
            throw new InvalidOperationException(
                PurchaseCostTransitionPolicy.DateBeforeCutoffMessage(found.CutoffAt, found.ProductId, found.ProductName));
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

    /// <summary>
    /// Moves each product's stored physical stock by exactly what this write did to its purchase
    /// movements, before the rebuild runs (issue #831). The other movement writers already stage the
    /// new quantity with the movement (<c>IRecordInventoryMovement</c>); without this, the rebuild of
    /// a product that has never been costed - no baseline, no costing quantity, no inventory value -
    /// compared its pre-write quantity with a history that already held the new movement, and
    /// refused a consistent product as <c>MissingOpening</c>. The guard itself is untouched: stock
    /// that did not reconcile before the write is still out by the same amount after it, and is still
    /// refused. For every other product the rebuild sets the replayed quantity anyway, so this changes
    /// nothing there.
    /// </summary>
    private async Task StagePhysicalStockAsync(IReadOnlyDictionary<long, int> physicalChanges, CancellationToken cancellationToken)
    {
        foreach (var (productId, change) in physicalChanges)
        {
            if (change == 0) continue;
            var product = await _db.Products.FirstOrDefaultAsync(candidate => candidate.Id == productId, cancellationToken)
                ?? throw new InvalidOperationException($"Product {productId} does not exist.");
            product.QuantityInStock = checked(product.QuantityInStock + change);
        }
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync()
    {
        return _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
    }

    private static PurchaseItemCandidate ToCandidate(PurchaseItemInput item) => new(item.ProductId, item.Quantity, item.UnitCost);

    /// <summary>
    /// Pairs each submitted line with the stored line it updates (issue #429), through
    /// <see cref="PurchaseLineIdentityPolicy"/>. The stored lines come from the purchase the tenant
    /// query filters already resolved, so a line id belonging to another purchase or another
    /// business is simply not among them and is reported as unknown.
    /// </summary>
    private static PurchaseLineMatch MatchSubmittedLines(
        IReadOnlyCollection<PurchaseItem> stored, IReadOnlyList<PurchaseItemInput> submitted) =>
        PurchaseLineIdentityPolicy.Match(
            stored
                .Select(item => new StoredPurchaseLine(
                    item.Id,
                    item.ProductId,
                    new GstClassificationState(item.GstClassification, item.GstClassificationSource)))
                .ToList(),
            submitted.Select(item => new SubmittedPurchaseLine(item.Id, item.ProductId)).ToList());

    /// <summary>
    /// Applies a submitted line GST classification (issue #429). A <c>null</c> submission keeps the
    /// classification the line already carries, so editing a purchase's quantities, costs or dates
    /// never silently reclassifies it; anything else goes through
    /// <see cref="PurchaseGstPolicy.Classify"/>, which records an explicit choice as
    /// <see cref="GstClassificationSource.Manual"/> and leaves an explicit unknown unclassified.
    /// </summary>
    private static void ApplyItemClassification(PurchaseItem item, GstClassification? requested)
    {
        if (requested is null) return;

        var state = PurchaseGstPolicy.Classify(requested);
        item.GstClassification = state.Classification;
        item.GstClassificationSource = state.Source;
    }

    /// <summary>
    /// Applies the submitted delivery and package GST classifications to <paramref name="purchase"/>,
    /// whose charge amounts have already been set. A <c>null</c> submission keeps the stored state
    /// (<paramref name="storedDelivery"/>/<paramref name="storedPackage"/>), and a charge that ends
    /// up absent or zero carries no classification at all, whatever was submitted or stored
    /// (parent issue #62, decision D3).
    /// </summary>
    private static void ApplyChargeClassifications(
        Purchase purchase,
        PurchaseFields fields,
        GstClassificationState storedDelivery,
        GstClassificationState storedPackage)
    {
        var delivery = PurchaseGstPolicy.ClassifyCharge(
            purchase.DeliveryCost, Requested(fields.DeliveryGstClassification, storedDelivery));
        purchase.DeliveryGstClassification = delivery.Classification;
        purchase.DeliveryGstClassificationSource = delivery.Source;

        var package = PurchaseGstPolicy.ClassifyCharge(
            purchase.PackageCost, Requested(fields.PackageGstClassification, storedPackage));
        purchase.PackageGstClassification = package.Classification;
        purchase.PackageGstClassificationSource = package.Source;

        static GstClassificationState Requested(GstClassification? submitted, GstClassificationState stored) =>
            submitted is null ? stored : PurchaseGstPolicy.Classify(submitted);
    }

    private static void AddAffected(IDictionary<long, DateTime> affected, long productId, DateTime changedAt)
    {
        if (!affected.TryGetValue(productId, out var existing) || changedAt < existing)
            affected[productId] = changedAt;
    }

    private static void AddPhysicalChange(Dictionary<long, int> physicalChanges, long productId, int change) =>
        physicalChanges[productId] = checked(physicalChanges.GetValueOrDefault(productId) + change);

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
        purchase.DeliveryGstClassification,
        purchase.DeliveryGstClassificationSource,
        purchase.PackageCost,
        purchase.PackageGstClassification,
        purchase.PackageGstClassificationSource,
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
        supplier.Id, supplier.Name, supplier.ContactName, supplier.Phone, supplier.Email, supplier.Address,
        supplier.Url);

    private static PurchaseItemRecord ToItemRecord(PurchaseItem item) => new(
        item.Id, item.ReceiptId, item.ProductId, item.Quantity, item.UnitCost,
        item.GstClassification, item.GstClassificationSource,
        item.Product is null ? null : ToProductSummary(item.Product));

    private static PurchaseProductSummaryRecord ToProductSummary(Product product) => new(
        product.Id, product.Name, product.Sku, product.Description, product.AverageUnitCost,
        product.CostingQuantity, product.InventoryValue, product.QuantityInStock, product.LowStockThreshold,
        product.RestockTo, product.Unit, product.IsActive, product.CreatedAt, product.UpdatedAt,
        product.CategoryId);
}
