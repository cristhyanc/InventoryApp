using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

/// <summary>
/// The machine-level Sync Restock workflow (issue #183). Nayax remains the source of truth for the
/// physical stock-adjustment fact; this service imports it, resolves it to a local product via
/// Machine + MDB, previews its storage impact, and applies only what an operator explicitly
/// accepts through the existing inventory/costing movement logic (<see cref="IInventoryCostService"/>).
/// It never writes machine stock back to Nayax and never redefines Nayax PAR as physical capacity.
/// </summary>
public class NayaxMachineStockSyncService : INayaxMachineStockSyncService
{
    /// <summary>
    /// A manual refill recorded within this window of a Nayax event for the same machine, product,
    /// and quantity is a plausible duplicate requiring human resolution, not an automatic double
    /// deduction (issue #183). This is a deliberately generous same-day-or-so window; it only ever
    /// produces an informational flag, never an automatic block or merge.
    /// </summary>
    internal static readonly TimeSpan DuplicateDetectionWindow = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayaxLynxClient;
    private readonly IInventoryCostService _costing;
    private readonly IInventoryCostRebuildService _rebuild;

    public NayaxMachineStockSyncService(
        AppDbContext db,
        INayaxLynxClient nayaxLynxClient,
        IInventoryCostService? costing = null,
        IInventoryCostRebuildService? rebuild = null)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
        _costing = costing ?? new InventoryCostService(db);
        _rebuild = rebuild ?? new InventoryCostRebuildService(db);
    }

    public async Task<NayaxMachineStockSyncPreviewDto> SyncAsync(long machineId, CancellationToken ct = default)
    {
        var alerts = await _nayaxLynxClient.GetMachineLastAlertsAsync(machineId, ct);
        var stockAlerts = alerts
            .Where(a => a.EventCode == NayaxMachineAlertEventCodes.StockAdjustForMachine)
            .GroupBy(a => a.EventID)
            .Select(g => g.First())
            .ToList();

        var existingEventIds = await _db.NayaxMachineStockEvents
            .Where(e => e.MachineId == machineId)
            .Select(e => e.NayaxEventId)
            .ToListAsync(ct);
        var existingEventIdSet = existingEventIds.ToHashSet();

        var newAlerts = stockAlerts.Where(a => !existingEventIdSet.Contains(a.EventID)).ToList();

        if (newAlerts.Count > 0)
        {
            var machineProducts = await _nayaxLynxClient.GetMachineProductsAsync(machineId, ct);
            var productsByMdb = machineProducts
                .Where(mp => mp.MDBCode.HasValue && mp.NayaxProductID.HasValue)
                .GroupBy(mp => mp.MDBCode!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            var localProducts = await _db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);

            foreach (var alert in newAlerts)
            {
                _db.NayaxMachineStockEvents.Add(BuildEvent(machineId, alert, productsByMdb, localProducts));
            }

            await _db.SaveChangesAsync(ct);
        }

        return await BuildPreviewAsync(machineId, newAlerts.Count, ct);
    }

    private static NayaxMachineStockEvent BuildEvent(
        long machineId,
        NayaxMachineAlert alert,
        IReadOnlyDictionary<int, NayaxMachineProduct> productsByMdb,
        IReadOnlyDictionary<long, Product> localProducts)
    {
        var evt = new NayaxMachineStockEvent
        {
            NayaxEventId = alert.EventID,
            MachineId = machineId,
            EventCode = alert.EventCode,
            EventTimestamp = alert.EventTimestamp,
            RawEventData = alert.EventData ?? string.Empty,
            RawSourceMetadata = alert.EventName,
        };

        if (!NayaxStockAdjustmentEventParser.TryParse(alert.EventData, out var parsed, out var failureReason))
        {
            evt.NeedsReviewReason = failureReason;
            return evt;
        }

        evt.ParsedMdb = parsed.Mdb;
        evt.ParsedProductName = parsed.ProductName;
        evt.ParsedQuantity = parsed.SignedQuantity;

        if (!productsByMdb.TryGetValue(parsed.Mdb, out var machineProduct))
        {
            evt.NeedsReviewReason = $"MDB {parsed.Mdb} is not a known position on machine {machineId}.";
            return evt;
        }

        if (!localProducts.TryGetValue(machineProduct.NayaxProductID!.Value, out var product))
        {
            evt.NeedsReviewReason =
                $"No local product is mapped to Nayax product {machineProduct.NayaxProductID} (MDB {parsed.Mdb}).";
            return evt;
        }

        evt.MatchedProductId = product.Id;

        if (!string.Equals(
                NayaxProductMatcher.NormalizeName(product.Name),
                NayaxProductMatcher.NormalizeName(parsed.ProductName),
                StringComparison.OrdinalIgnoreCase))
        {
            evt.NeedsReviewReason =
                $"Product name mismatch: Nayax reported '{parsed.ProductName}' but MDB {parsed.Mdb} is mapped to '{product.Name}'.";
            return evt;
        }

        evt.MatchStatus = NayaxStockEventMatchStatus.Matched;
        return evt;
    }

    private async Task<NayaxMachineStockSyncPreviewDto> BuildPreviewAsync(
        long machineId, int newEventCount, CancellationToken ct)
    {
        var pendingEvents = await _db.NayaxMachineStockEvents
            .Include(e => e.MatchedProduct)
            .Where(e => e.MachineId == machineId && e.ProcessingStatus == NayaxStockEventProcessingStatus.Unprocessed)
            .OrderBy(e => e.EventTimestamp)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var manualRefills = await _db.StockAdjustments
            .Where(sa => sa.MachineId == machineId &&
                         sa.Reason == StockAdjustmentReason.MachineRefill &&
                         sa.Source == StockAdjustmentSource.Manual)
            .ToListAsync(ct);

        var events = pendingEvents.Select(evt => ToPreviewDto(evt, manualRefills)).ToList();

        var productImpacts = pendingEvents
            .Where(e => e.MatchStatus == NayaxStockEventMatchStatus.Matched &&
                        e.ParsedQuantity is > 0 &&
                        e.MatchedProduct is not null)
            .GroupBy(e => e.MatchedProduct!)
            .Select(g => new NayaxProductImpactPreviewDto(
                g.Key.Id,
                g.Key.Name,
                g.Key.QuantityInStock,
                g.Sum(e => e.ParsedQuantity!.Value)))
            .OrderBy(p => p.ProductName)
            .ToList();

        return new NayaxMachineStockSyncPreviewDto(
            machineId,
            newEventCount,
            events,
            productImpacts,
            events.Count == 0 ? "No new Nayax stock-adjustment alerts to review." : null);
    }

    private static NayaxStockEventPreviewDto ToPreviewDto(
        NayaxMachineStockEvent evt, IReadOnlyList<StockAdjustment> manualRefills)
    {
        var available = evt.MatchedProduct?.QuantityInStock;
        var isPositive = evt.MatchStatus == NayaxStockEventMatchStatus.Matched && evt.ParsedQuantity is > 0;
        var isInsufficient = isPositive && available.HasValue && evt.ParsedQuantity!.Value > available.Value;

        var possibleDuplicate = isPositive
            ? manualRefills.FirstOrDefault(sa =>
                sa.ProductId == evt.MatchedProductId &&
                sa.QuantityChange == -evt.ParsedQuantity!.Value &&
                (sa.EffectiveAt - evt.EventTimestamp).Duration() <= DuplicateDetectionWindow)
            : null;

        return new NayaxStockEventPreviewDto(
            evt.Id,
            evt.NayaxEventId,
            evt.MachineId,
            evt.EventTimestamp,
            evt.RawEventData,
            evt.ParsedMdb,
            evt.ParsedProductName,
            evt.ParsedQuantity,
            evt.MatchedProductId,
            evt.MatchedProduct?.Name,
            evt.MatchStatus,
            evt.NeedsReviewReason,
            evt.ProcessingStatus,
            available,
            isInsufficient,
            isInsufficient ? evt.ParsedQuantity!.Value - available!.Value : null,
            evt.ParsedQuantity is < 0,
            possibleDuplicate is not null,
            possibleDuplicate is not null
                ? $"A manual machine refill of the same quantity was recorded for this product/machine at {possibleDuplicate.EffectiveAt:O}."
                : null);
    }

    public async Task<NayaxMachineStockApplyResponseDto> ApplyAsync(
        long machineId, IReadOnlyList<int> eventIds, CancellationToken ct = default)
    {
        var results = new List<NayaxStockEventApplyResultDto>();

        foreach (var eventId in eventIds.Distinct())
        {
            results.Add(await ApplyOneAsync(machineId, eventId, ct));
        }

        return new NayaxMachineStockApplyResponseDto(results);
    }

    private async Task<NayaxStockEventApplyResultDto> ApplyOneAsync(long machineId, int eventId, CancellationToken ct)
    {
        var evt = await _db.NayaxMachineStockEvents
            .FirstOrDefaultAsync(e => e.Id == eventId && e.MachineId == machineId, ct);

        if (evt is null)
            return new(eventId, NayaxStockEventApplyOutcome.Error, "Event not found for this machine.", null);

        if (evt.ProcessingStatus == NayaxStockEventProcessingStatus.Applied)
            return new(eventId, NayaxStockEventApplyOutcome.Applied, "Already applied.", evt.StockAdjustmentId);

        if (evt.MatchStatus != NayaxStockEventMatchStatus.Matched ||
            evt.MatchedProductId is not long productId ||
            evt.ParsedQuantity is not int quantity)
        {
            return new(eventId, NayaxStockEventApplyOutcome.NotMatched,
                evt.NeedsReviewReason ?? "Event needs review and cannot be applied.", null);
        }

        if (quantity <= 0)
        {
            return new(eventId, NayaxStockEventApplyOutcome.NotApplicable,
                "Negative adjustments are machine discrepancies, not refills, and are never applied automatically.",
                null);
        }

        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
            return new(eventId, NayaxStockEventApplyOutcome.Error, "Matched product no longer exists.", null);

        if (quantity > product.QuantityInStock)
        {
            return new(eventId, NayaxStockEventApplyOutcome.InsufficientStock,
                $"Nayax reported {quantity} loaded but only {product.QuantityInStock} is available in storage.",
                null);
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            var adjustment = _costing.ApplyMovement(
                productId, -quantity, StockAdjustmentReason.MachineRefill, null,
                $"Nayax Sync Restock (event {evt.NayaxEventId})");
            adjustment.MachineId = machineId;
            adjustment.Source = StockAdjustmentSource.Nayax;

            await _db.SaveChangesAsync(ct);

            evt.ProcessingStatus = NayaxStockEventProcessingStatus.Applied;
            evt.ProcessedAt = DateTime.UtcNow;
            evt.StockAdjustmentId = adjustment.Id;

            await _rebuild.RebuildAsync(productId, cancellationToken: ct);
            await _db.SaveChangesAsync(ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return new(eventId, NayaxStockEventApplyOutcome.Applied, "Applied.", adjustment.Id);
        }
        catch (Exception) when (ct.IsCancellationRequested == false)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return new(eventId, NayaxStockEventApplyOutcome.Error,
                "Failed to apply this event; no inventory movement was recorded.", null);
        }
    }
}
