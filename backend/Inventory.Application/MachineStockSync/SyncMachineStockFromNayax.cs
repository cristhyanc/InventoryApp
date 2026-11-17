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
/// <para><strong>Events already waiting are re-matched against that same map</strong> (issue
/// #1248). A match used to be decided once, at import, so an event imported while the MDB position
/// or the product's Nayax id was missing stayed Needs Review for ever and its refill had to be
/// entered by hand. Every load of the list now re-evaluates the events still waiting for a match
/// through <see cref="RematchMachineStockEvents"/>, against the mapping this same run read, so the
/// preview the operator is handed already shows the refreshed match and a newly matched event can
/// simply be applied. Processed, reconciled and separately-applied events are never re-matched.</para>
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
///
/// <para><strong>Product replacements are imported beside stock adjustments</strong> (issue #1325).
/// A Nayax "Machine Product Changed" alert (Event 510) is stored with the same
/// <c>NayaxEventLogId</c> deduplication as a 501 alert, parsed by
/// <see cref="NayaxProductReplacementEventParser"/>, and shown in the same working list with both
/// reported names. It is never matched to a product and never moves stock: its local-product and
/// quantity suggestions come from <see cref="NayaxProductReplacementSuggestionPolicy"/>, computed
/// from the live machine read every time the list is built, and its only resolution is "already
/// recorded manually" until applying a replacement exists (#1326).</para>
/// </summary>
public sealed class SyncMachineStockFromNayax : IMachineRestockEventFetch
{
    private readonly INayaxLynxClient _nayax;
    private readonly IMachineStockEventStore _store;
    private readonly RematchMachineStockEvents _rematch;
    private readonly IClock _clock;
    private readonly INayaxConnectionStore _connections;
    private readonly IMachineRestockSyncCoverageStore _coverage;
    private readonly IBusinessCalendar _businessCalendar;

    public SyncMachineStockFromNayax(
        INayaxLynxClient nayax,
        IMachineStockEventStore store,
        RematchMachineStockEvents rematch,
        IClock clock,
        INayaxConnectionStore connections,
        IMachineRestockSyncCoverageStore coverage,
        IBusinessCalendar businessCalendar)
    {
        _nayax = nayax;
        _store = store;
        _rematch = rematch;
        _clock = clock;
        _connections = connections;
        _coverage = coverage;
        _businessCalendar = businessCalendar;
    }

    /// <summary>
    /// The Dashboard's hourly fetch (issue #1122): this same import, with the preview it builds
    /// discarded. Only the import and the #1018 coverage write matter to that caller.
    /// </summary>
    Task IMachineRestockEventFetch.FetchAsync(MachineIdentity machine, CancellationToken cancellationToken) =>
        Handle(machine, cancellationToken);

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
            .Where(alert => IsImportedEventCode(alert.EventCode))
            .GroupBy(alert => alert.EventLogId)
            .Select(group => group.First())
            .ToList();

        var alreadyImported = (await _store.GetImportedNayaxEventLogIdsAsync(machineId, cancellationToken)).ToHashSet();
        var newAlerts = stockAlerts.Where(alert => !alreadyImported.Contains(alert.EventLogId)).ToList();

        // Read before the import, so what is re-matched is exactly the events that were already
        // waiting: the alerts this run imports are resolved by the same mapping as they are stored.
        var waiting = await _store.FindRematchCandidatesAsync(machineId, cancellationToken);

        MachineStockMatchRefresh? mapping = null;
        if (newAlerts.Count > 0 || waiting.Count > 0)
        {
            // Machine + MDB is the primary mapping, read from the same live machine-products
            // projection the machine detail view already uses - and read fresh (issue #842), because
            // this mapping decides which product a stock adjustment is recorded against. One read
            // serves both the import and the re-match, so the two cannot disagree about it.
            mapping = await _rematch.ReadCurrentMappingAsync(machineId, cancellationToken);

            if (newAlerts.Count > 0)
            {
                await _store.ImportAsync(
                    newAlerts.Select(alert => ToImport(machineId, alert, mapping)).ToList(),
                    cancellationToken);
            }

            // Issue #1248: an event whose MDB position or product link has been fixed since it was
            // imported becomes applicable here, rather than staying Needs Review for ever.
            if (waiting.Count > 0)
            {
                await _rematch.RefreshAsync(machineId, waiting, mapping, cancellationToken);
            }
        }

        var preview = await BuildPreviewAsync(
            machine, newAlerts.Count, fromDateGmt, includeReconciled, mapping, cancellationToken);

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

    /// <summary>The alert kinds Sync Restock imports: 501 stock adjustments and, since issue #1325, 510 product replacements.</summary>
    private static bool IsImportedEventCode(int eventCode) =>
        eventCode is NayaxMachineAlertEventCodes.StockAdjustForMachine
            or NayaxMachineAlertEventCodes.MachineProductChanged;

    private static MachineStockEventImport ToImport(
        long machineId, NayaxMachineAlert alert, MachineStockMatchRefresh mapping)
    {
        if (alert.EventCode == NayaxMachineAlertEventCodes.MachineProductChanged)
        {
            return ToReplacementImport(machineId, alert);
        }

        if (!NayaxStockAdjustmentEventParser.TryParse(alert.EventData, out var parsed, out var failureReason))
        {
            var unparsed = NayaxMachineStockMatchPolicy.Unparsed(failureReason);
            return NewImport(machineId, alert, null, null, null, unparsed);
        }

        var resolution = mapping.Resolve(parsed);
        return NewImport(machineId, alert, parsed.Mdb, parsed.ProductName, parsed.SignedQuantity, resolution);
    }

    /// <summary>
    /// A product replacement is stored with both reported names and no match and no quantity
    /// (issue #1325): it is Needs Review by construction, because nothing about it may be applied,
    /// and a parsed one carries no reason because the list explains it from its suggestions. Text
    /// that does not parse keeps the raw EventData and the parser's reason, like a 501 alert.
    /// </summary>
    private static MachineStockEventImport ToReplacementImport(long machineId, NayaxMachineAlert alert)
    {
        if (!NayaxProductReplacementEventParser.TryParse(alert.EventData, out var parsed, out var failureReason))
        {
            return NewImport(machineId, alert, null, null, null, NayaxMachineStockMatchPolicy.Unparsed(failureReason));
        }

        return NewImport(
            machineId,
            alert,
            parsed.Mdb,
            parsed.IncomingProductName,
            null,
            new NayaxStockEventResolution(NayaxStockEventMatchStatus.NeedsReview, null, null),
            parsed.OutgoingProductName);
    }

    private static MachineStockEventImport NewImport(
        long machineId,
        NayaxMachineAlert alert,
        int? parsedMdb,
        string? parsedProductName,
        int? parsedQuantity,
        NayaxStockEventResolution resolution,
        string? parsedOutgoingProductName = null) =>
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
            resolution.NeedsReviewReason,
            parsedOutgoingProductName);

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
        MachineStockMatchRefresh? mapping,
        CancellationToken cancellationToken)
    {
        var page = await _store.GetUnprocessedEventsAsync(
            machine.RequiredNayaxMachineId, cancellationToken, fromDateGmt, includeReconciled);
        var pendingEvents = page.Events;
        var manualRefills = await _store.GetManualMachineRefillsAsync(machine.LocalMachineId, cancellationToken);
        var replacements = await ReadReplacementContextAsync(
            machine.RequiredNayaxMachineId, pendingEvents, fromDateGmt, includeReconciled, mapping, cancellationToken);

        var events = pendingEvents
            .Select(pending => ToPreviewDto(machine.LocalMachineId, pending, manualRefills, replacements))
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
        long localMachineId,
        PendingMachineStockEvent pending,
        IReadOnlyList<ManualRefillEvidence> manualRefills,
        ReplacementContext replacements)
    {
        var impact = NayaxMachineStockImpactPolicy.Evaluate(
            pending.MatchStatus, pending.ParsedQuantity, pending.MatchedProductQuantityInStock);

        var possibleDuplicate = impact.IsPositiveRefill
            ? NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
                pending.MatchedProductId!.Value, pending.EventDateTimeGmt, manualRefills)
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
            possibleDuplicate is { } duplicate ? Describe(duplicate, pending.ParsedQuantity!.Value) : null,
            pending.DuplicateResolution,
            possibleDuplicate is { } matched ? -matched.Match.QuantityChange : null,
            // A movement's EffectiveAt is a persisted UTC instant, but SQLite does not round-trip
            // DateTimeKind and StockAdjustment.EffectiveAt carries no read conversion, so it is
            // marked UTC here. Without it the preview would publish an offset-less timestamp the
            // browser would read as its own local time and show the refill at the wrong hour.
            possibleDuplicate is { } timed ? AsUtc(timed.Match.EffectiveAt) : null,
            possibleDuplicate?.OtherQualifyingCount ?? 0,
            pending.EventCode,
            replacements.Describe(pending),
            replacements.DoubleCountNotes(pending) is not null,
            replacements.DoubleCountNotes(pending));
    }

    /// <summary>
    /// Everything a product replacement's row needs beyond the row itself (issue #1325), read once
    /// for the whole list - and not at all when the machine has no pending replacement. The
    /// pending events it compares against are the machine's <em>whole</em> unresolved list, never
    /// the page the operator's From date or Show reconciled filter produced, so a filter cannot hide
    /// the newer event or the 501 that a suggestion or a double-count flag depends on.
    /// </summary>
    private async Task<ReplacementContext> ReadReplacementContextAsync(
        long nayaxMachineId,
        IReadOnlyList<PendingMachineStockEvent> page,
        DateTime? fromDateGmt,
        bool includeReconciled,
        MachineStockMatchRefresh? mapping,
        CancellationToken cancellationToken)
    {
        // Only a row with an MDB can carry a suggestion or a double-count flag, so a page without
        // one needs nothing more - and no further read.
        if (!page.Any(pending => pending.ParsedMdb is not null))
        {
            return ReplacementContext.None;
        }

        // The unresolved list: unprocessed and not reconciled as already recorded manually. With no
        // filter it is the page itself; with one it is read whole, so a 501 on the page is still
        // flagged against a replacement the From date filtered out.
        var unresolved = fromDateGmt is null && !includeReconciled
            ? page
            : (await _store.GetUnprocessedEventsAsync(nayaxMachineId, cancellationToken)).Events;

        if (!page.Any(IsParsedReplacement))
        {
            return unresolved.Any(IsParsedReplacement)
                ? new ReplacementContext(unresolved, null, [])
                : ReplacementContext.None;
        }

        // The same live read the import used when this run imported or re-matched anything;
        // otherwise read here, because a suggestion must come from what Nayax reports now.
        mapping ??= await _rematch.ReadCurrentMappingAsync(nayaxMachineId, cancellationToken);
        var activeProducts = await _store.GetActiveProductCandidatesAsync(cancellationToken);

        return new ReplacementContext(unresolved, mapping, activeProducts);
    }

    private static bool IsParsedReplacement(PendingMachineStockEvent pending) =>
        pending.EventCode == NayaxMachineAlertEventCodes.MachineProductChanged
        && pending.ParsedMdb is not null
        && pending.ParsedProductName is not null
        && pending.ParsedOutgoingProductName is not null;

    private static ParsedNayaxProductReplacement AsReplacement(PendingMachineStockEvent pending) =>
        new(pending.ParsedMdb!.Value, pending.ParsedOutgoingProductName!, pending.ParsedProductName!);

    /// <summary>
    /// The suggestions and double-count flags of one list's product replacements (issue #1325),
    /// all decided by <see cref="NayaxProductReplacementSuggestionPolicy"/>.
    /// </summary>
    private sealed class ReplacementContext
    {
        public static readonly ReplacementContext None = new([], null, []);

        private readonly IReadOnlyList<PendingMachineStockEvent> _unresolved;
        private readonly MachineStockMatchRefresh? _mapping;
        private readonly IReadOnlyList<Inventory.Domain.Reporting.ProductMatching.ProductMatchCandidate> _activeProducts;

        public ReplacementContext(
            IReadOnlyList<PendingMachineStockEvent> unresolved,
            MachineStockMatchRefresh? mapping,
            IReadOnlyList<Inventory.Domain.Reporting.ProductMatching.ProductMatchCandidate> activeProducts)
        {
            _unresolved = unresolved;
            _mapping = mapping;
            _activeProducts = activeProducts;
        }

        public NayaxProductReplacementPreviewDto? Describe(PendingMachineStockEvent pending)
        {
            if (_mapping is null || !IsParsedReplacement(pending))
            {
                return null;
            }

            var replacement = AsReplacement(pending);
            var slot = _mapping.SlotReading(replacement.Mdb);
            var outgoing = NayaxProductReplacementSuggestionPolicy.SuggestOutgoing(replacement, _activeProducts);
            var incoming = NayaxProductReplacementSuggestionPolicy.SuggestIncoming(replacement, slot, _activeProducts);
            var quantity = NayaxProductReplacementSuggestionPolicy.SuggestIncomingQuantity(
                replacement, slot, HasNewerPendingEvent(pending));

            return new NayaxProductReplacementPreviewDto(
                replacement.OutgoingProductName,
                replacement.IncomingProductName,
                outgoing.ProductId,
                outgoing.ProductName,
                outgoing.Source,
                outgoing.Reason,
                incoming.ProductId,
                incoming.ProductName,
                incoming.Source,
                incoming.Reason,
                quantity.Quantity,
                quantity.Reason);
        }

        /// <summary>
        /// The possible double count between a pending replacement and a pending 501 for the same
        /// machine and MDB within 24 hours of it, described on both rows, or <see langword="null"/>.
        /// A safeguard only: it blocks nothing and changes no resolution.
        /// </summary>
        public string? DoubleCountNotes(PendingMachineStockEvent pending)
        {
            if (!IsUnresolved(pending) || pending.ParsedMdb is not int mdb)
            {
                return null;
            }

            if (IsParsedReplacement(pending))
            {
                var adjustments = _unresolved.Count(other =>
                    other.EventCode == NayaxMachineAlertEventCodes.StockAdjustForMachine
                    && other.ParsedMdb == mdb
                    && NayaxProductReplacementSuggestionPolicy.IsPossibleDoubleCount(
                        pending.EventDateTimeGmt, other.EventDateTimeGmt));

                return adjustments == 0
                    ? null
                    : $"{adjustments} pending stock adjustment{(adjustments == 1 ? string.Empty : "s")} for MDB "
                        + $"{mdb} within 24 hours of this replacement may record the same refill.";
            }

            if (pending.EventCode != NayaxMachineAlertEventCodes.StockAdjustForMachine)
            {
                return null;
            }

            var replaced = _unresolved.Any(other =>
                IsParsedReplacement(other)
                && other.ParsedMdb == mdb
                && NayaxProductReplacementSuggestionPolicy.IsPossibleDoubleCount(
                    other.EventDateTimeGmt, pending.EventDateTimeGmt));

            return replaced
                ? $"A pending product replacement for MDB {mdb} within 24 hours may record the same refill."
                : null;
        }

        private static bool IsUnresolved(PendingMachineStockEvent pending) =>
            pending.ProcessingStatus == NayaxStockEventProcessingStatus.Unprocessed
            && pending.DuplicateResolution != NayaxDuplicateResolution.ReconciledManually;

        private bool HasNewerPendingEvent(PendingMachineStockEvent pending) =>
            _unresolved.Any(other =>
                other.Id != pending.Id
                && other.ParsedMdb == pending.ParsedMdb
                && (other.EventDateTimeGmt > pending.EventDateTimeGmt
                    || (other.EventDateTimeGmt == pending.EventDateTimeGmt && other.Id > pending.Id)));
    }

    /// <summary>
    /// What a flagged event says about the manual refill it may duplicate (issue #1244). Both
    /// quantities are named, because since the rule stopped requiring them to be equal "a manual
    /// refill was recorded" no longer tells the operator whether this is the same restock loaded
    /// short or a genuinely separate one. A manual refill's own change is storage's, so the units
    /// that went into the machine are its opposite.
    /// </summary>
    private static string Describe(NayaxPossibleDuplicate duplicate, int eventQuantity)
    {
        var notes = $"A manual machine refill of {-duplicate.Match.QuantityChange} was recorded for this "
            + $"product/machine at {AsUtc(duplicate.Match.EffectiveAt):O}; this Nayax event reports {eventQuantity}.";

        if (duplicate.OtherQualifyingCount == 0)
        {
            return notes;
        }

        var plural = duplicate.OtherQualifyingCount == 1 ? string.Empty : "s";

        return $"{notes} {duplicate.OtherQualifyingCount} other manual refill{plural} also qualified.";
    }
}
