using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// The machine-level Sync Restock import and preview use case (issue #183): fetches the machine's
/// Nayax stock-adjustment alerts through the <see cref="INayaxLynxClient"/> port, parses and
/// resolves the ones not imported yet with the deterministic Domain policies, persists them as
/// imported facts through <see cref="IMachineStockEventStore"/>, and returns a reconciliation
/// preview. It never changes storage inventory itself, never writes to Nayax, and never redefines
/// Nayax PAR as physical capacity.
/// </summary>
public sealed class SyncMachineStockFromNayax
{
    private readonly INayaxLynxClient _nayax;
    private readonly IMachineStockEventStore _store;

    public SyncMachineStockFromNayax(INayaxLynxClient nayax, IMachineStockEventStore store)
    {
        _nayax = nayax;
        _store = store;
    }

    public async Task<NayaxMachineStockSyncPreviewDto> Handle(long machineId, CancellationToken cancellationToken)
    {
        var alerts = await _nayax.GetMachineLastAlertsAsync(machineId, cancellationToken);
        var stockAlerts = alerts
            .Where(alert => alert.EventCode == NayaxMachineAlertEventCodes.StockAdjustForMachine)
            .GroupBy(alert => alert.EventLogId)
            .Select(group => group.First())
            .ToList();

        var alreadyImported = (await _store.GetImportedNayaxEventLogIdsAsync(machineId, cancellationToken)).ToHashSet();
        var newAlerts = stockAlerts.Where(alert => !alreadyImported.Contains(alert.EventLogId)).ToList();

        if (newAlerts.Count > 0)
        {
            // Machine + MDB is the primary mapping, read from the same live machine-products
            // projection the machine detail view already uses.
            var machineProducts = await _nayax.GetMachineProductsAsync(machineId, cancellationToken);
            var nayaxProductIdByMdb = machineProducts
                .Where(product => product.MDBCode.HasValue && product.NayaxProductID.HasValue)
                .GroupBy(product => product.MDBCode!.Value)
                .ToDictionary(group => group.Key, group => group.First().NayaxProductID!.Value);

            var localProducts = await _store.GetStorageProductsAsync(
                nayaxProductIdByMdb.Values.Distinct().ToArray(), cancellationToken);

            await _store.ImportAsync(
                newAlerts.Select(alert => ToImport(machineId, alert, nayaxProductIdByMdb, localProducts)).ToList(),
                cancellationToken);
        }

        return await BuildPreviewAsync(machineId, newAlerts.Count, cancellationToken);
    }

    private static MachineStockEventImport ToImport(
        long machineId,
        NayaxMachineAlert alert,
        IReadOnlyDictionary<int, long> nayaxProductIdByMdb,
        IReadOnlyDictionary<long, NayaxStockSyncProduct> localProducts)
    {
        if (!NayaxStockAdjustmentEventParser.TryParse(alert.EventData, out var parsed, out var failureReason))
        {
            var unparsed = NayaxMachineStockMatchPolicy.Unparsed(failureReason);
            return NewImport(machineId, alert, null, null, null, unparsed);
        }

        var resolution = NayaxMachineStockMatchPolicy.Resolve(machineId, parsed, nayaxProductIdByMdb, localProducts);
        return NewImport(machineId, alert, parsed.Mdb, parsed.ProductName, parsed.SignedQuantity, resolution);
    }

    private static MachineStockEventImport NewImport(
        long machineId,
        NayaxMachineAlert alert,
        int? parsedMdb,
        string? parsedProductName,
        int? parsedQuantity,
        NayaxStockEventResolution resolution) =>
        new(
            alert.EventLogId,
            machineId,
            alert.EventCode,
            AsUtc(alert.EventDateTimeGmt),
            alert.EventDateTimeVmc,
            alert.EventData ?? string.Empty,
            JsonSerializer.Serialize(alert),
            parsedMdb,
            parsedProductName,
            parsedQuantity,
            resolution.MatchedProductId,
            resolution.MatchStatus,
            resolution.NeedsReviewReason);

    /// <summary>
    /// EventDateTimeGMT is documented as GMT; a value that arrives without an offset is therefore
    /// UTC, not server-local time.
    /// </summary>
    private static DateTime AsUtc(DateTime gmt) => gmt.Kind switch
    {
        DateTimeKind.Utc => gmt,
        DateTimeKind.Local => gmt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(gmt, DateTimeKind.Utc)
    };

    private async Task<NayaxMachineStockSyncPreviewDto> BuildPreviewAsync(
        long machineId, int newEventCount, CancellationToken cancellationToken)
    {
        var pendingEvents = await _store.GetUnprocessedEventsAsync(machineId, cancellationToken);
        var manualRefills = await _store.GetManualMachineRefillsAsync(machineId, cancellationToken);

        var events = pendingEvents.Select(pending => ToPreviewDto(pending, manualRefills)).ToList();

        var productImpacts = pendingEvents
            .Where(pending => pending.MatchStatus == NayaxStockEventMatchStatus.Matched
                && pending.ParsedQuantity is > 0
                && pending.MatchedProductId is not null
                && pending.MatchedProductQuantityInStock is not null
                && pending.DuplicateResolution != NayaxDuplicateResolution.ReconciledManually)
            .GroupBy(pending => (Id: pending.MatchedProductId!.Value,
                Name: pending.MatchedProductName ?? string.Empty,
                Available: pending.MatchedProductQuantityInStock!.Value))
            .Select(group => new NayaxProductImpactPreviewDto(
                group.Key.Id,
                group.Key.Name,
                group.Key.Available,
                group.Sum(pending => pending.ParsedQuantity!.Value)))
            .OrderBy(impact => impact.ProductName, StringComparer.Ordinal)
            .ToList();

        return new NayaxMachineStockSyncPreviewDto(
            machineId,
            newEventCount,
            events,
            productImpacts,
            events.Count == 0 ? "No new Nayax stock-adjustment alerts to review." : null);
    }

    private static NayaxStockEventPreviewDto ToPreviewDto(
        PendingMachineStockEvent pending, IReadOnlyList<ManualRefillEvidence> manualRefills)
    {
        var impact = NayaxMachineStockImpactPolicy.Evaluate(
            pending.MatchStatus, pending.ParsedQuantity, pending.MatchedProductQuantityInStock);

        var possibleDuplicate = impact.IsPositiveRefill
            ? NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
                pending.MatchedProductId!.Value, pending.ParsedQuantity!.Value, pending.EventDateTimeGmt, manualRefills)
            : null;

        return new NayaxStockEventPreviewDto(
            pending.Id,
            pending.NayaxEventLogId,
            pending.MachineId,
            pending.EventDateTimeGmt,
            pending.EventDateTimeVmc,
            pending.RawEventData,
            pending.ParsedMdb,
            pending.ParsedProductName,
            pending.ParsedQuantity,
            pending.MatchedProductId,
            pending.MatchedProductName,
            pending.MatchStatus,
            pending.NeedsReviewReason,
            pending.ProcessingStatus,
            pending.MatchedProductQuantityInStock,
            impact.IsInsufficientStorage,
            impact.UnaccountedDifference,
            impact.IsDiscrepancy,
            possibleDuplicate is not null,
            possibleDuplicate is not null
                ? $"A manual machine refill of the same quantity was recorded for this product/machine at {possibleDuplicate.Value.EffectiveAt:O}."
                : null,
            pending.DuplicateResolution);
    }
}
