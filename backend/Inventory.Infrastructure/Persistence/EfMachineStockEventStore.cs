using System.Linq.Expressions;
using Inventory.Application.Costing;
using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Domain.Nayax;
using Inventory.Domain.Reporting.ProductMatching;
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
            e.DuplicateResolution,
            e.ParsedMdb,
            e.ParsedProductName,
            e.EventCode);

    /// <summary>
    /// The one predicate for "this event is still waiting for a product match" (issue #1248),
    /// shared by the candidate read and the write, so the two cannot drift apart: a processed,
    /// reconciled or separately-applied event is never re-matched, because its match is the record
    /// of which product's storage actually moved, and an event whose EventData never parsed has no
    /// MDB and no product name for any mapping to resolve it by. It is the relational form of
    /// <see cref="RematchMachineStockEvents.IsWaitingForAMatch"/>.
    /// </summary>
    private static readonly Expression<Func<NayaxMachineStockEvent, bool>> WaitingForAMatch =
        e => e.EventCode == NayaxMachineAlertEventCodes.StockAdjustForMachine
            && e.ProcessingStatus == NayaxStockEventProcessingStatus.Unprocessed
            && e.DuplicateResolution == NayaxDuplicateResolution.None
            && e.MatchStatus != NayaxStockEventMatchStatus.Matched
            && e.ParsedMdb != null
            && e.ParsedProductName != null;

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

    public async Task<IReadOnlyList<ProductMatchCandidate>> GetActiveProductCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var products = await _db.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);

        return products.Select(p => new ProductMatchCandidate(p.Id, p.Name)).ToList();
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
                ParsedOutgoingProductName = imported.ParsedOutgoingProductName,
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
                e.DuplicateResolution,
                e.EventCode,
                e.ParsedOutgoingProductName))
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

    public async Task<IReadOnlyList<MachineStockEventState>> FindRematchCandidatesAsync(
        long nayaxMachineId, CancellationToken cancellationToken) =>
        await EventsOf(nayaxMachineId)
            .Where(WaitingForAMatch)
            .Select(EventState)
            .ToListAsync(cancellationToken);

    public async Task<int> UpdateMatchesAsync(
        long nayaxMachineId,
        IReadOnlyList<MachineStockEventMatchUpdate> updates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);

        if (updates.Count == 0)
            return 0;

        var refreshed = updates.ToDictionary(update => update.EventId);
        var ids = refreshed.Keys.ToList();

        // Tracked, machine-scoped, and filtered by the same "still waiting" condition the candidate
        // read uses: an event applied or resolved since that read is simply not among these rows,
        // so nothing can rewrite the match a committed movement was decided from.
        var rows = await _db.NayaxMachineStockEvents
            .Where(e => e.MachineId == nayaxMachineId && ids.Contains(e.Id))
            .Where(WaitingForAMatch)
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var row in rows)
        {
            var update = refreshed[row.Id];
            if (row.MatchStatus == update.MatchStatus
                && row.MatchedProductId == update.MatchedProductId
                && row.NeedsReviewReason == update.NeedsReviewReason)
            {
                continue;
            }

            row.MatchStatus = update.MatchStatus;
            row.MatchedProductId = update.MatchedProductId;
            row.NeedsReviewReason = update.NeedsReviewReason;
            changed++;
        }

        if (changed > 0)
        {
            // Inside the caller's transaction where there is one; the caller owns the commit.
            await _db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

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

    public async Task<NayaxStockSyncProduct?> FindActiveStorageProductAsync(
        long productId, CancellationToken cancellationToken)
    {
        // Tenant-filtered like every other read here, and additionally IsActive: a retired product,
        // another business's product and one that no longer exists all answer null, which is the
        // single refusal the replacement apply publishes for all three (issue #1326).
        var product = await _db.Products
            .AsNoTracking()
            .Where(p => p.Id == productId && p.IsActive)
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
                    return new MachineRefillApplication(
                        false, [], new MachineRefillFailure(MachineRefillFailure.EventNotFound, null));

                events.Add((refill, entity));
            }

            var applied = new List<(MachineStockRefillRequest Refill, NayaxMachineStockEvent Event, IStagedInventoryMovement Movement)>();
            foreach (var (refill, entity) in events)
            {
                // Each event keeps its own movement, with its own EventLogID note: the batch is one
                // transaction and one rebuild, never one merged movement. Each one is staged against
                // the quantity the previous one left, exactly as separate saves left it.
                //
                // The movement's own EffectiveAt stays the apply instant, which is what the cost
                // replay and Stock History read, so applying a late event cannot take units out of
                // storage at an earlier instant. The machine ledger needs the other instant - when
                // the machine was physically refilled - so the event's own EventDateTimeGMT travels
                // with the movement for it alone (issue #1243).
                var movement = await _recordMovement.RecordAsync(
                    new InventoryMovement(
                        productId, -refill.Quantity, DomainStock.StockAdjustmentReason.MachineRefill,
                        $"Nayax Sync Restock (EventLogID {refill.NayaxEventLogId})",
                        MachineId: machine.LocalMachineId,
                        Source: DomainStock.StockAdjustmentSource.Nayax,
                        MachineLedgerEffectiveAt: entity.EventDateTimeGmt),
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
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller owns the transaction, so its disposal is what discards these writes; what
            // is left here is the unit of work, which must not carry the failed batch any further.
            _db.ChangeTracker.Clear();

            // Why it failed, for the caller's log line and - for the one failure whose message is
            // written for an operator - for the result they see (issue #1240). Every other
            // exception contributes its type name alone: a provider's own message can quote the
            // statement or the data it failed on, which belongs in neither a log nor a response.
            return new MachineRefillApplication(
                false,
                [],
                new MachineRefillFailure(
                    exception.GetType().Name,
                    exception is InventoryCostDataQualityException refusal ? refusal.Message : null));
        }
    }

    public async Task<MachineReplacementApplication> ApplyProductReplacementAsync(
        MachineIdentity machine,
        MachineStockReplacementWrite replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(replacement);

        var nayaxMachineId = machine.RequiredNayaxMachineId;
        var choice = replacement.Choice;
        var plan = NayaxProductReplacementWritePolicy.Movements(choice);

        try
        {
            // The event row first, before a single movement is staged, so a request naming an event
            // this machine does not have has staged nothing at all when it is refused.
            var entity = await _db.NayaxMachineStockEvents.FirstOrDefaultAsync(
                e => e.Id == replacement.EventId && e.MachineId == nayaxMachineId, cancellationToken);
            if (entity is null)
                return new MachineReplacementApplication(
                    false, null, new MachineRefillFailure(MachineRefillFailure.EventNotFound, null));

            var note = $"Nayax Sync Restock product replacement (EventLogID {replacement.NayaxEventLogId})";

            // The units loaded, written exactly as a Sync Restock refill is: a negative storage
            // change through the one authoritative movement use case, costing-neutral, carrying the
            // event's own EventDateTimeGMT as the machine ledger's instant (issue #1243) while the
            // movement itself keeps the apply instant for the cost replay and Stock History.
            var incoming = plan.WritesIncomingRefill
                ? await _recordMovement.RecordAsync(
                    new InventoryMovement(
                        choice.IncomingProductId,
                        -choice.IncomingQuantity,
                        DomainStock.StockAdjustmentReason.MachineRefill,
                        $"{note} - loaded into the machine",
                        MachineId: machine.LocalMachineId,
                        Source: DomainStock.StockAdjustmentSource.Nayax,
                        MachineLedgerEffectiveAt: entity.EventDateTimeGmt),
                    cancellationToken)
                : null;

            // The units taken out come back to storage first - they are in the machine, so they are
            // in storage before anything can be written off them - as the same MachineRefill reason
            // with a positive storage change, which the machine ledger reads as its ReturnToStorage
            // movement and which is just as costing-neutral as a refill.
            var returned = plan.WritesOutgoingReturn
                ? await _recordMovement.RecordAsync(
                    new InventoryMovement(
                        choice.OutgoingProductId!.Value,
                        choice.OutgoingQuantity!.Value,
                        DomainStock.StockAdjustmentReason.MachineRefill,
                        $"{note} - taken out of the machine",
                        MachineId: machine.LocalMachineId,
                        Source: DomainStock.StockAdjustmentSource.Nayax,
                        MachineLedgerEffectiveAt: entity.EventDateTimeGmt),
                    cancellationToken)
                : null;

            // And only then the established write-off of exactly those units, so storage ends where
            // it started and the loss is costed once by the cost replay. It carries no machine-ledger
            // instant: it is not a refill, and the machine ledger reads only refills.
            var writtenOff = plan.WriteOffReason is { } writeOffReason
                ? await _recordMovement.RecordAsync(
                    new InventoryMovement(
                        choice.OutgoingProductId!.Value,
                        -choice.OutgoingQuantity!.Value,
                        writeOffReason,
                        $"{note} - {writeOffReason.ToString().ToLowerInvariant()} on removal from the machine",
                        MachineId: machine.LocalMachineId,
                        Source: DomainStock.StockAdjustmentSource.Nayax),
                    cancellationToken)
                : null;

            // Saved once for the whole replacement, which is what gives every staged movement its id.
            await _db.SaveChangesAsync(cancellationToken);

            entity.ProcessingStatus = NayaxStockEventProcessingStatus.Applied;
            entity.ProcessedAt = DateTime.UtcNow;
            entity.StockAdjustmentId = incoming?.Id;
            entity.ReplacementIncomingProductId = choice.IncomingProductId;
            entity.ReplacementIncomingQuantity = choice.IncomingQuantity;
            entity.ReplacementOutgoingDisposition = choice.OutgoingDisposition;
            entity.ReplacementOutgoingProductId = choice.OutgoingProductId;
            entity.ReplacementOutgoingQuantity = choice.OutgoingQuantity;
            entity.ReplacementOutgoingReturnStockAdjustmentId = returned?.Id;
            entity.ReplacementOutgoingWriteOffStockAdjustmentId = writtenOff?.Id;

            // Once per product whose stock actually moved, and never for a product that did not: a
            // rebuild replays that product's whole movement and sale history, and the two sides of
            // one replacement are legitimately the same product.
            foreach (var productId in MovedProducts(choice, plan))
            {
                await _rebuild.RebuildAsync(productId, cancellationToken: cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);

            return new MachineReplacementApplication(
                true,
                new AppliedProductReplacement(incoming?.Id, returned?.Id, writtenOff?.Id));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller owns the transaction, so its disposal is what discards these writes; what
            // is left here is the unit of work, which must not carry the failed replacement any
            // further. Why it failed is reported in exactly the shape a refill's failure is
            // (issue #1240): a type name, plus the one message written for an operator.
            _db.ChangeTracker.Clear();

            return new MachineReplacementApplication(
                false,
                null,
                new MachineRefillFailure(
                    exception.GetType().Name,
                    exception is InventoryCostDataQualityException refusal ? refusal.Message : null));
        }
    }

    /// <summary>
    /// The distinct products one replacement moved stock for, in a stable order. The incoming and
    /// the outgoing product may legitimately be the same one, and it is then rebuilt once.
    /// </summary>
    private static IEnumerable<long> MovedProducts(
        NayaxProductReplacementChoice choice, NayaxReplacementMovementPlan plan)
    {
        var moved = new List<long>(2);

        if (plan.WritesIncomingRefill)
            moved.Add(choice.IncomingProductId);

        if (plan.WritesOutgoingReturn && !moved.Contains(choice.OutgoingProductId!.Value))
            moved.Add(choice.OutgoingProductId.Value);

        return moved;
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
