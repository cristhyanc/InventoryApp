using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// What happened to one submitted collector result row (issue #937, parent #817).
///
/// <para><strong>Four outcomes, because three of them are not the same "no".</strong> A row that was
/// stored as a price, a row that moved a listing to a person's attention, a row that was kept but is
/// not a price yet, and a row that was refused are four different facts, and a response that merged
/// the middle two would leave a collector unable to tell "we kept this and somebody will look" from
/// "we kept this and nothing changed".</para>
///
/// <para>Numbered from 1, for the reason every persisted enum in this application is.</para>
/// </summary>
public enum PriceCollectorRowOutcome
{
    /// <summary>Stored as an accepted observation and now the listing's current price.</summary>
    Accepted = 1,

    /// <summary>
    /// Kept as the listing's pending observation, and the listing's own match status moved - a
    /// confirmed listing flagged for review, or a rejected one proposed again. A person decides what
    /// happens next (#928).
    /// </summary>
    SentToReview = 2,

    /// <summary>
    /// Kept as the listing's pending observation, with the listing left exactly as it was. The
    /// ordinary answer for a listing nobody has confirmed yet: it is evidence, and it is not a price.
    /// </summary>
    Pending = 3,

    /// <summary>Refused, with a reason. Nothing about the listing changed.</summary>
    Rejected = 4,
}

/// <summary>
/// The identity of one supplier listing (issue #937): the facts that say the listing is still the
/// same thing it was when somebody confirmed it.
///
/// <para>It is deliberately not the whole listing. The title moves when a retailer restyles a page
/// and the URL moves when it reorganises one; what must not move silently is <em>what is in the
/// pack</em> - the variant, the individual size, the barcode and how many come together - because
/// each of those changes what a per-unit price means. The supplier's own product id is not here
/// either: it is the key the listing was found by, so a row that reached a listing at all already
/// matches it.</para>
/// </summary>
/// <param name="Variant">The supplier's wording for which variant this is, or <see langword="null"/>.</param>
/// <param name="SizeValue">The size of one individual item, or <see langword="null"/>.</param>
/// <param name="SizeUnit">What that size is measured in, or <see langword="null"/>.</param>
/// <param name="Barcode">The published barcode, or <see langword="null"/>.</param>
/// <param name="BarcodeScope">Whether that barcode is the unit's or the carton's.</param>
/// <param name="UnitsPerPack">How many individual items the pack holds, or <see langword="null"/>.</param>
public sealed record SupplierListingIdentity(
    string? Variant,
    decimal? SizeValue,
    MasterProductSizeUnit? SizeUnit,
    string? Barcode,
    BarcodeScope? BarcodeScope,
    int? UnitsPerPack);

/// <summary>
/// What is to be done about one submitted row (issue #937): the outcome to report, the reason when
/// there is one, whether the listing's match status moves, and where the reading is stored.
/// </summary>
/// <param name="Outcome">What the response reports for this row.</param>
/// <param name="Reason">
/// Why, for every outcome but <see cref="PriceCollectorRowOutcome.Accepted"/>. An accepted row needs
/// no reason, and the three others always carry one - a collector that is told "rejected" and not
/// told why cannot fix anything.
/// </param>
/// <param name="NewMatchStatus">
/// The match status the listing is to be moved to, or <see langword="null"/> to leave it exactly as
/// it is. A rediscovery never moves a status on its own; only an identity change does.
/// </param>
/// <param name="StoresPendingObservation">
/// Whether the reading is kept as the listing's pending observation, replacing whatever pending
/// reading was there. Deliberately separate from the outcome: the pending store is where a reading
/// waits for #928, and both <see cref="PriceCollectorRowOutcome.Pending"/> and
/// <see cref="PriceCollectorRowOutcome.SentToReview"/> use it.
/// </param>
/// <param name="RecordsAcceptedObservation">
/// Whether the reading becomes an accepted <c>SupplierPriceObservation</c> and the listing's current
/// price. True for exactly <see cref="PriceCollectorRowOutcome.Accepted"/>, and stated rather than
/// derived so no caller has to re-decide it.
/// </param>
public sealed record PriceCollectorRowDecision(
    PriceCollectorRowOutcome Outcome,
    string? Reason,
    SupplierListingMatchStatus? NewMatchStatus,
    bool StoresPendingObservation,
    bool RecordsAcceptedObservation);

/// <summary>
/// The rules a submitted collector result is held to (issue #937, parent #817): the owner's decision
/// of 27 October 2026 that "the backend must still enforce product matching, quantities and price
/// validation before accepting results".
///
/// <para><strong>The collector's output is untrusted input, and this policy is where that is
/// true.</strong> A search run by a model can match the wrong product, read the wrong pack size or
/// lose a decimal point, and each of those is a wrong "cheapest supplier" rather than a visible
/// error. So nothing a collector sends decides anything on its own: a row reaches a stored price only
/// by matching a listing a person already confirmed, in every field that changes what the price
/// means, inside a band around the price that listing already had.</para>
///
/// <para><strong>Three answers, not two.</strong> A row is accepted, held, or refused, and the
/// difference between the middle and the last is the whole design: a reading that disagrees with a
/// confirmed listing is <em>evidence</em> that something changed, so it is kept where a person can
/// see it and the listing is flagged - never discarded, and never accepted either. Only a check that
/// says the row is not about this listing at all, or was not read when or where it says, refuses it
/// outright.</para>
///
/// <para><strong>It stores nothing and reads nothing.</strong> Every method here is a pure function
/// of the values it is handed, so the rules can be stated over a table of cases rather than through
/// an endpoint; which listing a row belongs to, and whether that listing's work item was issued by
/// this run, are the import's questions and are answered before this policy is asked anything.</para>
/// </summary>
public static class PriceCollectorResultPolicy
{
    /// <summary>
    /// The largest batch of rows one request may carry: two hundred, the owner's figure.
    ///
    /// <para>It bounds one request and not one search. A search that found more packs than this is
    /// reported in several batches and finished by its own outcome call, which is why the outcome
    /// carries the total the collector submitted - see <c>PriceCollectorWorkItemPolicy</c>.</para>
    /// </summary>
    public const int MaxRowsPerBatch = 200;

    /// <summary>
    /// How far below the last accepted price a new one may be and still be accepted: half of it.
    /// </summary>
    public const decimal PriceBandFloorFactor = 0.5m;

    /// <summary>And how far above: twice it.</summary>
    public const decimal PriceBandCeilingFactor = 2m;

    /// <summary>What a caller who submitted more rows than one request may carry is told.</summary>
    public static string BatchTooLargeMessage { get; } =
        $"A price collector results batch carries at most {MaxRowsPerBatch} rows.";

    /// <summary>What a caller whose row cites a work-item id nothing issued is told.</summary>
    public const string UnknownWorkItemReason = "unknown work item";

    /// <summary>What a caller whose row cites another run's work item is told.</summary>
    public const string WorkItemFromAnotherRunReason = "work item belongs to another run";

    /// <summary>
    /// What a caller whose row names a different supplier from the one its work item was issued for
    /// is told. A search at one retailer cannot answer for another's shelf.
    /// </summary>
    public const string SupplierDiffersFromWorkItemReason =
        "supplier differs from the work item's supplier";

    /// <summary>
    /// What a caller whose row would move an existing listing to another product is told. The exact
    /// wording the issue names, because it is what a reviewer searches the response for.
    /// </summary>
    public const string ListingBelongsToAnotherProductReason = "listing belongs to another product";

    /// <summary>What a caller who sends rows after its search was already answered is told.</summary>
    public const string SearchAlreadyAnsweredReason = "the search has already been answered";

    /// <summary>
    /// What a caller who sent the same idempotency key twice <em>within one batch</em> is told. A
    /// repeat across batches is answered with the first outcome instead; a repeat inside one batch
    /// has no first outcome to answer with.
    /// </summary>
    public const string DuplicateRowInBatchReason =
        "duplicate work item and supplier product id in this batch";

    /// <summary>What a caller whose reading predates the run that is reporting it is told.</summary>
    public const string ObservedBeforeRunStartedReason = "observed before the run started";

    /// <summary>What a caller whose reading is dated in the future is told.</summary>
    public const string ObservedInFutureReason = "observed in the future";

    /// <summary>What a caller whose source link is not on the supplier's own site is told.</summary>
    public const string SourceLinkHostMismatchReason =
        "source link is not on the supplier's own site";

    /// <summary>
    /// What a caller whose row disagrees with the supplier master record about whether the
    /// published price includes GST is told.
    ///
    /// <para>The stored basis is the supplier record's own (<c>SupplierPreset.PricesIncludeGst</c>,
    /// issue #931), because that is the fact this application keeps about how a retailer publishes
    /// prices - but the row carries one too, and the two disagreeing means one of them is about a
    /// different page. Comparing an inclusive price with an exclusive one is off by the GST rate,
    /// which is the one mistake a price comparison must not be able to make, so the row is refused
    /// rather than stored on whichever basis won.</para>
    /// </summary>
    public const string PriceBasisDiffersFromSupplierReason =
        "price basis differs from the supplier's published basis";

    /// <summary>What a caller who rediscovered a listing a person rejected is told.</summary>
    public const string ListingRejectedReason = "listing was rejected";

    /// <summary>Why a confirmed listing was flagged, or a rejected one proposed again.</summary>
    public const string IdentityChangedReason =
        "variant, individual size, barcode or pack quantity changed";

    /// <summary>Why a price was held instead of accepted.</summary>
    public const string PriceOutsideBandReason =
        "price is more than 50% below or 100% above the last accepted price";

    /// <summary>Why an unconfirmed listing's reading is held rather than priced.</summary>
    public const string ListingNotConfirmedReason =
        "listing is not confirmed, so the reading is held as a pending observation";

    /// <summary>Whether <paramref name="rowCount"/> is a batch size this application accepts.</summary>
    public static bool IsAcceptedBatchSize(int rowCount) => rowCount <= MaxRowsPerBatch;

    /// <summary>Refuses a batch larger than one request may carry. Nothing is stored.</summary>
    /// <exception cref="DomainValidationException">The batch holds more than <see cref="MaxRowsPerBatch"/> rows.</exception>
    public static void RequireAcceptedBatchSize(int rowCount)
    {
        if (!IsAcceptedBatchSize(rowCount))
        {
            throw new DomainValidationException(BatchTooLargeMessage);
        }
    }

    /// <summary>
    /// Whether <paramref name="packPrice"/> is inside the band around
    /// <paramref name="lastAcceptedPackPrice"/>: no more than 50% below it and no more than 100%
    /// above it.
    ///
    /// <para>Both edges are <em>inside</em> the band, because "no more than 50% below" includes
    /// exactly 50% below - the boundary belongs to the accepted side deliberately, so a supplier
    /// running a half-price special is not sent to review every time it does.</para>
    /// </summary>
    public static bool IsWithinPriceBand(decimal lastAcceptedPackPrice, decimal packPrice) =>
        packPrice >= lastAcceptedPackPrice * PriceBandFloorFactor
            && packPrice <= lastAcceptedPackPrice * PriceBandCeilingFactor;

    /// <summary>
    /// Whether <paramref name="observedAtUtc"/> is inside the window the run has been open for: at or
    /// after the run started, and not after <paramref name="asOfUtc"/>.
    ///
    /// <para>Both ends matter and for different reasons. A reading from before the run started was
    /// not read by this run, whatever it says; a reading dated in the future would be treated as the
    /// freshest price there is by <see cref="SupplierPriceFreshnessPolicy"/> for as long as the clock
    /// took to catch up.</para>
    /// </summary>
    public static bool IsObservedWithinRun(
        DateTime observedAtUtc,
        DateTime runStartedAtUtc,
        DateTime asOfUtc) =>
        observedAtUtc >= runStartedAtUtc && observedAtUtc <= asOfUtc;

    /// <summary>
    /// Whether <paramref name="sourceUrl"/> was read from the site
    /// <paramref name="supplierSiteUrl"/> names: the same host, or a subdomain of it.
    ///
    /// <para><strong>An absent value is not a mismatch.</strong> A supplier master record with no
    /// website recorded states no host to compare against, and a row with no link states none
    /// either; refusing those would reject every supplier nobody has filled a website in for, which
    /// is a configuration gap rather than a wrong price. <strong>A near-miss is</strong>: the check
    /// is on host labels, so <c>alpha.example.evil.test</c> does not pass as
    /// <c>alpha.example</c> - a string <c>EndsWith</c> would have let it.</para>
    /// </summary>
    public static bool SourceLinkMatchesSupplier(string? supplierSiteUrl, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(supplierSiteUrl) || string.IsNullOrWhiteSpace(sourceUrl))
        {
            return true;
        }

        if (!TryHost(supplierSiteUrl, out var supplierHost))
        {
            // A supplier record whose website is not a URL states no host either. The site is
            // validated where it is written (SupplierUrlPolicy); nothing is invented here.
            return true;
        }

        if (!TryHost(sourceUrl, out var sourceHost))
        {
            return false;
        }

        return string.Equals(sourceHost, supplierHost, StringComparison.OrdinalIgnoreCase)
            || sourceHost.EndsWith("." + supplierHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="observed"/> describes a different thing from <paramref name="stored"/>
    /// in any of the fields that change what a per-unit price means.
    ///
    /// <para>The variant is the supplier's own wording, so it is compared with its ends trimmed and
    /// without regard to case: a restyled page is not a new product. <strong>The barcode is compared
    /// exactly</strong>, because <c>0012345678905</c> and <c>12345678905</c> are different codes and
    /// the leading zeros are the whole difference.</para>
    /// </summary>
    public static bool HasIdentityChanged(SupplierListingIdentity stored, SupplierListingIdentity observed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(observed);

        return !string.Equals(Fold(stored.Variant), Fold(observed.Variant), StringComparison.OrdinalIgnoreCase)
            || stored.SizeValue != observed.SizeValue
            || stored.SizeUnit != observed.SizeUnit
            || !string.Equals(stored.Barcode, observed.Barcode, StringComparison.Ordinal)
            || stored.BarcodeScope != observed.BarcodeScope
            || stored.UnitsPerPack != observed.UnitsPerPack;
    }

    /// <summary>
    /// What to do about a row for a listing this catalogue has never held: record it as the
    /// collector's own proposal, and hold its price.
    ///
    /// <para>A newly found pack is a match nobody has checked, so its first reading is never a price:
    /// it waits in the pending store until #928 confirms the listing really is the product the search
    /// was for. Accepting it would publish the matcher's guess as a supplier's offer.</para>
    /// </summary>
    public static PriceCollectorRowDecision DecideForNewListing() =>
        new(
            PriceCollectorRowOutcome.Pending,
            ListingNotConfirmedReason,
            SupplierListingMatchStatus.Proposed,
            StoresPendingObservation: true,
            RecordsAcceptedObservation: false);

    /// <summary>
    /// What to do about a row for a listing this catalogue already holds.
    ///
    /// <para>The order is the rule. An identity change is asked about first, for every status, because
    /// a listing that is no longer the same thing cannot have its price compared with the price of
    /// what it used to be - not even to decide whether that price is plausible. Only then does a
    /// confirmed listing's price meet the band.</para>
    /// </summary>
    /// <param name="currentStatus">The match status the listing holds now.</param>
    /// <param name="stored">The identity the catalogue holds for it.</param>
    /// <param name="observed">The identity the collector read.</param>
    /// <param name="packPrice">The pack price the collector read.</param>
    /// <param name="lastAcceptedPackPrice">
    /// The listing's last accepted pack price, or <see langword="null"/> when it has never had one -
    /// a listing with no accepted price has no band, which is the issue's own wording.
    /// </param>
    public static PriceCollectorRowDecision DecideForKnownListing(
        SupplierListingMatchStatus currentStatus,
        SupplierListingIdentity stored,
        SupplierListingIdentity observed,
        decimal packPrice,
        decimal? lastAcceptedPackPrice)
    {
        var identityChanged = HasIdentityChanged(stored, observed);

        switch (currentStatus)
        {
            case SupplierListingMatchStatus.Confirmed when identityChanged:
                return Review(SupplierListingMatchStatus.NeedsReview, IdentityChangedReason);

            case SupplierListingMatchStatus.Confirmed:
                return lastAcceptedPackPrice is { } last && !IsWithinPriceBand(last, packPrice)
                    ? Review(SupplierListingMatchStatus.NeedsReview, PriceOutsideBandReason)
                    : new PriceCollectorRowDecision(
                        PriceCollectorRowOutcome.Accepted,
                        Reason: null,
                        NewMatchStatus: null,
                        StoresPendingObservation: false,
                        RecordsAcceptedObservation: true);

            // Proposed again rather than flagged: a listing somebody refused, which now describes
            // something else, is a new proposal rather than a refusal that needs re-reading.
            case SupplierListingMatchStatus.Rejected when identityChanged:
                return Review(SupplierListingMatchStatus.Proposed, IdentityChangedReason);

            case SupplierListingMatchStatus.Rejected:
                return new PriceCollectorRowDecision(
                    PriceCollectorRowOutcome.Rejected,
                    ListingRejectedReason,
                    NewMatchStatus: null,
                    StoresPendingObservation: false,
                    RecordsAcceptedObservation: false);

            // Proposed and NeedsReview: the reading is kept and the listing is left alone. There is
            // nothing to flag - nobody has confirmed this listing, so nothing about it disagrees with
            // a decision - and nothing to accept, because an unconfirmed listing's price is not a
            // supplier's offer for this product until somebody says the listing is this product.
            default:
                return new PriceCollectorRowDecision(
                    PriceCollectorRowOutcome.Pending,
                    ListingNotConfirmedReason,
                    NewMatchStatus: null,
                    StoresPendingObservation: true,
                    RecordsAcceptedObservation: false);
        }
    }

    private static PriceCollectorRowDecision Review(
        SupplierListingMatchStatus newStatus,
        string reason) =>
        new(
            PriceCollectorRowOutcome.SentToReview,
            reason,
            newStatus,
            StoresPendingObservation: true,
            RecordsAcceptedObservation: false);

    private static string Fold(string? value) => (value ?? string.Empty).Trim();

    private static bool TryHost(string value, out string host)
    {
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            // A leading "www." is a convention rather than a different site, and a supplier record
            // and a product page disagree about it constantly.
            host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host[4..]
                : uri.Host;

            return true;
        }

        host = string.Empty;
        return false;
    }
}
