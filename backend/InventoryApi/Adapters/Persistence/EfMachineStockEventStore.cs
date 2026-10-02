using Inventory.Application.Costing;
using Inventory.Application.MachineStockSync;
using Inventory.Domain.Nayax;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IMachineStockEventStore"/> (issue #183). It lives
/// in InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>
/// and the persistence models, both of which still live in InventoryApi. Move it into
/// Inventory.Infrastructure once the shared AppDbContext and persistence models relocate there;
/// this follows the same pattern as <see cref="EfSupplierStore"/>.
///
/// Applying a refill deliberately reuses the Application <see cref="IRecordInventoryMovement"/> and
/// <see cref="IRebuildProductCost"/> use cases (issue #296) rather than writing its own movement,
/// so a Nayax-sourced <see cref="StockAdjustmentReason.MachineRefill"/> inherits exactly the established internal-transfer invariants: it reduces storage quantity and
/// never touches costing quantity/value or creates COGS.
/// </summary>
public sealed class EfMachineStockEventStore : IMachineStockEventStore
{
    private readonly AppDbContext _db;
    private readonly IRecordInventoryMovement _recordMovement;
    private readonly IRebuildProductCost _rebuild;

    public EfMachineStockEventStore(
        AppDbContext db, IRecordInventoryMovement recordMovement, IRebuildProductCost rebuild)
    {
        _db = db;
        _recordMovement = recordMovement;
        _rebuild = rebuild;
    }

    public async Task<IReadOnlyList<long>> GetImportedNayaxEventLogIdsAsync(
        long machineId, CancellationToken cancellationToken) =>
        await _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(e => e.MachineId == machineId)
            .Select(e => e.NayaxEventLogId)
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
        // resolved business on SaveChanges, and the (BusinessId, NayaxEventLogId) unique index is what
        // makes re-importing the same alert impossible.
        foreach (var imported in events)
        {
            _db.NayaxMachineStockEvents.Add(new NayaxMachineStockEvent
            {
                NayaxEventLogId = imported.NayaxEventLogId,
                MachineId = imported.MachineId,
                EventCode = imported.EventCode,
                EventDateTimeGmt = imported.EventDateTimeGmt,
                EventDateTimeVmc = imported.EventDateTimeVmc,
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

    public async Task<MachineStockEventsPage> GetUnprocessedEventsAsync(
        long machineId,
        CancellationToken cancellationToken,
        DateTime? fromDateGmt = null,
        bool includeReconciled = false)
    {
        var query = _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(e => e.MachineId == machineId
                && e.ProcessingStatus == NayaxStockEventProcessingStatus.Unprocessed);

        if (fromDateGmt is DateTime from)
            query = query.Where(e => e.EventDateTimeGmt >= from);

        var hiddenReconciledCount = includeReconciled
            ? 0
            : await query.CountAsync(e => e.DuplicateResolution == NayaxDuplicateResolution.ReconciledManually, cancellationToken);

        if (!includeReconciled)
            query = query.Where(e => e.DuplicateResolution != NayaxDuplicateResolution.ReconciledManually);

        var events = await query
            .OrderBy(e => e.EventDateTimeGmt)
            .ThenBy(e => e.Id)
            .Select(e => new PendingMachineStockEvent(
                e.Id,
                e.NayaxEventLogId,
                e.MachineId,
                e.EventDateTimeGmt,
                e.EventDateTimeVmc,
                e.RawEventData,
                e.ParsedMdb,
                e.ParsedProductName,
                e.ParsedQuantity,
                e.MatchedProductId,
                e.MatchedProduct != null ? e.MatchedProduct.Name : null,
                e.MatchedProduct != null ? (int?)e.MatchedProduct.QuantityInStock : null,
                e.MatchStatus,
                e.NeedsReviewReason,
                e.ProcessingStatus,
                e.DuplicateResolution))
            .ToListAsync(cancellationToken);

        return new MachineStockEventsPage(events, hiddenReconciledCount);
    }

    public async Task<IReadOnlyList<ManualRefillEvidence>> GetManualMachineRefillsAsync(
        long machineId, CancellationToken cancellationToken)
    {
        var refills = await _db.StockAdjustments
            .AsNoTracking()
            .Where(sa => sa.MachineId == machineId
                && sa.Reason == StockAdjustmentReason.MachineRefill
                && sa.Source == StockAdjustmentSource.Manual)
            .Select(sa => new { sa.Id, sa.ProductId, sa.QuantityChange, sa.EffectiveAt })
            .ToListAsync(cancellationToken);

        return refills
            .Select(sa => new ManualRefillEvidence(sa.ProductId, sa.QuantityChange, sa.EffectiveAt, sa.Id))
            .ToList();
    }

    public async Task<MachineStockEventState?> FindEventAsync(
        long machineId, int eventId, CancellationToken cancellationToken) =>
        await _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(e => e.Id == eventId && e.MachineId == machineId)
            .Select(e => new MachineStockEventState(
                e.Id,
                e.NayaxEventLogId,
                e.EventDateTimeGmt,
                e.MatchStatus,
                e.NeedsReviewReason,
                e.ProcessingStatus,
                e.MatchedProductId,
                e.ParsedQuantity,
                e.StockAdjustmentId,
                e.DuplicateResolution))
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
        long nayaxEventLogId,
        long productId,
        int quantity,
        NayaxDuplicateResolution duplicateResolution,
        int? matchedManualStockAdjustmentId,
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
            var adjustment = await _recordMovement.RecordAsync(
                new InventoryMovement(
                    productId, -quantity, DomainStock.StockAdjustmentReason.MachineRefill,
                    $"Nayax Sync Restock (EventLogID {nayaxEventLogId})",
                    MachineId: machineId,
                    Source: DomainStock.StockAdjustmentSource.Nayax),
                cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);

            entity.ProcessingStatus = NayaxStockEventProcessingStatus.Applied;
            entity.ProcessedAt = DateTime.UtcNow;
            entity.StockAdjustmentId = adjustment.Id;

            if (duplicateResolution != NayaxDuplicateResolution.None)
            {
                entity.DuplicateResolution = duplicateResolution;
                entity.DuplicateResolvedAt = DateTime.UtcNow;
                entity.MatchedManualStockAdjustmentId = matchedManualStockAdjustmentId;
            }

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

    public async Task<bool> ReconcileAsManualDuplicateAsync(
        int eventId, long machineId, int? matchedManualStockAdjustmentId, CancellationToken cancellationToken)
    {
        var entity = await _db.NayaxMachineStockEvents
            .FirstOrDefaultAsync(e => e.Id == eventId && e.MachineId == machineId, cancellationToken);
        if (entity is null)
            return false;

        entity.DuplicateResolution = NayaxDuplicateResolution.ReconciledManually;
        entity.DuplicateResolvedAt = DateTime.UtcNow;
        entity.MatchedManualStockAdjustmentId = matchedManualStockAdjustmentId;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
