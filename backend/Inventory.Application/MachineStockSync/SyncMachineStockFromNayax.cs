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
/// nothing at all. <strong>Nor does a fetch that leaves work waiting</strong> (issue #1018): a
/// machine counts as synced only when nothing is left to sync, so coverage is recorded only while
/// the machine has no pending restock event in the reminder's window
/// (<see cref="MachineRestockSyncPendingWindow"/>). Opening Sync Restock and leaving its events
/// unapplied therefore never presents the machine as synced; the dialog re-syncs after every apply
/// and resolve, and the fetch after the last pending event is cleared is the one that records it.
/// A fetch that found no alert and left nothing pending records coverage exactly as one that found
/// ten and left nothing pending does: an empty answer from Nayax is an answer.</para>
///
/// <para>The binding to the credentials is <em>verified</em> rather than assumed. This use case
/// does not resolve the credentials its Nayax calls authenticate with - the
/// <c>INayaxRequestCredentialProvider</c> behind <see cref="INayaxLynxClient"/> does, per call - so
/// the revision read here before the first of those calls is a candidate, not evidence. What makes
/// it evidence is the fence inside
/// <see cref="IMachineRestockSyncCoverageStore.TryRecordSuccessfulSyncAsync"/>: that write lands
/// only while the stored connection still holds exactly this revision, and a credential revision
/// only ever increases, so the equality proves no save landed between the read below and the write -
/// and therefore that every Nayax request in between resolved that same revision. A save anywhere in
/// that window, including one that landed before the request captured its credentials, leaves the
/// coverage unrecorded.</para>
///
/// <para>It is never a claim of complete historical coverage. The Lynx contract for
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
    private readonly IBusinessCalendar _businessCalendar;

    public SyncMachineStockFromNayax(
        INayaxLynxClient nayax,
        IMachineStockEventStore store,
        INayaxProductLinkStore productLinks,
        IClock clock,
        INayaxConnectionStore connections,
        IMachineRestockSyncCoverageStore coverage,
        IBusinessCalendar businessCalendar)
    {
        _nayax = nayax;
        _store = store;
        _productLinks = productLinks;
        _clock = clock;
        _connections = connections;
        _coverage = coverage;
        _businessCalendar = businessCalendar;
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
        // existed when it began. The revision is the candidate the coverage write is fenced on: it
        // is not the snapshot the credential provider will resolve for each Nayax call below, and is
        // never treated as one. What binds the two together is the fence inside that write, which
        // accepts the claim only while the stored revision is still this one - so a credential saved
        // at any point while the sync ran, before or after a request captured its credentials,
        // leaves the coverage unrecorded instead of misattributed. Reading it here rather than
        // afterwards is what gives the fence a window to close.
        var startedAtUtc = _clock.UtcNow;
        var candidateCredentialRevision = (await _connections.FindAsync(cancellationToken))?.CredentialRevision;

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
            machine.LocalMachineId, machineId, startedAtUtc, candidateCredentialRevision, cancellationToken);

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
    ///
    /// <para>The write itself decides whether the claim is really this operation's to make: it is
    /// fenced on <paramref name="candidateCredentialRevision"/> still being the stored one and on
    /// the machine having nothing left to sync (issue #1018), and a refusal is reported as
    /// <see langword="false"/> rather than as a failure, because coverage that is left unrecorded is
    /// the safe answer.</para>
    /// </summary>
    private async Task RecordSuccessfulFetchAsync(
        long localMachineId,
        long nayaxMachineId,
        DateTime startedAtUtc,
        int? candidateCredentialRevision,
        CancellationToken cancellationToken)
    {
        if (candidateCredentialRevision is not { } revision)
        {
            return;
        }

        try
        {
            // Measured from the run's own start, the same "today" the reminder would use for it.
            var pendingFromUtc = MachineRestockSyncPendingWindow.StartUtc(_businessCalendar, startedAtUtc);
            await _coverage.TryRecordSuccessfulSyncAsync(
                localMachineId, startedAtUtc, revision, nayaxMachineId, pendingFromUtc, cancellationToken);
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
