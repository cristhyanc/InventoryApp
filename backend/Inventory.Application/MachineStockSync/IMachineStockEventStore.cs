using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// One Nayax alert to persist as an imported fact, already parsed and resolved by the use case.
/// <see cref="NayaxEventLogId"/> is the alert's documented <c>EventLogID</c>,
/// <see cref="EventDateTimeGmt"/> its <c>EventDateTimeGMT</c> (UTC) and
/// <see cref="EventDateTimeVmc"/> its machine-clock <c>EventDateTimeVMC</c>. The raw EventData is
/// carried verbatim and <see cref="RawSourceMetadata"/> holds the complete source alert as JSON in
/// Nayax's documented field names, so the import stays auditable.
///
/// <para>For a product replacement (Event 510, issue #1325) <see cref="ParsedProductName"/> is the
/// <em>incoming</em> product's name and <see cref="ParsedOutgoingProductName"/> the outgoing one's;
/// <see cref="ParsedQuantity"/> and <see cref="MatchedProductId"/> are always null, because a
/// replacement reports no quantity and its product suggestions are advice computed afresh on every
/// read, never a stored match. For a 501 event <see cref="ParsedOutgoingProductName"/> is null.</para>
/// </summary>
public sealed record MachineStockEventImport(
    long NayaxEventLogId,
    long MachineId,
    int EventCode,
    DateTime EventDateTimeGmt,
    DateTime EventDateTimeVmc,
    string RawEventData,
    string? RawSourceMetadata,
    int? ParsedMdb,
    string? ParsedProductName,
    int? ParsedQuantity,
    long? MatchedProductId,
    NayaxStockEventMatchStatus MatchStatus,
    string? NeedsReviewReason,
    string? ParsedOutgoingProductName = null);

/// <summary>
/// An imported event that has not been applied yet, with its matched product's current storage.
/// <see cref="EventCode"/> says which kind of alert it is (501 stock adjustment or 510 product
/// replacement, issue #1325) and <see cref="ParsedOutgoingProductName"/> is the replacement's
/// outgoing name.
/// </summary>
public sealed record PendingMachineStockEvent(
    int Id,
    long NayaxEventLogId,
    long MachineId,
    DateTime EventDateTimeGmt,
    DateTime? EventDateTimeVmc,
    string RawEventData,
    int? ParsedMdb,
    string? ParsedProductName,
    int? ParsedQuantity,
    long? MatchedProductId,
    string? MatchedProductName,
    int? MatchedProductQuantityInStock,
    NayaxStockEventMatchStatus MatchStatus,
    string? NeedsReviewReason,
    NayaxStockEventProcessingStatus ProcessingStatus,
    NayaxDuplicateResolution DuplicateResolution,
    int EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
    string? ParsedOutgoingProductName = null);

/// <summary>
/// One page of the Sync Restock working list (issue #206): the currently-unprocessed events that
/// satisfy the operator's From date / Show reconciled filters, plus how many reconciled-manually
/// events in that same date window are hidden because Show reconciled is off.
/// </summary>
public sealed record MachineStockEventsPage(
    IReadOnlyList<PendingMachineStockEvent> Events, int HiddenReconciledCount);

/// <summary>
/// The stored state one apply attempt needs in order to decide what to do.
///
/// <para><see cref="ParsedMdb"/> and <see cref="ParsedProductName"/> are what the alert's own
/// EventData parsed to, carried since issue #1248 because they are what the machine's current MDB
/// mapping is re-matched against. Both are absent for an alert whose EventData never parsed at all,
/// which is an event no mapping can ever resolve.</para>
///
/// <para><see cref="EventCode"/> is carried since issue #1325, because only a 501 stock adjustment
/// can ever be applied or re-matched: a 510 product replacement is refused by every apply path.</para>
/// </summary>
public sealed record MachineStockEventState(
    int Id,
    long NayaxEventLogId,
    DateTime EventDateTimeGmt,
    NayaxStockEventMatchStatus MatchStatus,
    string? NeedsReviewReason,
    NayaxStockEventProcessingStatus ProcessingStatus,
    long? MatchedProductId,
    int? ParsedQuantity,
    int? StockAdjustmentId,
    NayaxDuplicateResolution DuplicateResolution,
    int? ParsedMdb = null,
    string? ParsedProductName = null,
    int EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine);

/// <summary>
/// One event's refreshed product match (issue #1248): what the machine's current MDB mapping and
/// the local catalogue now say about an event that is still waiting for one. It carries only the
/// three match columns, so re-matching can never change an event's processing status, its duplicate
/// resolution, its movement, its parsed values or the imported alert itself.
/// </summary>
public sealed record MachineStockEventMatchUpdate(
    int EventId,
    NayaxStockEventMatchStatus MatchStatus,
    long? MatchedProductId,
    string? NeedsReviewReason);

/// <summary>
/// One accepted event's refill, as the use case decided it: the event to mark processed, the
/// movement's quantity, and - for the explicit duplicate override of issue #196 - the resolution to
/// stamp and the manual refill it was resolved against.
/// </summary>
public sealed record MachineStockRefillRequest(
    int EventId,
    long NayaxEventLogId,
    int Quantity,
    NayaxDuplicateResolution DuplicateResolution,
    int? MatchedManualStockAdjustmentId);

/// <summary>One recorded refill: the event it applied and the movement that was written for it.</summary>
public sealed record AppliedMachineRefill(int EventId, int StockAdjustmentId);

/// <summary>
/// One confirmed product replacement to record (issue #1326): the 510 event it settles, that
/// event's own <c>EventLogID</c> for the movements' notes, and the operator's choice as the
/// deterministic Domain rule <see cref="NayaxProductReplacementWritePolicy"/> accepted it.
/// </summary>
public sealed record MachineStockReplacementWrite(
    int EventId, long NayaxEventLogId, NayaxProductReplacementChoice Choice);

/// <summary>
/// The movements one applied replacement created, each <see langword="null"/> when the choice wrote
/// none: the incoming refill out of storage, the outgoing units' return to storage, and - for a
/// disposal only - the write-off of exactly those returned units.
/// </summary>
public sealed record AppliedProductReplacement(
    int? IncomingStockAdjustmentId,
    int? OutgoingReturnStockAdjustmentId,
    int? OutgoingWriteOffStockAdjustmentId);

/// <summary>
/// The outcome of one replacement's write. A failed one has recorded nothing at all - no movement
/// and no applied state on the event - and <see cref="Failure"/> says why, in the same shape and for
/// the same reasons as <see cref="MachineRefillApplication"/> (issue #1240).
/// </summary>
public sealed record MachineReplacementApplication(
    bool Succeeded,
    AppliedProductReplacement? Applied,
    MachineRefillFailure? Failure = null);

/// <summary>
/// Why one product's batch of refills could not be recorded (issue #1240).
///
/// <para><see cref="FailureType"/> is a short code for the diagnostic log line - an exception's type
/// name, or one of the constants here - and never an arbitrary message: a provider exception's own
/// text can quote what it failed on, and that belongs in neither a log nor a response.</para>
///
/// <para><see cref="CostDataQualityMessage"/> is the one failure whose reason is published to the
/// operator, and it is non-<see langword="null"/> only for that one: the cost replay's refusal is
/// composed from the caller's own product names and movement ids, says exactly what to repair, and
/// cannot be repaired by retrying. Every other failure keeps the established generic sentence,
/// because nothing else here has a message written for a caller.</para>
/// </summary>
public sealed record MachineRefillFailure(string FailureType, string? CostDataQualityMessage)
{
    /// <summary>The batch named an event this machine does not have, so nothing was staged.</summary>
    public const string EventNotFound = "EventNotFound";
}

/// <summary>
/// The outcome of one product's batch of refills. A failed batch has recorded nothing at all:
/// neither any inventory movement nor any event's processed state, <see cref="Applied"/> is empty,
/// and <see cref="Failure"/> says why (issue #1240). A successful one carries no failure.
/// </summary>
public sealed record MachineRefillApplication(
    bool Succeeded,
    IReadOnlyList<AppliedMachineRefill> Applied,
    MachineRefillFailure? Failure = null);

/// <summary>
/// The write transaction one product's batch of refills runs in (issue #1127). Disposing it without
/// <see cref="CommitAsync"/> rolls back everything written through the connection it shares since it
/// began - every movement of that product's batch, every event state it stamped, and the quantities
/// and costs the product's single rebuild touched. Disposal also drops whatever the batch left
/// tracked in the request's unit of work, so the history one product's rebuild loaded does not make
/// the next product's saves slower.
///
/// <para>On SQLite it is a <c>BEGIN IMMEDIATE</c> transaction, so the write lock is taken when the
/// transaction opens rather than at its first write. That is what makes the reads inside it
/// authoritative: the event's own eligibility and the product's storage quantity both decide a
/// write, and a deferred transaction would let two callers read the same answer and both act on it.
/// It is the same shape and the same reason as
/// <c>IPickListRefillStore.BeginWriteTransactionAsync</c> (issue #743).</para>
/// </summary>
public interface IMachineStockRefillWriteTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Narrow persistence port for the machine Sync Restock workflow (issue #183), owned by the
/// Application layer. Everything transactional, relational, or costing-specific lives behind it, so
/// the use cases stay free of EF Core.
/// </summary>
///
/// <para><strong>Two machine ids, never confused</strong> (issue #741). The imported events are the
/// provider's own rows and keep the provider's machine number, so every event read and write takes
/// <c>nayaxMachineId</c>. The stock movements are this application's, and since #741
/// <c>StockAdjustment.MachineId</c> holds the local machine, so the manual-refill evidence takes
/// <c>localMachineId</c> and an applied refill takes the whole
/// <see cref="Inventory.Application.Machines.MachineIdentity"/>: it finds the event by the one and
/// records the movement against the other.</para>
public interface IMachineStockEventStore
{
    /// <summary>The Nayax <c>EventLogID</c>s already imported for this machine; the basis of idempotency.</summary>
    Task<IReadOnlyList<long>> GetImportedNayaxEventLogIdsAsync(long nayaxMachineId, CancellationToken cancellationToken);

    /// <summary>
    /// The local products with the given local product ids, keyed by that id. Since issue #733 the
    /// caller resolves a Nayax product id to a local one through
    /// <see cref="Inventory.Application.Nayax.INayaxProductLinkStore"/> first: a remote identifier
    /// never reaches a persistence port as if it were a local key.
    /// </summary>
    Task<IReadOnlyDictionary<long, NayaxStockSyncProduct>> GetStorageProductsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken);

    /// <summary>
    /// The business's active products, as id and name only (issue #1325): the catalogue a product
    /// replacement's names are matched against when the live slot does not settle the suggestion.
    /// </summary>
    Task<IReadOnlyList<Inventory.Domain.Reporting.ProductMatching.ProductMatchCandidate>> GetActiveProductCandidatesAsync(
        CancellationToken cancellationToken);

    Task ImportAsync(IReadOnlyList<MachineStockEventImport> events, CancellationToken cancellationToken);

    /// <summary>
    /// The machine's currently-unprocessed events, oldest first (issue #206). <paramref
    /// name="fromDateGmt"/>, when given, is a lower bound on the canonical
    /// <see cref="PendingMachineStockEvent.EventDateTimeGmt"/> - the same event timestamp the
    /// stock-sync contract already defines, never a second interpretation. <paramref
    /// name="includeReconciled"/> controls whether events already resolved as "reconciled: already
    /// recorded manually" are included; when it is <c>false</c> they are excluded from the returned
    /// events but still counted in <see cref="MachineStockEventsPage.HiddenReconciledCount"/>.
    /// Neither parameter changes any persisted event: both are presentation/workflow filters over
    /// the same imported audit facts.
    /// </summary>
    Task<MachineStockEventsPage> GetUnprocessedEventsAsync(
        long nayaxMachineId,
        CancellationToken cancellationToken,
        DateTime? fromDateGmt = null,
        bool includeReconciled = false);

    /// <summary>
    /// Manual (operator-entered) machine refills for this machine, as duplicate evidence. A stock
    /// movement names the local machine (issue #741), so this takes the local id.
    /// </summary>
    Task<IReadOnlyList<ManualRefillEvidence>> GetManualMachineRefillsAsync(
        long localMachineId, CancellationToken cancellationToken);

    Task<MachineStockEventState?> FindEventAsync(long nayaxMachineId, int eventId, CancellationToken cancellationToken);

    /// <summary>
    /// The stored state of each of <paramref name="eventIds"/> that exists for this machine, in no
    /// particular order and with no entry at all for an id that does not (issue #1127). It is the
    /// same machine- and tenant-scoped read as <see cref="FindEventAsync"/>, asked once for a whole
    /// request instead of once per event.
    /// </summary>
    Task<IReadOnlyList<MachineStockEventState>> FindEventsAsync(
        long nayaxMachineId, IReadOnlyCollection<int> eventIds, CancellationToken cancellationToken);

    /// <summary>
    /// The machine's events that are still waiting for a product match (issue #1248): unprocessed,
    /// with no duplicate resolution recorded, not matched to a local product, and carrying the
    /// parsed MDB and product name a mapping resolves them by. A processed event - applied,
    /// reconciled as already recorded manually, or applied as a separate restock - is never
    /// returned, because its match already decided what was written and re-matching it would
    /// rewrite the record of a movement that happened; nor is an alert whose EventData never
    /// parsed, which no mapping can resolve.
    /// </summary>
    Task<IReadOnlyList<MachineStockEventState>> FindRematchCandidatesAsync(
        long nayaxMachineId, CancellationToken cancellationToken);

    /// <summary>
    /// Records the refreshed match of each of <paramref name="updates"/> and returns how many rows
    /// actually changed (issue #1248). It writes the three match columns and nothing else, and only
    /// on an event of this machine that is still waiting for a match - the same condition
    /// <see cref="FindRematchCandidatesAsync"/> reads by, re-checked at the write, so an event
    /// applied or resolved since that read is left exactly as the writer that settled it left it.
    ///
    /// <para>It saves inside the caller's transaction where there is one and deliberately does
    /// <b>not</b> commit, so an apply that re-matches an event under its write lock commits the
    /// refreshed match and the movement it decided together, or neither.</para>
    /// </summary>
    Task<int> UpdateMatchesAsync(
        long nayaxMachineId,
        IReadOnlyList<MachineStockEventMatchUpdate> updates,
        CancellationToken cancellationToken);

    Task<NayaxStockSyncProduct?> FindStorageProductAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// The product with this id while it is an <strong>active</strong> product of the caller's own
    /// business, and <see langword="null"/> otherwise (issue #1326). One answer covers a retired
    /// product, another business's product and one that no longer exists alike: the tenant query
    /// filters are what make a foreign id indistinguishable from a missing one here, and the
    /// replacement apply's single refusal sentence is what keeps it that way for the caller too.
    ///
    /// <para>It is deliberately separate from <see cref="FindStorageProductAsync"/>, which an
    /// ordinary apply uses on a product the <em>import</em> matched: that match may legitimately
    /// name a product the operator has since retired, and refusing it here would change the 501
    /// apply's behaviour. A product a request names is held to the stricter rule.</para>
    /// </summary>
    Task<NayaxStockSyncProduct?> FindActiveStorageProductAsync(
        long productId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the write transaction one product's batch of refills runs in (issue #1127). Every read
    /// that decides those writes - the events' own stored state and the product's storage quantity -
    /// must happen inside it.
    /// </summary>
    Task<IMachineStockRefillWriteTransaction> BeginRefillWriteTransactionAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Records every refill of one product and each event's processed state together, so an event
    /// can never be left marked processed without its inventory movement, and rebuilds that
    /// product's cost exactly <strong>once</strong> for the whole batch (issue #1127). Each refill
    /// is still its own <c>MachineRefill</c> movement with its own note and its own
    /// <c>StockAdjustmentId</c> on the event row, so every event stays individually auditable.
    ///
    /// <para>The refill is a machine transfer out of storage: it reduces storage quantity and must
    /// not change costing quantity/value or create COGS.
    /// <see cref="MachineStockRefillRequest.DuplicateResolution"/> is
    /// <see cref="NayaxDuplicateResolution.None"/> for an ordinary (non-duplicate) apply, or
    /// <see cref="NayaxDuplicateResolution.AppliedAsSeparateRestock"/> when the operator explicitly
    /// overrode a possible-duplicate warning (issue #196); in that case
    /// <see cref="MachineStockRefillRequest.MatchedManualStockAdjustmentId"/> is the manual refill
    /// the override was resolved against, persisted alongside the movement for auditability. The
    /// events are found by the machine's Nayax number and the movements record its local id
    /// (issue #741).</para>
    ///
    /// <para>It saves inside the caller's transaction and deliberately does <b>not</b> commit: the
    /// caller opened the transaction and owns the commit, so a failure here - a movement that cannot
    /// be recorded or a fatal costing data-quality issue - is answered with
    /// <c>Succeeded: false</c> and the caller's disposal discards every write of that product's
    /// batch.</para>
    /// </summary>
    Task<MachineRefillApplication> ApplyProductRefillsAsync(
        Machines.MachineIdentity machine,
        long productId,
        IReadOnlyList<MachineStockRefillRequest> refills,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records one confirmed product replacement (issue #1326): up to three movements, the event's
    /// applied state and the choice it was applied with, all in the caller's own write-locked
    /// transaction and in <strong>one</strong> unit of work, so the event can never be left marked
    /// applied without the movements it names - or with only some of them.
    ///
    /// <para>The movements are exactly what
    /// <see cref="NayaxProductReplacementWritePolicy.Movements"/> describes, in that order: the
    /// incoming refill (a negative storage change, costing-neutral, carrying the event's own
    /// <c>EventDateTimeGMT</c> as the machine ledger's instant exactly as a Sync Restock refill
    /// does), then - for an outgoing choice - the return of the outgoing units to storage (the same
    /// <c>MachineRefill</c> reason with a <em>positive</em> storage change, equally costing-neutral),
    /// then - for a disposal only - the established <c>Damaged</c>/<c>Expired</c> write-off of
    /// exactly those units. The return is staged before the write-off deliberately: the units are in
    /// the machine, so they are in storage before they are written off it.</para>
    ///
    /// <para>Each product whose stock actually moved is rebuilt <strong>once</strong>, so the loss a
    /// disposal records is costed by the one authoritative cost replay. Like
    /// <see cref="ApplyProductRefillsAsync"/> it saves inside the caller's transaction and
    /// deliberately does <b>not</b> commit: a failure is answered with <c>Succeeded: false</c> and
    /// the caller's disposal discards every write.</para>
    /// </summary>
    Task<MachineReplacementApplication> ApplyProductReplacementAsync(
        Machines.MachineIdentity machine,
        MachineStockReplacementWrite replacement,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an event as "already recorded manually" (issue #196; extended by issue #242 to any
    /// otherwise-eligible unresolved event, not only one flagged as a possible duplicate): marks it
    /// reconciled against <paramref name="matchedManualStockAdjustmentId"/> - the matching manual
    /// refill's <c>StockAdjustment.Id</c> when the app found one, or <c>null</c> when the operator is
    /// explicitly resolving an event the app did not flag - without creating a Nayax-sourced movement
    /// or changing storage. Idempotent: resolving an event already reconciled this way changes
    /// nothing. Returns <c>false</c> only when the event does not exist for this machine.
    /// </summary>
    Task<bool> ReconcileAsManualDuplicateAsync(
        int eventId, long nayaxMachineId, int? matchedManualStockAdjustmentId, CancellationToken cancellationToken);
}
