using System.Linq.Expressions;
using Inventory.Application.Costing;
using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMachineStockEventStore"/> (issue #183). It lives in
/// Inventory.Infrastructure, following the same pattern as
/// <see cref="EfSupplierStore"/>: <see cref="AppDbContext"/> and the persistence models it depends
/// on moved there in issue #307, and this adapter family followed them in issue #309 (Persistence
/// 8/8 of #153).
///
/// Applying a refill deliberately reuses the Application <see cref="IRecordInventoryMovement"/> and
/// <see cref="IRebuildProductCost"/> use cases (issue #296) rather than writing its own movement,
/// so a Nayax-sourced <see cref="StockAdjustmentReason.MachineRefill"/> inherits exactly the established internal-transfer invariants: it reduces storage quantity and
/// never touches costing quantity/value or creates COGS.
/// </summary>
public sealed class EfMachineStockEventStore : IMachineStockEventStore
{
    /// <summary>
    /// The one projection of a stored event into the state an apply attempt is decided from, shared
    /// by the single-event and the whole-request reads.
    /// </summary>
    private static readonly Expression<Func<NayaxMachineStockEvent, MachineStockEventState>> EventState =
        e => new MachineStockEventState(
            e.Id,
            e.NayaxEventLogId,
            e.EventDateTimeGmt,
            e.MatchStatus,
            e.NeedsReviewReason,
            e.ProcessingStatus,
            e.MatchedProductId,
            e.ParsedQuantity,
            e.StockAdjustmentId,
            e.DuplicateResolution);

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
        long nayaxMachineId, CancellationToken cancellationToken) =>
        await _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(e => e.MachineId == nayaxMachineId)
            .Select(e => e.NayaxEventLogId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<long, NayaxStockSyncProduct>> GetStorageProductsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        var products = await _db.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
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
        long nayaxMachineId,
        CancellationToken cancellationToken,
        DateTime? fromDateGmt = null,
        bool includeReconciled = false)
    {
        var query = _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(e => e.MachineId == nayaxMachineId
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
        long localMachineId, CancellationToken cancellationToken)
    {
        var refills = await _db.StockAdjustments
            .AsNoTracking()
            .Where(sa => sa.MachineId == localMachineId
                && sa.Reason == StockAdjustmentReason.MachineRefill
                && sa.Source == StockAdjustmentSource.Manual)
            .Select(sa => new { sa.Id, sa.ProductId, sa.QuantityChange, sa.EffectiveAt })
            .ToListAsync(cancellationToken);

        return refills
            .Select(sa => new ManualRefillEvidence(sa.ProductId, sa.QuantityChange, sa.EffectiveAt, sa.Id))
            .ToList();
    }

    public async Task<MachineStockEventState?> FindEventAsync(
        long nayaxMachineId, int eventId, CancellationToken cancellationToken) =>
        await EventsOf(nayaxMachineId)
            .Where(e => e.Id == eventId)
            .Select(EventState)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<MachineStockEventState>> FindEventsAsync(
        long nayaxMachineId, IReadOnlyCollection<int> eventIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventIds);

        if (eventIds.Count == 0)
            return [];

        return await EventsOf(nayaxMachineId)
            .Where(e => eventIds.Contains(e.Id))
            .Select(EventState)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The one machine- and tenant-scoped event read both <see cref="FindEventAsync"/> and
    /// <see cref="FindEventsAsync"/> start from, so asking for a whole request's events cannot read
    /// anything a single-event read would not.
    /// </summary>
    private IQueryable<NayaxMachineStockEvent> EventsOf(long nayaxMachineId) =>
        _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(e => e.MachineId == nayaxMachineId);

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

    public async Task<IMachineStockRefillWriteTransaction> BeginRefillWriteTransactionAsync(
        CancellationToken cancellationToken) =>
        await MachineStockRefillWriteTransaction.BeginAsync(_db, cancellationToken).ConfigureAwait(false);

    public async Task<MachineRefillApplication> ApplyProductRefillsAsync(
        MachineIdentity machine,
        long productId,
        IReadOnlyList<MachineStockRefillRequest> refills,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(refills);

        if (refills.Count == 0)
            return new MachineRefillApplication(true, []);

        // The events are the provider's own rows, found by the provider's machine number; the
        // movements below are this application's and record the local machine (issue #741).
        var nayaxMachineId = machine.RequiredNayaxMachineId;

        try
        {
            // Every event row first, before a single movement is staged, so a batch naming an event
            // this machine does not have has staged nothing at all when it is refused.
            var events = new List<(MachineStockRefillRequest Refill, NayaxMachineStockEvent Event)>();
            foreach (var refill in refills)
            {
                var entity = await _db.NayaxMachineStockEvents.FirstOrDefaultAsync(
                    e => e.Id == refill.EventId && e.MachineId == nayaxMachineId, cancellationToken);
                if (entity is null)
                    return new MachineRefillApplication(false, []);

                events.Add((refill, entity));
            }

            var applied = new List<(MachineStockRefillRequest Refill, NayaxMachineStockEvent Event, IStagedInventoryMovement Movement)>();
            foreach (var (refill, entity) in events)
            {
                // Each event keeps its own movement, with its own EventLogID note: the batch is one
                // transaction and one rebuild, never one merged movement. Each one is staged against
                // the quantity the previous one left, exactly as separate saves left it.
                var movement = await _recordMovement.RecordAsync(
                    new InventoryMovement(
                        productId, -refill.Quantity, DomainStock.StockAdjustmentReason.MachineRefill,
                        $"Nayax Sync Restock (EventLogID {refill.NayaxEventLogId})",
                        MachineId: machine.LocalMachineId,
                        Source: DomainStock.StockAdjustmentSource.Nayax),
                    cancellationToken);

                applied.Add((refill, entity, movement));
            }

            // Saved once for the whole product, which is what gives every staged movement its id.
            await _db.SaveChangesAsync(cancellationToken);

            foreach (var (refill, entity, movement) in applied)
            {
                entity.ProcessingStatus = NayaxStockEventProcessingStatus.Applied;
                entity.ProcessedAt = DateTime.UtcNow;
                entity.StockAdjustmentId = movement.Id;

                if (refill.DuplicateResolution != NayaxDuplicateResolution.None)
                {
                    entity.DuplicateResolution = refill.DuplicateResolution;
                    entity.DuplicateResolvedAt = DateTime.UtcNow;
                    entity.MatchedManualStockAdjustmentId = refill.MatchedManualStockAdjustmentId;
                }
            }

            // Once for the product, however many of its events the batch applied (issue #1127): a
            // rebuild replays the product's whole movement and sale history, so repeating it per
            // event replayed the same history again for no new answer.
            await _rebuild.RebuildAsync(productId, cancellationToken: cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            return new MachineRefillApplication(
                true,
                [.. applied.Select(x => new AppliedMachineRefill(x.Refill.EventId, x.Movement.Id))]);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller owns the transaction, so its disposal is what discards these writes; what
            // is left here is the unit of work, which must not carry the failed batch any further.
            _db.ChangeTracker.Clear();
            return new MachineRefillApplication(false, []);
        }
    }

    public async Task<bool> ReconcileAsManualDuplicateAsync(
        int eventId, long nayaxMachineId, int? matchedManualStockAdjustmentId, CancellationToken cancellationToken)
    {
        var entity = await _db.NayaxMachineStockEvents
            .FirstOrDefaultAsync(e => e.Id == eventId && e.MachineId == nayaxMachineId, cancellationToken);
        if (entity is null)
            return false;

        entity.DuplicateResolution = NayaxDuplicateResolution.ReconciledManually;
        entity.DuplicateResolvedAt = DateTime.UtcNow;
        entity.MatchedManualStockAdjustmentId = matchedManualStockAdjustmentId;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
