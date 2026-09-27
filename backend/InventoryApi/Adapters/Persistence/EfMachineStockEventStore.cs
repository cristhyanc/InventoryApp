using Inventory.Application.MachineStockSync;
using Inventory.Domain.Nayax;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IMachineStockEventStore"/> (issue #183). It lives
/// in InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>,
/// the persistence models, and the existing inventory/costing movement services, all of which still
/// live in InventoryApi. Move it into Inventory.Infrastructure once the shared AppDbContext and
/// persistence models relocate there; this follows the same pattern as <see cref="EfSupplierStore"/>.
///
/// Applying a refill deliberately reuses <see cref="IInventoryCostService.ApplyMovement"/> rather
/// than writing its own movement, so a Nayax-sourced <see cref="StockAdjustmentReason.MachineRefill"/>
/// inherits exactly the established internal-transfer invariants: it reduces storage quantity and
/// never touches costing quantity/value or creates COGS.
/// </summary>
public sealed class EfMachineStockEventStore : IMachineStockEventStore
{
    private readonly AppDbContext _db;
    private readonly IInventoryCostService _costing;
    private readonly IInventoryCostRebuildService _rebuild;

    public EfMachineStockEventStore(
        AppDbContext db, IInventoryCostService costing, IInventoryCostRebuildService rebuild)
    {
        _db = db;
        _costing = costing;
        _rebuild = rebuild;
    }

    public async Task<IReadOnlyList<long>> GetImportedNayaxEventIdsAsync(
        long machineId, CancellationToken cancellationToken) =>
        await _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(e => e.MachineId == machineId)
            .Select(e => e.NayaxEventId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<long, NayaxStockSyncProduct>> GetStorageProductsAsync(
        IReadOnlyCollection<long> nayaxProductIds, CancellationToken cancellationToken)
    {
        var products = await _db.Products
            .AsNoTracking()
            .Where(p => nayaxProductIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.QuantityInStock })
            .ToListAsync(cancellationToken);

        return products.ToDictionary(
            p => p.Id,
            p => new NayaxStockSyncProduct(p.Id, p.Name, p.QuantityInStock));
    }

    public async Task ImportAsync(
        IReadOnlyList<MachineStockEventImport> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
            return;

        // BusinessId is deliberately left unset: BusinessOwnershipEnforcer stamps the caller's
        // resolved business on SaveChanges, and the (BusinessId, NayaxEventId) unique index is what
        // makes re-importing the same alert impossible.
        foreach (var imported in events)
        {
            _db.NayaxMachineStockEvents.Add(new NayaxMachineStockEvent
            {
                NayaxEventId = imported.NayaxEventId,
                MachineId = imported.MachineId,
                EventCode = imported.EventCode,
                EventTimestamp = imported.EventTimestamp,
                RawEventData = imported.RawEventData,
                RawSourceMetadata = imported.RawSourceMetadata,
                ParsedMdb = imported.ParsedMdb,
                ParsedProductName = imported.ParsedProductName,
                ParsedQuantity = imported.ParsedQuantity,
                MatchedProductId = imported.MatchedProductId,
                MatchStatus = imported.MatchStatus,
                NeedsReviewReason = imported.NeedsReviewReason,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingMachineStockEvent>> GetUnprocessedEventsAsync(
        long machineId, CancellationToken cancellationToken) =>
        await _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(e => e.MachineId == machineId
                && e.ProcessingStatus == NayaxStockEventProcessingStatus.Unprocessed)
            .OrderBy(e => e.EventTimestamp)
            .ThenBy(e => e.Id)
            .Select(e => new PendingMachineStockEvent(
                e.Id,
                e.NayaxEventId,
                e.MachineId,
                e.EventTimestamp,
                e.RawEventData,
                e.ParsedMdb,
                e.ParsedProductName,
                e.ParsedQuantity,
                e.MatchedProductId,
                e.MatchedProduct != null ? e.MatchedProduct.Name : null,
                e.MatchedProduct != null ? (int?)e.MatchedProduct.QuantityInStock : null,
                e.MatchStatus,
                e.NeedsReviewReason,
                e.ProcessingStatus))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ManualRefillEvidence>> GetManualMachineRefillsAsync(
        long machineId, CancellationToken cancellationToken)
    {
        var refills = await _db.StockAdjustments
            .AsNoTracking()
            .Where(sa => sa.MachineId == machineId
                && sa.Reason == StockAdjustmentReason.MachineRefill
                && sa.Source == StockAdjustmentSource.Manual)
            .Select(sa => new { sa.ProductId, sa.QuantityChange, sa.EffectiveAt })
            .ToListAsync(cancellationToken);

        return refills
            .Select(sa => new ManualRefillEvidence(sa.ProductId, sa.QuantityChange, sa.EffectiveAt))
            .ToList();
    }

    public async Task<MachineStockEventState?> FindEventAsync(
        long machineId, int eventId, CancellationToken cancellationToken) =>
        await _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(e => e.Id == eventId && e.MachineId == machineId)
            .Select(e => new MachineStockEventState(
                e.Id,
                e.NayaxEventId,
                e.MatchStatus,
                e.NeedsReviewReason,
                e.ProcessingStatus,
                e.MatchedProductId,
                e.ParsedQuantity,
                e.StockAdjustmentId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<NayaxStockSyncProduct?> FindStorageProductAsync(
        long productId, CancellationToken cancellationToken)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new { p.Id, p.Name, p.QuantityInStock })
            .FirstOrDefaultAsync(cancellationToken);

        return product is null ? null : new NayaxStockSyncProduct(product.Id, product.Name, product.QuantityInStock);
    }

    public async Task<MachineRefillApplication> ApplyRefillAsync(
        int eventId,
        long machineId,
        long nayaxEventId,
        long productId,
        int quantity,
        CancellationToken cancellationToken)
    {
        var entity = await _db.NayaxMachineStockEvents
            .FirstOrDefaultAsync(e => e.Id == eventId && e.MachineId == machineId, cancellationToken);
        if (entity is null)
            return new MachineRefillApplication(false, null);

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var adjustment = _costing.ApplyMovement(
                productId, -quantity, StockAdjustmentReason.MachineRefill, null,
                $"Nayax Sync Restock (event {nayaxEventId})");
            adjustment.MachineId = machineId;
            adjustment.Source = StockAdjustmentSource.Nayax;

            await _db.SaveChangesAsync(cancellationToken);

            entity.ProcessingStatus = NayaxStockEventProcessingStatus.Applied;
            entity.ProcessedAt = DateTime.UtcNow;
            entity.StockAdjustmentId = adjustment.Id;

            await _rebuild.RebuildAsync(productId, cancellationToken: cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            return new MachineRefillApplication(true, adjustment.Id);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return new MachineRefillApplication(false, null);
        }
    }
}
