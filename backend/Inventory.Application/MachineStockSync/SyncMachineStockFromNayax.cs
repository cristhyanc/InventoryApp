using System.Text.Json;
using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
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
///
/// <para><strong>A run that fully succeeds records that it did</strong> (issue #891), so the
/// Dashboard can say when this machine's restock alerts were last really fetched instead of guessing
/// it from an event's timestamps. The claim is deliberately narrow: it is recorded only after the
/// alert read, the import and the preview construction have all completed, it is stamped with the
/// instant the operation <em>started</em> - a fetch can only have accounted for alerts that already
/// existed when it began - and it is bound to the Nayax credential revision the operation
/// authenticated under. A fetch that threw, imported partially or never built a preview records
/// nothing at all, and a successful fetch that found no alert records coverage exactly as one that
/// found ten does: an empty answer from Nayax is an answer.
///
/// It is never a claim of complete historical coverage. The Lynx contract for
/// <c>GET /v1/machines/{MachineID}/lastAlerts</c> is "the most recent alerts" and declares neither a
/// range nor a completeness guarantee (Nayax developer portal, Get Machine Last Alerts), so what is
/// recorded is when this workflow last succeeded and nothing more.</para>
/// </summary>
public sealed class SyncMachineStockFromNayax
{
    private readonly INayaxLynxClient _nayax;
    private readonly IMachineStockEventStore _store;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly IClock _clock;
    private readonly INayaxConnectionStore _connections;
    private readonly IMachineRestockSyncCoverageStore _coverage;

    public SyncMachineStockFromNayax(
        INayaxLynxClient nayax,
        IMachineStockEventStore store,
        INayaxProductLinkStore productLinks,
        IClock clock,
        INayaxConnectionStore connections,
        IMachineRestockSyncCoverageStore coverage)
    {
        _nayax = nayax;
        _store = store;
        _productLinks = productLinks;
        _clock = clock;
        _connections = connections;
        _coverage = coverage;
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

        // Both taken before the first Nayax call (issue #891). The instant is what the coverage
        // claim is stamped with, because this fetch can only account for alerts that already
        // existed when it began. The revision is the credentials this operation is about to run
        // under: reading it afterwards would let a save that landed mid-operation be recorded as if
        // this run had proved the new token, and the conditional write refuses a stale revision
        // rather than guessing - so a credential replaced while the sync ran simply leaves the
        // coverage unrecorded.
        var startedAtUtc = _clock.UtcNow;
        var credentialRevision = (await _connections.FindAsync(cancellationToken))?.CredentialRevision;

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
            // projection the machine detail view already uses - and read fresh (issue #842), because
            // this mapping decides which product a stock adjustment is recorded against.
            var machineProducts = await _nayax.GetMachineProductsFreshAsync(machineId, cancellationToken);
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

        var preview = await BuildPreviewAsync(
            machine, newAlerts.Count, fromDateGmt, includeReconciled, cancellationToken);

        await RecordSuccessfulFetchAsync(
            machine.LocalMachineId, startedAtUtc, credentialRevision, cancellationToken);

        return preview;
    }

    /// <summary>
    /// Records the coverage of a run that fully succeeded (issue #891), after the import and the
    /// preview and never before either.
    ///
    /// <para>It is the last thing this use case does, and a failure of it does not fail the
    /// operation: the alerts are already imported and the preview the caller is about to be handed
    /// is the operator's working list, so losing the reconciliation to a reminder's bookkeeping
    /// would be the wrong trade. Leaving the coverage unrecorded errs in the safe direction - the
    /// Dashboard keeps warning that this machine is overdue, which is a reminder too many rather
    /// than a machine quietly presented as fresh.</para>
    ///
    /// <para>A run with no credential revision read nothing from a stored connection, so there are
    /// no credentials to bind a claim to and nothing is recorded.</para>
    /// </summary>
    private async Task RecordSuccessfulFetchAsync(
        long localMachineId, DateTime startedAtUtc, int? credentialRevision, CancellationToken cancellationToken)
    {
        if (credentialRevision is not { } revision)
        {
            return;
        }

        try
        {
            await _coverage.TryRecordSuccessfulSyncAsync(
                localMachineId, startedAtUtc, revision, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Deliberately swallowed; see the remarks above. The caller keeps its preview and the
            // reminder keeps its warning.
        }
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
