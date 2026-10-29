using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One result row a collector is submitting (issue #937, parent #817): which search it answers, and
/// everything it read off the supplier's page.
///
/// <para><strong>It carries no listing id and no product.</strong> Which listing this is, and which
/// product the listing belongs to, are the backend's answers - the work item names the product and
/// the supplier, and <c>(supplier, supplier product id)</c> names the listing. A row that could
/// choose its own product would be a collector deciding what a price is a price of, which is the one
/// thing the owner's "the backend must still enforce product matching" decision rules out.</para>
///
/// <para>Every value here is as published and untrusted. Nothing is defaulted on the way in: an
/// unknown pack quantity, barcode, variant, size or GST rate stays unknown, because each of those
/// defaults would be this application inventing a fact about a supplier's shelf.</para>
/// </summary>
/// <param name="WorkItemId">The issued work item this row answers.</param>
/// <param name="SupplierPresetId">
/// The supplier the row was read from. It is checked against the work item's own supplier rather
/// than trusted: a search at one retailer cannot answer for another's shelf.
/// </param>
/// <param name="SupplierProductId">The supplier's own product id or SKU, as published.</param>
/// <param name="Title">The supplier's own wording for the product.</param>
/// <param name="Variant">Which variant it is, as read.</param>
/// <param name="SizeValue">The individual item's size, as read.</param>
/// <param name="SizeUnit">What that size is measured in.</param>
/// <param name="Barcode">The published barcode, as text.</param>
/// <param name="BarcodeScope">Whether that barcode is the unit's or the carton's.</param>
/// <param name="PackQuantity">How many individual items the pack holds.</param>
/// <param name="PackPrice">What the whole pack costs.</param>
/// <param name="RegularPackPrice">What it normally costs, beside a promotion.</param>
/// <param name="Currency">The currency, which must be AUD.</param>
/// <param name="PriceBasis">Whether the pack price includes GST.</param>
/// <param name="SupplierGstRate">
/// The supplier's own GST rate for this product when it publishes one (the JB Metro <c>gst</c>
/// field), as a proportion, and <see langword="null"/> when it does not.
/// </param>
/// <param name="IsPromotion">Whether this price was a promotion.</param>
/// <param name="PromotionEndsAtUtc">When that promotion finishes, when the supplier says.</param>
/// <param name="StockStatus">What the supplier said about availability.</param>
/// <param name="ObservedAtUtc">When the price was read.</param>
/// <param name="SourceReference">The collector's own reference for the result it read.</param>
/// <param name="SourceUrl">The page it was read from.</param>
public sealed record PriceCollectorResultRowSubmission(
    long WorkItemId,
    int SupplierPresetId,
    string? SupplierProductId,
    string? Title,
    string? Variant,
    decimal? SizeValue,
    MasterProductSizeUnit? SizeUnit,
    string? Barcode,
    BarcodeScope? BarcodeScope,
    int? PackQuantity,
    decimal PackPrice,
    decimal? RegularPackPrice,
    string? Currency,
    SupplierPriceBasis PriceBasis,
    decimal? SupplierGstRate,
    bool IsPromotion,
    DateTime? PromotionEndsAtUtc,
    SupplierStockStatus StockStatus,
    DateTime ObservedAtUtc,
    string? SourceReference,
    string? SourceUrl);

/// <summary>
/// One batch of result rows for one run (issue #937): at most
/// <see cref="PriceCollectorResultPolicy.MaxRowsPerBatch"/> of them, for any mixture of that run's
/// work items.
///
/// <para>A batch is a transport unit and not a search: one search's rows may arrive in several
/// batches, and what finishes a search is its own outcome call.</para>
/// </summary>
public sealed record PriceCollectorResultBatch(
    long RunId,
    IReadOnlyList<PriceCollectorResultRowSubmission> Rows);

/// <summary>What became of one submitted row (issue #937).</summary>
/// <param name="WorkItemId">The work item the row cited.</param>
/// <param name="SupplierProductId">
/// The supplier product id the row cited, normalised, and the empty string for a row that carried
/// none at all - which is also the one kind of row no idempotency key could be recorded for.
/// </param>
/// <param name="Outcome">What was done with it.</param>
/// <param name="Reason">Why, for everything but an accepted row.</param>
/// <param name="ListingId">The listing it resolved to, when it resolved to one.</param>
/// <param name="ObservationId">The accepted observation it produced, when it produced one.</param>
public sealed record PriceCollectorRowResult(
    long WorkItemId,
    string SupplierProductId,
    PriceCollectorRowOutcome Outcome,
    string? Reason,
    long? ListingId,
    long? ObservationId);

/// <summary>
/// The answer to one submitted batch (issue #937): its rows in four lists, which are the four
/// outcomes.
///
/// <para>Four lists rather than one list with a field, because the collector reads them for four
/// different purposes: what is now a price, what a person has been asked to look at, what is waiting
/// on somebody's decision, and what it has to fix.</para>
/// </summary>
public sealed record PriceCollectorResultReceipt(
    long RunId,
    IReadOnlyList<PriceCollectorRowResult> Accepted,
    IReadOnlyList<PriceCollectorRowResult> SentToReview,
    IReadOnlyList<PriceCollectorRowResult> Pending,
    IReadOnlyList<PriceCollectorRowResult> Rejected);

/// <summary>
/// A collector finishing one search (issue #937): what happened, why if it failed, and how many
/// distinct supplier product ids it submitted for that search.
///
/// <para><strong>The total is the point of this call.</strong> A search's rows may arrive in several
/// batches, so "the collector has sent everything" is not something the backend can observe - the
/// collector states it, and the backend accepts the answer only while the number matches what it has
/// actually received. A batch lost in transit therefore leaves the search unanswered instead of
/// silently finished, and an unanswered search records no listing as missing.</para>
/// </summary>
public sealed record PriceCollectorSearchAnswer(
    long RunId,
    long WorkItemId,
    PriceCollectorWorkItemOutcome Outcome,
    PriceCollectorSearchFailureReason? FailureReason,
    int SubmittedRowCount);

/// <summary>The recorded answer to one search (issue #937).</summary>
/// <param name="WorkItemId">The search that was answered.</param>
/// <param name="Outcome">What was recorded for it.</param>
/// <param name="FailureReason">Why it failed, for a failed search and for no other.</param>
/// <param name="SubmittedRowCount">The total the collector declared, which matched what arrived.</param>
/// <param name="RecordedAtUtc">When the answer was recorded.</param>
/// <param name="ListingsRecordedNotFound">
/// How many known listings of this product at this supplier the search looked for and did not see.
/// Only a complete search or a no-match one records any
/// (<see cref="PriceCollectorWorkItemPolicy.RecordsListingsNotFound"/>); every other outcome records
/// none, because a search that stopped early saw less than everything.
/// </param>
public sealed record PriceCollectorSearchAnswerReceipt(
    long WorkItemId,
    PriceCollectorWorkItemOutcome Outcome,
    PriceCollectorSearchFailureReason? FailureReason,
    int SubmittedRowCount,
    DateTime RecordedAtUtc,
    int ListingsRecordedNotFound);

/// <summary>
/// One closed collector run (issue #937): how it ended, when, and what became of its searches.
/// </summary>
/// <param name="RunId">The run that was closed.</param>
/// <param name="Status">How it ended.</param>
/// <param name="CompletedAtUtc">When it was closed.</param>
/// <param name="Counts">
/// One count per outcome, which is also what <see cref="PriceCollectorRunPolicy.StatusFor"/> derived
/// <paramref name="Status"/> from - the status and its evidence are reported together.
/// </param>
/// <param name="ListingsCheckedCount">The distinct listings this run's rows resolved to.</param>
/// <param name="ObservationsRecordedCount">The prices it accepted.</param>
/// <param name="FailureCount">Its refused rows plus its failed searches.</param>
public sealed record PriceCollectorRunCompletion(
    long RunId,
    PriceCollectorRunStatus Status,
    DateTime CompletedAtUtc,
    PriceCollectorRunOutcomeCounts Counts,
    int ListingsCheckedCount,
    int ObservationsRecordedCount,
    int FailureCount);
