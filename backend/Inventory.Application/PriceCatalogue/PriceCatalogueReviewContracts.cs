using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One master product as the Super Admin review names it (issue #928, parent #817): its key and the
/// four values that say what the product <em>is</em>.
///
/// <para>The brand is here, unlike in the member-facing read of #938, and the difference is who is
/// asking: a business may see only the name and size of the master product its own mapping is matched
/// to, because that is all a matched status needs to be readable, while the reviewer is deciding
/// <em>which</em> product a description is and cannot do that without the brand. Nothing about any
/// business travels with it either way - a master product carries no business identity at all.</para>
/// </summary>
public sealed record ReviewedMasterProduct(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit);

/// <summary>
/// One match suggestion as the review reads it (issue #928): what the matcher thinks a waiting key
/// is, how sure it was, why, which run said so, and what has happened to the suggestion since.
///
/// <para>Exactly one of <see cref="MasterProduct"/> and the four <c>Suggested*</c> values is present,
/// which is <c>ProductMatchSuggestion</c>'s own rule: a suggestion proposes an existing master
/// product or describes a new one. The description is what the "create one" action is prefilled
/// from.</para>
/// </summary>
public sealed record ReviewedMatchSuggestion(
    long Id,
    ReviewedMasterProduct? MasterProduct,
    string? SuggestedBrand,
    string? SuggestedName,
    decimal? SuggestedSizeValue,
    MasterProductSizeUnit? SuggestedSizeUnit,
    decimal Confidence,
    string Reason,
    string CollectorRun,
    ProductMatchSuggestionStatus Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// The reading one listing is holding pending (issue #937), as the review shows it: what was read,
/// when, why it was held, and whether it could become a price today.
///
/// <para><see cref="IsFresh"/> and <see cref="NeedsPriceException"/> are the server's own answers
/// rather than something a page works out: the freshness limit belongs to the supplier master record
/// and the band belongs to <c>PriceCollectorResultPolicy</c>, so a client that computed either would
/// be a second implementation of a rule that decides whether a price is published.</para>
/// </summary>
public sealed record ReviewedPendingObservation(
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
    bool NeedsPriceException);

/// <summary>
/// A confirmed listing somewhere in the catalogue that carries this listing's barcode, in the same
/// scope (issue #928): the "strong match" the review list shows first.
///
/// <para>It is evidence and not a decision - see
/// <see cref="SupplierListingReviewPolicy.IsStrongBarcodeMatch"/>. The confirmed listing's own
/// supplier and master product are named because that is what makes the evidence readable: "this is
/// the code Woolworths' confirmed Kinder Bueno 43 g carries" is the sentence a reviewer acts on.
/// </para>
/// </summary>
public sealed record ReviewedBarcodeMatch(
    long SupplierListingId,
    string SupplierName,
    ReviewedMasterProduct? MasterProduct);

/// <summary>
/// One supplier listing awaiting a decision (issue #928): everything the collector submitted about
/// it, where it hangs, when it was first and last seen, and the reading it is holding.
///
/// <para>Every text value is the supplier's own wording, stored as published and published again
/// here as <strong>plain text</strong>: nothing on this path renders, sanitises or interprets it, and
/// the only link a page may follow is <see cref="Url"/> - the issue's last criterion.</para>
/// </summary>
public sealed record ReviewedSupplierListing(
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
    ReviewedMasterProduct? MasterProduct,
    string? WaitingKey,
    string? OriginWaitingKey,
    DateTime FirstSeenAtUtc,
    DateTime LastSeenAtUtc,
    DateTime? LastCheckedAtUtc,
    DateTime? LastNotFoundAtUtc,
    decimal? LastAcceptedPackPrice,
    ReviewedBarcodeMatch? StrongBarcodeMatch,
    ReviewedPendingObservation? PendingObservation);

/// <summary>
/// One product awaiting a match (issue #928), by the only two things that describe it: the search
/// name and the unit label the businesses that want it priced recorded.
///
/// <para><strong><see cref="BusinessesWaitingCount"/> is a count and never a list.</strong> The issue
/// is explicit: a waiting product is shown "by search name and unit label only, with a count of
/// businesses waiting, never which business asked". The count is what tells a reviewer whether a
/// decision matters to one business or to twelve; which businesses they are is not theirs to know,
/// and there is no field here for it. The collector's own work list publishes no count at all, which
/// is a different answer for a different caller: the collector is an application searching a shelf,
/// and a Super Admin is the person deciding the shared catalogue.</para>
/// </summary>
public sealed record ReviewedWaitingProduct(
    string WaitingKey,
    string SearchName,
    string UnitLabel,
    int BusinessesWaitingCount,
    DateTime FirstRequestedAtUtc,
    DateTime LastUpdatedAtUtc,
    IReadOnlyList<ReviewedMatchSuggestion> Suggestions,
    IReadOnlyList<ReviewedSupplierListing> ProposedListings);

/// <summary>
/// One automatic match waiting to be checked (issue #941's work, as #928 presents it): the run that
/// applied it, how sure it was, why, and what it matched.
///
/// <para><see cref="SearchName"/> and <see cref="UnitLabel"/> come from the mappings the match
/// applied to and are <see langword="null"/> when none is left - a business that turned tracking off
/// afterwards leaves the suggestion with nothing to describe it but its key, and inventing a
/// description for it would be worse than saying there is none.</para>
/// </summary>
public sealed record ReviewedAutomaticMatch(
    long SuggestionId,
    string WaitingKey,
    string? SearchName,
    string? UnitLabel,
    ReviewedMasterProduct MasterProduct,
    decimal Confidence,
    string Reason,
    string CollectorRun,
    DateTime AppliedAtUtc);

/// <summary>
/// The whole review queue (issue #928), in the three sections the page is built from: the products
/// nothing has matched, the matches a collector run applied by itself, and the listings waiting for a
/// decision.
///
/// <para>The automatic matches are a section of their own because the issue asks for exactly that -
/// "listed separately, flagged auto-matched" - and because what a reviewer does with one is confirm
/// or undo rather than match.</para>
///
/// <para><strong><see cref="MasterProducts"/> is the whole shared catalogue, and it is here because
/// one of the criteria needs it</strong>: a reviewer may "pick another existing master product", and
/// a picker that could only offer the products a suggestion happened to name would make that
/// criterion reachable for some keys and not others. It is platform data about products - a brand, a
/// name and a size, about no business at all - and it is published unbounded and ordered rather than
/// paged, because the catalogue is a list of products a person keeps and the alternative is a search
/// surface nobody has needed yet. If it ever outgrows one response, a search endpoint is its own
/// change.</para>
/// </summary>
public sealed record PriceCatalogueReviewQueue(
    IReadOnlyList<ReviewedWaitingProduct> WaitingProducts,
    IReadOnlyList<ReviewedAutomaticMatch> AutomaticMatches,
    IReadOnlyList<ReviewedSupplierListing> Listings,
    IReadOnlyList<ReviewedMasterProduct> MasterProducts);

/// <summary>
/// What a Super Admin submits about one supplier listing (issue #928): confirm it - at the identity
/// they reviewed - or refuse it.
///
/// <para><strong>"Correct and confirm" is this same submission.</strong> The identity values are the
/// ones to store, so a reviewer who changes the pack quantity or the barcode scope and confirms sends
/// one request rather than an edit followed by a confirmation: the correction and the decision are
/// one operation, and a listing that was corrected and then not confirmed would be a row somebody
/// edited for no decision.</para>
///
/// <para>Every value here is <em>submitted</em> and nothing here is trusted: the Domain policies in
/// <c>Inventory.Domain.PriceCatalogue</c> decide what is storable.</para>
/// </summary>
/// <param name="SupplierListingId">The listing being decided.</param>
/// <param name="Outcome">Confirm or refuse.</param>
/// <param name="UnitsPerPack">The pack quantity as reviewed, or <see langword="null"/> for unknown.</param>
/// <param name="Barcode">The barcode as reviewed, or <see langword="null"/> for none.</param>
/// <param name="BarcodeScope">Whether that barcode is the unit's or the carton's.</param>
/// <param name="Variant">The supplier's wording for which variant this is, as reviewed.</param>
/// <param name="SizeValue">The individual item's size as reviewed.</param>
/// <param name="SizeUnit">What that size is measured in.</param>
/// <param name="ApprovePriceException">
/// Whether the reviewer explicitly approves accepting a pending price that is outside the band around
/// the listing's last accepted price. Read only when the band is what would otherwise refuse the
/// reading, and audited separately from the decision when it is used.
/// </param>
public sealed record SupplierListingReviewSubmission(
    long SupplierListingId,
    SupplierListingReviewOutcome Outcome,
    int? UnitsPerPack,
    string? Barcode,
    BarcodeScope? BarcodeScope,
    string? Variant,
    decimal? SizeValue,
    MasterProductSizeUnit? SizeUnit,
    bool ApprovePriceException);

/// <summary>
/// What became of one listing decision (issue #928): the status the listing ended in, and what
/// happened to the reading it was holding.
///
/// <para>The price half is reported in full, including the reason nothing was accepted, because
/// "confirmed with no current price" is a real and expected answer and a reviewer who was not told
/// why would have to guess whether the confirmation worked.</para>
/// </summary>
public sealed record SupplierListingReviewResult(
    long SupplierListingId,
    SupplierListingReviewOutcome Outcome,
    SupplierListingMatchStatus MatchStatus,
    bool PriceAccepted,
    bool PriceExceptionUsed,
    string? PriceReason,
    long? AcceptedObservationId,
    decimal? AcceptedPackPrice,
    int? ConfirmedUnitsPerPack);

/// <summary>
/// What a Super Admin submits about one waiting product (issue #928): which of the five decisions,
/// and the one thing that decision needs.
///
/// <para>The waiting key is in the body rather than in the route because it is a folded description
/// containing <c>|</c> and <c>:</c> - a key is compared and never parsed, and putting one in a path
/// segment would make a URL's escaping part of the comparison.</para>
/// </summary>
/// <param name="WaitingKey">The key being decided, as the review list published it.</param>
/// <param name="Decision">Which of the five decisions this is.</param>
/// <param name="SuggestionId">
/// The suggestion being accepted or refused, for those two decisions and for no other.
/// </param>
/// <param name="MasterProductId">
/// The existing master product to match, for <see cref="ProductMatchReviewDecision.ChooseMasterProduct"/>.
/// </param>
/// <param name="NewBrand">The brand of a master product to create.</param>
/// <param name="NewName">Its name or variant.</param>
/// <param name="NewSizeValue">The size of one item.</param>
/// <param name="NewSizeUnit">What that size is measured in.</param>
public sealed record ProductMatchReviewSubmission(
    string? WaitingKey,
    ProductMatchReviewDecision Decision,
    long? SuggestionId,
    long? MasterProductId,
    string? NewBrand,
    string? NewName,
    decimal? NewSizeValue,
    MasterProductSizeUnit? NewSizeUnit);

/// <summary>
/// What became of one match decision (issue #928): what the key is matched to now, whether a master
/// product was created for it, and how many supplier listings moved with it.
///
/// <para><strong>There is deliberately no count of the mappings a match moved</strong>, which is
/// #941's own restraint for the same reason: that count is about businesses. The listing count is
/// not - a supplier listing is a fact about a supplier's shelf - and it is what tells a reviewer
/// whether the prices a key had collected followed the decision.</para>
/// </summary>
public sealed record ProductMatchReviewResult(
    string WaitingKey,
    ProductMatchReviewDecision Decision,
    ReviewedMasterProduct? MasterProduct,
    bool MasterProductCreated,
    long? SuggestionId,
    int ListingsMovedCount);

/// <summary>
/// What became of one automatic-match decision (issue #928): the suggestion that was confirmed or
/// undone, the master product it was about, and the listings an undo moved back.
/// </summary>
public sealed record AutomaticMatchReviewResult(
    long SuggestionId,
    AutomaticMatchReviewDecision Decision,
    string WaitingKey,
    ReviewedMasterProduct MasterProduct,
    int ListingsMovedCount);
