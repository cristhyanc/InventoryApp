using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;

namespace InventoryApi.DTOs;

/// <summary>
/// One result row the collector submits (issue #937, parent #817).
///
/// <para><strong>It carries no listing id and no product id, and that is the contract.</strong>
/// Which listing this is comes from <c>(supplier, supplier product id)</c> and which product the
/// listing belongs to comes from the work item - the owner's decision of 27 October 2026 that "the
/// backend must still enforce product matching" expressed as a shape, so a collector cannot decide
/// what a price is a price of even by mistake.</para>
///
/// <para>Every enum travels as its number, as every other enum this API publishes does, and every
/// optional value is genuinely optional: an unknown pack quantity, barcode, variant, size or GST
/// rate is left out rather than sent as a zero.</para>
/// </summary>
public sealed record PriceCollectorResultRowRequest
{
    /// <summary>The issued work item this row answers.</summary>
    public long WorkItemId { get; init; }

    /// <summary>The supplier the row was read from, checked against the work item's own.</summary>
    public int SupplierPresetId { get; init; }

    /// <summary>The supplier's own product id or SKU, as published.</summary>
    [MaxLength(SupplierListingPolicy.MaxSupplierProductIdLength)]
    public string? SupplierProductId { get; init; }

    /// <summary>The supplier's own wording for the product.</summary>
    [MaxLength(SupplierListingPolicy.MaxTitleLength)]
    public string? Title { get; init; }

    /// <summary>Which variant it is, as read.</summary>
    [MaxLength(SupplierListingPolicy.MaxVariantLength)]
    public string? Variant { get; init; }

    /// <summary>The individual item's size, as read.</summary>
    public decimal? SizeValue { get; init; }

    /// <summary>What that size is measured in.</summary>
    public MasterProductSizeUnit? SizeUnit { get; init; }

    /// <summary>The published barcode, as text - never a number, because the leading zeros matter.</summary>
    [MaxLength(SupplierListingPolicy.MaxBarcodeLength)]
    public string? Barcode { get; init; }

    /// <summary>Whether that barcode is the unit's or the carton's.</summary>
    public BarcodeScope? BarcodeScope { get; init; }

    /// <summary>How many individual items the pack holds.</summary>
    public int? PackQuantity { get; init; }

    /// <summary>What the whole pack costs.</summary>
    public decimal PackPrice { get; init; }

    /// <summary>What it normally costs, beside a promotion.</summary>
    public decimal? RegularPackPrice { get; init; }

    /// <summary>The currency, which must be AUD.</summary>
    public string? Currency { get; init; }

    /// <summary>Whether the pack price includes GST. Checked against the supplier master record.</summary>
    public SupplierPriceBasis PriceBasis { get; init; }

    /// <summary>The supplier's own GST rate for this product, as a proportion, when it publishes one.</summary>
    public decimal? SupplierGstRate { get; init; }

    /// <summary>Whether this price was a promotion.</summary>
    public bool IsPromotion { get; init; }

    /// <summary>When that promotion finishes, when the supplier says.</summary>
    public DateTime? PromotionEndsAtUtc { get; init; }

    /// <summary>What the supplier said about availability.</summary>
    public SupplierStockStatus StockStatus { get; init; }

    /// <summary>When the price was read.</summary>
    public DateTime ObservedAtUtc { get; init; }

    /// <summary>The collector's own reference for the result it read.</summary>
    [MaxLength(SupplierPriceObservationPolicy.MaxSourceReferenceLength)]
    public string? SourceReference { get; init; }

    /// <summary>The page it was read from. Its host is checked against the supplier master record.</summary>
    [MaxLength(SupplierPriceObservationPolicy.MaxSourceUrlLength)]
    public string? SourceUrl { get; init; }

    /// <summary>This row as the use case takes it, with every instant read as UTC.</summary>
    public PriceCollectorResultRowSubmission ToSubmission() =>
        new(
            WorkItemId,
            SupplierPresetId,
            SupplierProductId,
            Title,
            Variant,
            SizeValue,
            SizeUnit,
            Barcode,
            BarcodeScope,
            PackQuantity,
            PackPrice,
            RegularPackPrice,
            Currency,
            PriceBasis,
            SupplierGstRate,
            IsPromotion,
            PromotionEndsAtUtc is { } ends ? PriceCollectorInstant.AsUtc(ends) : null,
            StockStatus,
            PriceCollectorInstant.AsUtc(ObservedAtUtc),
            SourceReference,
            SourceUrl);
}

/// <summary>
/// One submitted batch (issue #937): the run it belongs to and its rows, at most
/// <c>PriceCollectorResultPolicy.MaxRowsPerBatch</c> of them.
/// </summary>
public sealed record PriceCollectorResultsRequest
{
    /// <summary>
    /// The run the collector is reporting under, as <c>GET /api/price-collector/work</c> issued it.
    /// Marked <see cref="JsonRequiredAttribute"/> because a run id defaults to <c>0</c> on
    /// under-posting, which is never a real run rather than being refused outright.
    /// </summary>
    [JsonRequired]
    public long RunId { get; init; }

    /// <summary>The rows. A larger batch is refused whole, with nothing stored.</summary>
    public IReadOnlyList<PriceCollectorResultRowRequest> Rows { get; init; } = [];

    /// <summary>This batch as the use case takes it.</summary>
    public PriceCollectorResultBatch ToBatch() =>
        new(RunId, [.. Rows.Select(row => row.ToSubmission())]);
}

/// <summary>What became of one submitted row, as the response reports it (issue #937).</summary>
public sealed record PriceCollectorRowResultResponse(
    long WorkItemId,
    string SupplierProductId,
    PriceCollectorRowOutcome Outcome,
    string? Reason,
    long? ListingId,
    long? ObservationId)
{
    public static PriceCollectorRowResultResponse From(PriceCollectorRowResult row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new PriceCollectorRowResultResponse(
            row.WorkItemId,
            row.SupplierProductId,
            row.Outcome,
            row.Reason,
            row.ListingId,
            row.ObservationId);
    }
}

/// <summary>
/// The answer to one submitted batch (issue #937): its rows in four lists, which are the four
/// outcomes, each row carrying the reason it got that one.
///
/// <para>Nothing here says anything about a business. A listing id, an observation id, a work item
/// id and a supplier's own product id are facts about a supplier's shelf, which is what lets this
/// surface be reachable by a caller that holds no membership anywhere.</para>
/// </summary>
public sealed record PriceCollectorResultsResponse(
    long RunId,
    IReadOnlyList<PriceCollectorRowResultResponse> Accepted,
    IReadOnlyList<PriceCollectorRowResultResponse> SentToReview,
    IReadOnlyList<PriceCollectorRowResultResponse> Pending,
    IReadOnlyList<PriceCollectorRowResultResponse> Rejected)
{
    public static PriceCollectorResultsResponse From(PriceCollectorResultReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new PriceCollectorResultsResponse(
            receipt.RunId,
            [.. receipt.Accepted.Select(PriceCollectorRowResultResponse.From)],
            [.. receipt.SentToReview.Select(PriceCollectorRowResultResponse.From)],
            [.. receipt.Pending.Select(PriceCollectorRowResultResponse.From)],
            [.. receipt.Rejected.Select(PriceCollectorRowResultResponse.From)]);
    }
}

/// <summary>
/// A collector finishing one search (issue #937): what happened, why if it failed, and the total
/// number of distinct supplier product ids it submitted for that search.
/// </summary>
public sealed record PriceCollectorSearchOutcomeRequest
{
    /// <summary>
    /// The run this search belongs to. Marked <see cref="JsonRequiredAttribute"/> because a run id
    /// defaults to <c>0</c> on under-posting, which is never a real run rather than being refused
    /// outright.
    /// </summary>
    [JsonRequired]
    public long RunId { get; init; }

    /// <summary>
    /// What happened. <c>NotSearched</c> is the run completion's own answer and is refused here.
    /// Marked <see cref="JsonRequiredAttribute"/> because an outcome left out would otherwise default
    /// to the enum's own <c>0</c>, which names none of the four answers a search may give.
    /// </summary>
    [JsonRequired]
    public PriceCollectorWorkItemOutcome Outcome { get; init; }

    /// <summary>Why it failed, for a failed search and for no other.</summary>
    public PriceCollectorSearchFailureReason? FailureReason { get; init; }

    /// <summary>
    /// How many distinct supplier product ids the collector submitted for this search, across every
    /// batch. The answer is accepted only while this equals what the backend received.
    ///
    /// <para>Marked <see cref="JsonRequiredAttribute"/> because <c>0</c> is itself a legitimate count
    /// - the one <c>no_matches</c> and <c>failed</c> require - so it must be the collector's own
    /// stated count rather than what under-posting would default it to.</para>
    /// </summary>
    [JsonRequired]
    public int SubmittedRowCount { get; init; }

    /// <summary>This answer as the use case takes it.</summary>
    public PriceCollectorSearchAnswer ToAnswer(long workItemId) =>
        new(RunId, workItemId, Outcome, FailureReason, SubmittedRowCount);
}

/// <summary>The recorded answer to one search (issue #937).</summary>
public sealed record PriceCollectorSearchOutcomeResponse(
    long WorkItemId,
    PriceCollectorWorkItemOutcome Outcome,
    PriceCollectorSearchFailureReason? FailureReason,
    int SubmittedRowCount,
    DateTime RecordedAtUtc,
    int ListingsRecordedNotFound)
{
    public static PriceCollectorSearchOutcomeResponse From(PriceCollectorSearchAnswerReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new PriceCollectorSearchOutcomeResponse(
            receipt.WorkItemId,
            receipt.Outcome,
            receipt.FailureReason,
            receipt.SubmittedRowCount,
            receipt.RecordedAtUtc,
            receipt.ListingsRecordedNotFound);
    }
}

/// <summary>One closed run (issue #937): how it ended, and what became of its searches.</summary>
public sealed record PriceCollectorRunCompletionResponse(
    long RunId,
    PriceCollectorRunStatus Status,
    DateTime CompletedAtUtc,
    int CompleteSearchCount,
    int TruncatedSearchCount,
    int NoMatchesSearchCount,
    int FailedSearchCount,
    int NotSearchedCount,
    int ListingsCheckedCount,
    int ObservationsRecordedCount,
    int FailureCount)
{
    public static PriceCollectorRunCompletionResponse From(PriceCollectorRunCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        return new PriceCollectorRunCompletionResponse(
            completion.RunId,
            completion.Status,
            completion.CompletedAtUtc,
            completion.Counts.Complete,
            completion.Counts.Truncated,
            completion.Counts.NoMatches,
            completion.Counts.Failed,
            completion.Counts.NotSearched,
            completion.ListingsCheckedCount,
            completion.ObservationsRecordedCount,
            completion.FailureCount);
    }
}

/// <summary>
/// How an instant submitted over the wire is read (issue #937): as UTC, whatever the payload said.
///
/// <para>A collector run is scheduled in UTC and every instant in this catalogue is UTC, so an
/// offset-less value is UTC and an offset-bearing one is converted rather than refused. Leaving a
/// kind unspecified would make "is this observation inside the run window?" depend on the server's
/// own time zone, which is the one thing that must not vary between deployments.</para>
/// </summary>
internal static class PriceCollectorInstant
{
    public static DateTime AsUtc(DateTime instant) => instant.Kind switch
    {
        DateTimeKind.Utc => instant,
        DateTimeKind.Local => instant.ToUniversalTime(),
        _ => DateTime.SpecifyKind(instant, DateTimeKind.Utc),
    };
}
