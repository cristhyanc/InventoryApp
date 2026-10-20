using System.Text.Json;
using Inventory.Application.Machines;
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
///
/// The machine's MDB map still comes from the live machine-products read, but the
/// <c>NayaxProductID</c> each position reports is resolved to a local product through
/// <see cref="INayaxProductLinkStore"/> (issue #733) rather than used as a local product id. A
/// position whose Nayax product this business holds no link for resolves to no local product, which
/// is the state <c>NayaxMachineStockMatchPolicy</c> already reports as Needs Review ("no local
/// product is mapped to Nayax product ..."), so such an event still never moves inventory.
/// </summary>
public sealed class SyncMachineStockFromNayax
{
    private readonly INayaxLynxClient _nayax;
    private readonly IMachineStockEventStore _store;
    private readonly INayaxProductLinkStore _productLinks;

    public SyncMachineStockFromNayax(
        INayaxLynxClient nayax, IMachineStockEventStore store, INayaxProductLinkStore productLinks)
    {
        _nayax = nayax;
        _store = store;
        _productLinks = productLinks;
    }

    /// <summary>
    /// Imports new alerts and previews the machine's Sync Restock working list (issue #206).
    /// <paramref name="fromDateGmt"/>, when given, is a lower bound on the canonical
    /// <c>EventDateTimeGMT</c>; <c>null</c> shows the complete unprocessed history. <paramref
    /// name="includeReconciled"/> shows or hides events already reconciled as "already recorded
    /// manually"; the default hides them. Neither parameter changes what was imported or persisted.
    ///
    /// <para>Everything read from or stored for Nayax - the alerts, the machine's MDB map, the
    /// imported events - uses <paramref name="machine"/>'s Nayax number; the manual refills checked
    /// for duplicates are this application's movements and use its local id, which is also the
    /// machine id the preview reports (issue #741).</para>
    /// </summary>
    public async Task<NayaxMachineStockSyncPreviewDto> Handle(
        MachineIdentity machine,
        CancellationToken cancellationToken,
        DateTime? fromDateGmt = null,
        bool includeReconciled = false)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var machineId = machine.RequiredNayaxMachineId;
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

            // The Nayax ids the machine reported, resolved to the local products they name through
            // the link table; the storage read that follows is given local product ids only.
            var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
                nayaxProductIdByMdb.Values.Distinct().ToList(), cancellationToken);
            var storageProducts = await _store.GetStorageProductsAsync(
                localProductIdByNayaxId.Values.Distinct().ToList(), cancellationToken);

            // Re-keyed by Nayax id for the Domain policy, which resolves MDB -> Nayax product ->
            // local product. A Nayax id with no link, or whose linked product the storage read did
            // not return, is simply absent, which is the policy's "no local product is mapped"
            // branch.
            var localProducts = localProductIdByNayaxId
                .Where(link => storageProducts.ContainsKey(link.Value))
                .ToDictionary(link => link.Key, link => storageProducts[link.Value]);

            await _store.ImportAsync(
                newAlerts.Select(alert => ToImport(machineId, alert, nayaxProductIdByMdb, localProducts)).ToList(),
                cancellationToken);
        }

        return await BuildPreviewAsync(machine, newAlerts.Count, fromDateGmt, includeReconciled, cancellationToken);
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
        MachineIdentity machine,
        int newEventCount,
        DateTime? fromDateGmt,
        bool includeReconciled,
        CancellationToken cancellationToken)
    {
        var page = await _store.GetUnprocessedEventsAsync(
            machine.RequiredNayaxMachineId, cancellationToken, fromDateGmt, includeReconciled);
        var pendingEvents = page.Events;
        var manualRefills = await _store.GetManualMachineRefillsAsync(machine.LocalMachineId, cancellationToken);

        var events = pendingEvents
            .Select(pending => ToPreviewDto(machine.LocalMachineId, pending, manualRefills))
            .ToList();

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
            machine.LocalMachineId,
            newEventCount,
            events,
            productImpacts,
            page.HiddenReconciledCount,
            events.Count == 0 ? "No new Nayax stock-adjustment alerts to review." : null);
    }

    /// <summary>
    /// The preview of one stored event. The event row holds the provider's machine number, but the
    /// API speaks local machine ids since issue #741, so the preview reports the local machine the
    /// route named.
    /// </summary>
    private static NayaxStockEventPreviewDto ToPreviewDto(
        long localMachineId, PendingMachineStockEvent pending, IReadOnlyList<ManualRefillEvidence> manualRefills)
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
            localMachineId,
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
