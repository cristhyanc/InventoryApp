using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;

namespace InventoryApi.DTOs;

/// <summary>
/// One master product as the Super Admin review publishes it (issue #928): its key, its brand, its
/// name and the size of one item.
///
/// <para>The brand is published here and deliberately not on the member-facing read of #938: a
/// business may see only what the master product its own mapping is matched to is called and how big
/// it is, while the reviewer is deciding <em>which</em> product a description is. Nothing about a
/// business travels either way, because a master product carries no business identity at all.</para>
/// </summary>
public sealed record SuperAdminReviewMasterProductResponse(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit)
{
    public static SuperAdminReviewMasterProductResponse From(ReviewedMasterProduct master)
    {
        ArgumentNullException.ThrowIfNull(master);

        return new SuperAdminReviewMasterProductResponse(
            master.Id,
            master.Brand,
            master.Name,
            master.SizeValue,
            master.SizeUnit);
    }
}

/// <summary>One match suggestion as the review publishes it (issue #928).</summary>
public sealed record SuperAdminReviewMatchSuggestionResponse(
    long Id,
    SuperAdminReviewMasterProductResponse? MasterProduct,
    string? SuggestedBrand,
    string? SuggestedName,
    decimal? SuggestedSizeValue,
    MasterProductSizeUnit? SuggestedSizeUnit,
    decimal Confidence,
    string Reason,
    string CollectorRun,
    ProductMatchSuggestionStatus Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static SuperAdminReviewMatchSuggestionResponse From(ReviewedMatchSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        return new SuperAdminReviewMatchSuggestionResponse(
            suggestion.Id,
            suggestion.MasterProduct is null
                ? null
                : SuperAdminReviewMasterProductResponse.From(suggestion.MasterProduct),
            suggestion.SuggestedBrand,
            suggestion.SuggestedName,
            suggestion.SuggestedSizeValue,
            suggestion.SuggestedSizeUnit,
            suggestion.Confidence,
            suggestion.Reason,
            suggestion.CollectorRun,
            suggestion.Status,
            suggestion.CreatedAtUtc,
            suggestion.UpdatedAtUtc);
    }
}

/// <summary>
/// The reading one listing is holding pending, as the review publishes it (issue #928): what was
/// read, when, why it was held, and the server's own answers about whether it could become a price.
/// </summary>
public sealed record SuperAdminReviewPendingObservationResponse(
    long Id,
    long PriceCollectorRunId,
    long PriceCollectorWorkItemId,
    string Reason,
    decimal PackPrice,
    decimal? RegularPackPrice,
    string Currency,
    SupplierPriceBasis PriceBasis,
    decimal? SupplierGstRate,
    bool IsPromotion,
    DateTime? PromotionEndsAtUtc,
    SupplierStockStatus StockStatus,
    DateTime ObservedAtUtc,
    string? SourceReference,
    string? SourceUrl,
    string? Variant,
    decimal? SizeValue,
    MasterProductSizeUnit? SizeUnit,
    string? Barcode,
    BarcodeScope? BarcodeScope,
    int? UnitsPerPack,
    string? Title,
    DateTime RecordedAtUtc,
    bool IsFresh,
    bool NeedsPriceException)
{
    public static SuperAdminReviewPendingObservationResponse From(ReviewedPendingObservation reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        return new SuperAdminReviewPendingObservationResponse(
            reading.Id,
            reading.PriceCollectorRunId,
            reading.PriceCollectorWorkItemId,
            reading.Reason,
            reading.PackPrice,
            reading.RegularPackPrice,
            reading.Currency,
            reading.PriceBasis,
            reading.SupplierGstRate,
            reading.IsPromotion,
            reading.PromotionEndsAtUtc,
            reading.StockStatus,
            reading.ObservedAtUtc,
            reading.SourceReference,
            reading.SourceUrl,
            reading.Variant,
            reading.SizeValue,
            reading.SizeUnit,
            reading.Barcode,
            reading.BarcodeScope,
            reading.UnitsPerPack,
            reading.Title,
            reading.RecordedAtUtc,
            reading.IsFresh,
            reading.NeedsPriceException);
    }
}

/// <summary>
/// The confirmed listing whose barcode this one carries (issue #928): the evidence a strong match is
/// made of, and never a decision.
/// </summary>
public sealed record SuperAdminReviewBarcodeMatchResponse(
    long SupplierListingId,
    string SupplierName,
    SuperAdminReviewMasterProductResponse? MasterProduct)
{
    public static SuperAdminReviewBarcodeMatchResponse From(ReviewedBarcodeMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return new SuperAdminReviewBarcodeMatchResponse(
            match.SupplierListingId,
            match.SupplierName,
            match.MasterProduct is null
                ? null
                : SuperAdminReviewMasterProductResponse.From(match.MasterProduct));
    }
}

/// <summary>
/// One supplier listing awaiting a decision, as the review publishes it (issue #928): the supplier,
/// the supplier's own wording and product id, the identity as read, the instants, and the reading it
/// is holding.
///
/// <para>Every text value is the supplier's own, published as <strong>plain text</strong>: nothing
/// here is HTML, nothing is rendered as markup, and the only link the page may offer is
/// <see cref="Url"/>.</para>
/// </summary>
public sealed record SuperAdminReviewSupplierListingResponse(
    long Id,
    int SupplierPresetId,
    string SupplierName,
    string SupplierProductId,
    string? Url,
    string? Title,
    string? Variant,
    decimal? SizeValue,
    MasterProductSizeUnit? SizeUnit,
    int? UnitsPerPack,
    string? Barcode,
    BarcodeScope? BarcodeScope,
    SupplierListingMatchStatus MatchStatus,
    SuperAdminReviewMasterProductResponse? MasterProduct,
    string? WaitingKey,
    string? OriginWaitingKey,
    DateTime FirstSeenAtUtc,
    DateTime LastSeenAtUtc,
    DateTime? LastCheckedAtUtc,
    DateTime? LastNotFoundAtUtc,
    decimal? LastAcceptedPackPrice,
    SuperAdminReviewBarcodeMatchResponse? StrongBarcodeMatch,
    SuperAdminReviewPendingObservationResponse? PendingObservation)
{
    public static SuperAdminReviewSupplierListingResponse From(ReviewedSupplierListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return new SuperAdminReviewSupplierListingResponse(
            listing.Id,
            listing.SupplierPresetId,
            listing.SupplierName,
            listing.SupplierProductId,
            listing.Url,
            listing.Title,
            listing.Variant,
            listing.SizeValue,
            listing.SizeUnit,
            listing.UnitsPerPack,
            listing.Barcode,
            listing.BarcodeScope,
            listing.MatchStatus,
            listing.MasterProduct is null
                ? null
                : SuperAdminReviewMasterProductResponse.From(listing.MasterProduct),
            listing.WaitingKey,
            listing.OriginWaitingKey,
            listing.FirstSeenAtUtc,
            listing.LastSeenAtUtc,
            listing.LastCheckedAtUtc,
            listing.LastNotFoundAtUtc,
            listing.LastAcceptedPackPrice,
            listing.StrongBarcodeMatch is null
                ? null
                : SuperAdminReviewBarcodeMatchResponse.From(listing.StrongBarcodeMatch),
            listing.PendingObservation is null
                ? null
                : SuperAdminReviewPendingObservationResponse.From(listing.PendingObservation));
    }
}

/// <summary>
/// One product awaiting a match, as the review publishes it (issue #928): its search name, its unit
/// label and the <em>count</em> of businesses waiting - never which business asked, for which there
/// is no field.
/// </summary>
public sealed record SuperAdminReviewWaitingProductResponse(
    string WaitingKey,
    string SearchName,
    string UnitLabel,
    int BusinessesWaitingCount,
    DateTime FirstRequestedAtUtc,
    DateTime LastUpdatedAtUtc,
    IReadOnlyList<SuperAdminReviewMatchSuggestionResponse> Suggestions,
    IReadOnlyList<SuperAdminReviewSupplierListingResponse> ProposedListings)
{
    public static SuperAdminReviewWaitingProductResponse From(ReviewedWaitingProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);

        return new SuperAdminReviewWaitingProductResponse(
            product.WaitingKey,
            product.SearchName,
            product.UnitLabel,
            product.BusinessesWaitingCount,
            product.FirstRequestedAtUtc,
            product.LastUpdatedAtUtc,
            [.. product.Suggestions.Select(SuperAdminReviewMatchSuggestionResponse.From)],
            [.. product.ProposedListings.Select(SuperAdminReviewSupplierListingResponse.From)]);
    }
}

/// <summary>One automatic match waiting to be checked, as the review publishes it (issue #928).</summary>
public sealed record SuperAdminReviewAutomaticMatchResponse(
    long SuggestionId,
    string WaitingKey,
    string? SearchName,
    string? UnitLabel,
    SuperAdminReviewMasterProductResponse MasterProduct,
    decimal Confidence,
    string Reason,
    string CollectorRun,
    DateTime AppliedAtUtc)
{
    public static SuperAdminReviewAutomaticMatchResponse From(ReviewedAutomaticMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return new SuperAdminReviewAutomaticMatchResponse(
            match.SuggestionId,
            match.WaitingKey,
            match.SearchName,
            match.UnitLabel,
            SuperAdminReviewMasterProductResponse.From(match.MasterProduct),
            match.Confidence,
            match.Reason,
            match.CollectorRun,
            match.AppliedAtUtc);
    }
}

/// <summary>
/// The whole review queue as <c>GET /api/super-admin/price-catalogue-review</c> reports it (issue
/// #928), in its three sections.
/// </summary>
public sealed record SuperAdminPriceCatalogueReviewResponse(
    IReadOnlyList<SuperAdminReviewWaitingProductResponse> WaitingProducts,
    IReadOnlyList<SuperAdminReviewAutomaticMatchResponse> AutomaticMatches,
    IReadOnlyList<SuperAdminReviewSupplierListingResponse> Listings,
    IReadOnlyList<SuperAdminReviewMasterProductResponse> MasterProducts)
{
    public static SuperAdminPriceCatalogueReviewResponse From(PriceCatalogueReviewQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        return new SuperAdminPriceCatalogueReviewResponse(
            [.. queue.WaitingProducts.Select(SuperAdminReviewWaitingProductResponse.From)],
            [.. queue.AutomaticMatches.Select(SuperAdminReviewAutomaticMatchResponse.From)],
            [.. queue.Listings.Select(SuperAdminReviewSupplierListingResponse.From)],
            [.. queue.MasterProducts.Select(SuperAdminReviewMasterProductResponse.From)]);
    }
}

/// <summary>
/// What a Super Admin submits about one supplier listing (issue #928): confirm it at the identity
/// they reviewed - corrections included, because "correct and confirm" is one decision - or refuse it.
///
/// <para>The listing is named by the route and never by the body, which is this repository's rule for
/// every resource: a request carries what to do, not which row to do it to.</para>
/// </summary>
public sealed record SuperAdminSupplierListingDecisionRequest(
    SupplierListingReviewOutcome Outcome,
    int? UnitsPerPack,
    string? Barcode,
    BarcodeScope? BarcodeScope,
    string? Variant,
    decimal? SizeValue,
    MasterProductSizeUnit? SizeUnit,
    bool ApprovePriceException)
{
    public SupplierListingReviewSubmission ToSubmission(long supplierListingId) =>
        new(
            supplierListingId,
            Outcome,
            UnitsPerPack,
            Barcode,
            BarcodeScope,
            Variant,
            SizeValue,
            SizeUnit,
            ApprovePriceException);
}

/// <summary>What became of one listing decision (issue #928).</summary>
public sealed record SuperAdminSupplierListingDecisionResponse(
    long SupplierListingId,
    SupplierListingReviewOutcome Outcome,
    SupplierListingMatchStatus MatchStatus,
    bool PriceAccepted,
    bool PriceExceptionUsed,
    string? PriceReason,
    long? AcceptedObservationId,
    decimal? AcceptedPackPrice,
    int? ConfirmedUnitsPerPack)
{
    public static SuperAdminSupplierListingDecisionResponse From(SupplierListingReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SuperAdminSupplierListingDecisionResponse(
            result.SupplierListingId,
            result.Outcome,
            result.MatchStatus,
            result.PriceAccepted,
            result.PriceExceptionUsed,
            result.PriceReason,
            result.AcceptedObservationId,
            result.AcceptedPackPrice,
            result.ConfirmedUnitsPerPack);
    }
}

/// <summary>
/// What a Super Admin submits about one waiting product (issue #928): which of the five decisions,
/// and the one thing that decision needs.
///
/// <para>The waiting key is in the body rather than in the route because it is a folded description
/// containing <c>|</c> and <c>:</c>; a key is compared and never parsed, and putting one in a path
/// segment would make a URL's escaping part of the comparison.</para>
/// </summary>
public sealed record SuperAdminProductMatchDecisionRequest(
    string? WaitingKey,
    ProductMatchReviewDecision Decision,
    long? SuggestionId,
    long? MasterProductId,
    string? NewBrand,
    string? NewName,
    decimal? NewSizeValue,
    MasterProductSizeUnit? NewSizeUnit)
{
    public ProductMatchReviewSubmission ToSubmission() =>
        new(
            WaitingKey,
            Decision,
            SuggestionId,
            MasterProductId,
            NewBrand,
            NewName,
            NewSizeValue,
            NewSizeUnit);
}

/// <summary>What became of one match decision (issue #928).</summary>
public sealed record SuperAdminProductMatchDecisionResponse(
    string WaitingKey,
    ProductMatchReviewDecision Decision,
    SuperAdminReviewMasterProductResponse? MasterProduct,
    bool MasterProductCreated,
    long? SuggestionId,
    int ListingsMovedCount)
{
    public static SuperAdminProductMatchDecisionResponse From(ProductMatchReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SuperAdminProductMatchDecisionResponse(
            result.WaitingKey,
            result.Decision,
            result.MasterProduct is null
                ? null
                : SuperAdminReviewMasterProductResponse.From(result.MasterProduct),
            result.MasterProductCreated,
            result.SuggestionId,
            result.ListingsMovedCount);
    }
}

/// <summary>What became of one automatic-match decision (issue #928).</summary>
public sealed record SuperAdminAutomaticMatchDecisionResponse(
    long SuggestionId,
    AutomaticMatchReviewDecision Decision,
    string WaitingKey,
    SuperAdminReviewMasterProductResponse MasterProduct,
    int ListingsMovedCount)
{
    public static SuperAdminAutomaticMatchDecisionResponse From(AutomaticMatchReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SuperAdminAutomaticMatchDecisionResponse(
            result.SuggestionId,
            result.Decision,
            result.WaitingKey,
            SuperAdminReviewMasterProductResponse.From(result.MasterProduct),
            result.ListingsMovedCount);
    }
}
