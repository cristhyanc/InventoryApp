using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPriceCatalogueReviewStore"/> (issue #928, parent #817):
/// the reads the Super Admin review page is built from and the writes its four decisions make.
///
/// <para><strong>Five platform tables, and four columns of one tenant-owned row.</strong> It writes
/// <c>MasterProducts</c> - this is their only writer in the application, and the only way a master
/// product is ever created - <c>SupplierListings</c>, <c>ProductMatchSuggestions</c>,
/// <c>SupplierPriceObservations</c> and <c>SupplierListingPendingObservations</c>, and sets the
/// status, the master product, the match source and the instant on the <c>ProductPriceTracking</c>
/// rows that carry the key being decided. Nothing else is written, and the enforcer is what says so
/// rather than this class: the save that touches mappings runs inside
/// <c>AppDbContext.BeginPriceCatalogueReview</c>, whose window accepts exactly those four columns on
/// exactly that entity type and refuses every other tenant-owned write
/// (<c>BusinessOwnershipEnforcer</c>).</para>
///
/// <para><strong>The mappings are read across businesses, and that read is this issue's own reviewed
/// decision.</strong> A platform-admin request carries a <em>denied</em> <c>BusinessScope</c>, so the
/// central query filters match no mapping at all and a review of the shared catalogue would see
/// nothing to decide. The filters are therefore ignored - in <em>one</em> place,
/// <see cref="MappingsAcrossBusinesses"/>, which every statement that needs a mapping goes through.
/// What crosses that boundary is the waiting key, the search name and unit label it folds from, the
/// mapping's own match state and - for the queue alone - the <em>count</em> of distinct businesses
/// waiting under a key, which is the one thing issue #928 asks for and bounds in the same sentence:
/// "a count of businesses waiting, never which business asked". No business id, name or product id
/// leaves this adapter in any answer. See AGENTS.md § Tenant ownership and data isolation and
/// docs/architecture.md § Super Admin review of the supplier price catalogue.</para>
///
/// <para><strong>Each decision is one <c>BEGIN IMMEDIATE</c> transaction, and the write lock is taken
/// before the reads.</strong> Every decision here is a comparison against the state the reviewer was
/// looking at - is this listing still waiting, is this suggestion still tentative, is anything still
/// awaiting this key - and a refusal of a stale one. That is only authoritative while nothing can
/// commit between the question and the write, which is exactly what the immediate write lock gives
/// (the same lock #938's switch, #896's machine delete and #941's matches take). The authoritative
/// read, the comparison and the mutation are one operation.</para>
///
/// <para><strong>Every rule is the Domain's, and this class is the sequence.</strong>
/// <see cref="SupplierListingReviewPolicy"/> decides which listings may be decided and what confirming
/// one does with the reading it holds, <see cref="ProductMatchReviewPolicy"/> decides which
/// suggestions and mappings a decision reaches, <see cref="MasterProductPolicy"/> and
/// <see cref="SupplierListingPolicy"/> decide what may be stored,
/// <see cref="SupplierPriceFreshnessPolicy"/> decides whether a reading is still guidance, and
/// <see cref="SupplierListingPriceStatePolicy"/> decides what the listing then currently costs.</para>
/// </summary>
public sealed class EfPriceCatalogueReviewStore : IPriceCatalogueReviewStore
{
    private readonly AppDbContext _db;

    public EfPriceCatalogueReviewStore(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The one place this adapter reaches a price-tracking mapping, and the one
    /// <c>IgnoreQueryFilters</c> statement in it.
    ///
    /// <para>It is a method rather than three statements so that "where does this surface read a
    /// tenant-owned table?" has one answer a reviewer can see. It takes no predicate, page, sort or
    /// column list from anybody: each caller adds its own <c>Where</c> over the <em>waiting key</em>
    /// or the match status, which are product descriptions and states, and the queue's own projection
    /// is the only one that reads the business key at all - for a count.</para>
    /// </summary>
    private IQueryable<ProductPriceTracking> MappingsAcrossBusinesses() =>
        _db.ProductPriceTrackings.IgnoreQueryFilters();

    /// <inheritdoc />
    public async Task<PriceCatalogueReviewQueue> ReadQueueAsync(
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        // The reviewed cross-business read: the mappings that are waiting or auto-matched, projected
        // to a product description, a state and the ownership key the count below is made from.
        var mappings = await MappingsAcrossBusinesses()
            .AsNoTracking()
            .Where(mapping => mapping.Status == ProductPriceTrackingStatus.Requested
                || mapping.Status == ProductPriceTrackingStatus.AutoMatched)
            .Select(mapping => new MappingFacts(
                mapping.BusinessId,
                mapping.Status,
                mapping.MasterProductId,
                mapping.WaitingKey,
                mapping.SearchName,
                mapping.UnitLabel,
                mapping.CreatedAtUtc,
                mapping.UpdatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var suggestions = await _db.ProductMatchSuggestions
            .AsNoTracking()
            .Include(suggestion => suggestion.MasterProduct)
            .Where(suggestion => suggestion.Status == ProductMatchSuggestionStatus.Tentative
                || suggestion.Status == ProductMatchSuggestionStatus.AutoApplied)
            .OrderBy(suggestion => suggestion.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var awaitingDecision = await _db.SupplierListings
            .AsNoTracking()
            .Include(listing => listing.SupplierPreset)
            .Include(listing => listing.MasterProduct)
            .Where(listing => listing.MatchStatus == SupplierListingMatchStatus.Proposed
                || listing.MatchStatus == SupplierListingMatchStatus.NeedsReview)
            .OrderBy(listing => listing.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Every confirmed listing that carries a barcode, which is what a strong match is evidence
        // against. The whole set, deliberately: a barcode match across suppliers is the point - the
        // issue's own examples are a JB Metro code that is a Woolworths listing's - so bounding this
        // to one product would hide exactly the evidence it exists for.
        var confirmed = await _db.SupplierListings
            .AsNoTracking()
            .Include(listing => listing.SupplierPreset)
            .Include(listing => listing.MasterProduct)
            .Where(listing => listing.MatchStatus == SupplierListingMatchStatus.Confirmed
                && listing.Barcode != null)
            .OrderBy(listing => listing.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var listingIds = awaitingDecision.ConvertAll(listing => listing.Id);

        var pending = (await _db.SupplierListingPendingObservations
            .AsNoTracking()
            .Where(reading => listingIds.Contains(reading.SupplierListingId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(reading => reading.SupplierListingId);

        var acceptedIds = awaitingDecision
            .Where(listing => listing.LatestObservationId is not null)
            .Select(listing => listing.LatestObservationId!.Value)
            .ToList();

        var acceptedPrices = await _db.SupplierPriceObservations
            .AsNoTracking()
            .Where(observation => acceptedIds.Contains(observation.Id))
            .Select(observation => new { observation.Id, observation.PackPrice })
            .ToDictionaryAsync(row => row.Id, row => row.PackPrice, cancellationToken)
            .ConfigureAwait(false);

        var listings = awaitingDecision
            .ConvertAll(listing => Describe(listing, confirmed, pending, acceptedPrices, asOfUtc));

        // Barcode matches first, which is the issue's own ordering, and then by supplier and key so
        // the list is stable between reads rather than in whatever order the database answered.
        var ordered = listings
            .OrderByDescending(listing => listing.StrongBarcodeMatch is not null)
            .ThenBy(listing => listing.SupplierName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(listing => listing.Id)
            .ToList();

        // The whole shared catalogue, ordered as a person reads it, for the "pick another existing
        // master product" decision. Platform data about products and about no business.
        var catalogue = await _db.MasterProducts
            .AsNoTracking()
            .OrderBy(master => master.Brand)
            .ThenBy(master => master.Name)
            .ThenBy(master => master.SizeValue)
            .Select(master => new ReviewedMasterProduct(
                master.Id,
                master.Brand,
                master.Name,
                master.SizeValue,
                master.SizeUnit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PriceCatalogueReviewQueue(
            WaitingProducts(mappings, suggestions, ordered),
            AutomaticMatches(mappings, suggestions),
            ordered,
            catalogue);
    }

    /// <inheritdoc />
    public async Task<SupplierListingReviewResult?> DecideListingAsync(
        SupplierListingReviewSubmission submission,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var listing = await _db.SupplierListings
            .Include(candidate => candidate.SupplierPreset)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == submission.SupplierListingId,
                cancellationToken)
            .ConfigureAwait(false);

        if (listing is null)
        {
            return null;
        }

        // The authoritative state, read inside the lock: a listing another reviewer or a collector
        // run has moved since this page was read is refused rather than decided against the old one.
        SupplierListingReviewPolicy.RequireAwaitingReview(listing.MatchStatus);

        var pending = await _db.SupplierListingPendingObservations
            .SingleOrDefaultAsync(
                reading => reading.SupplierListingId == listing.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (submission.Outcome == SupplierListingReviewOutcome.Rejected)
        {
            return await RefuseListingAsync(listing, pending, asOfUtc, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ConfirmListingAsync(
                listing,
                pending,
                submission,
                asOfUtc,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses one listing: the refusal is recorded on the row and the reading it was holding is
    /// removed.
    ///
    /// <para><strong>The identity is left exactly as it was</strong>, because a refusal is not an
    /// edit: what the row says is what the supplier published, and it is the evidence of what was
    /// refused. <strong>The held reading goes</strong>, because a reading nobody will ever price is
    /// not a disagreement waiting for a decision any more - and a rediscovery that changes the
    /// listing's identity makes it a proposal again (#937), with a reading of its own.</para>
    /// </summary>
    private async Task<SupplierListingReviewResult> RefuseListingAsync(
        SupplierListing listing,
        SupplierListingPendingObservation? pending,
        DateTime asOfUtc,
        SqliteWriteLockTransaction transaction,
        CancellationToken cancellationToken)
    {
        listing.MatchStatus = SupplierListingMatchStatus.Rejected;
        listing.UpdatedAtUtc = asOfUtc;

        if (pending is not null)
        {
            _db.SupplierListingPendingObservations.Remove(pending);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SupplierListingReviewResult(
            listing.Id,
            SupplierListingReviewOutcome.Rejected,
            listing.MatchStatus,
            PriceAccepted: false,
            PriceExceptionUsed: false,
            PriceReason: null,
            AcceptedObservationId: null,
            AcceptedPackPrice: null,
            ConfirmedUnitsPerPack: null);
    }

    /// <summary>
    /// Confirms one listing at the identity the reviewer reviewed, and then decides what becomes of
    /// the reading it was holding.
    ///
    /// <para>The submitted identity is validated <strong>before</strong> anything is changed, so a
    /// value this catalogue would not store leaves the listing exactly as it was - decision included.
    /// </para>
    ///
    /// <para><strong>A confirmation that moves the identity withdraws the price the listing was
    /// publishing</strong> (<see cref="SupplierListingReviewPolicy.ConfirmationWithdrawsCurrentPrice"/>),
    /// because the comparison reads the listing's pack quantity beside the observation the listing
    /// points at: leaving the pointer would publish a 24-pack's price as a 36-pack's. The observation
    /// itself is kept - <c>SupplierPriceObservations</c> is append-only - and the listing is confirmed
    /// with no current price unless the reading it holds becomes one on its own merits, which is the
    /// same "priced on the next run" answer a refused reading already gives.</para>
    /// </summary>
    private async Task<SupplierListingReviewResult> ConfirmListingAsync(
        SupplierListing listing,
        SupplierListingPendingObservation? pending,
        SupplierListingReviewSubmission submission,
        DateTime asOfUtc,
        SqliteWriteLockTransaction transaction,
        CancellationToken cancellationToken)
    {
        var variant = SupplierListingPolicy.NormaliseVariant(submission.Variant);
        var (sizeValue, sizeUnit) = SupplierListingPolicy.NormaliseIndividualSize(
            submission.SizeValue,
            submission.SizeUnit);
        var unitsPerPack = SupplierListingPolicy.NormaliseUnitsPerPack(submission.UnitsPerPack);
        var (barcode, barcodeScope) = SupplierListingPolicy.NormaliseBarcode(
            submission.Barcode,
            submission.BarcodeScope);

        var confirmedIdentity = new SupplierListingIdentity(
            variant,
            sizeValue,
            sizeUnit,
            barcode,
            barcodeScope,
            unitsPerPack);

        // Read before the identity is written: the price the listing is publishing was accepted
        // against the identity it holds now, and that is what decides whether it survives.
        var storedIdentity = IdentityOf(listing);
        var accepted = await LastAcceptedPriceAsync(listing, cancellationToken).ConfigureAwait(false);

        var decision = SupplierListingReviewPolicy.DecidePendingPrice(
            confirmedIdentity,
            pending is null
                ? null
                : new ReviewedPendingReading(
                    IdentityOf(pending),
                    pending.PackPrice,
                    pending.ObservedAtUtc),
            accepted?.PackPrice,
            FreshnessLimitHoursOf(listing),
            asOfUtc,
            submission.ApprovePriceException);

        // The price the listing was publishing is a price of the pack it was read against, so a
        // confirmation that moves the identity takes it away rather than re-dividing it.
        var withdrawsCurrentPrice = accepted is not null
            && SupplierListingReviewPolicy.ConfirmationWithdrawsCurrentPrice(
                storedIdentity,
                confirmedIdentity);

        listing.Variant = variant;
        listing.SizeValue = sizeValue;
        listing.SizeUnit = sizeUnit;
        listing.UnitsPerPack = unitsPerPack;
        listing.Barcode = barcode;
        listing.BarcodeScope = barcodeScope;
        listing.MatchStatus = SupplierListingMatchStatus.Confirmed;
        listing.UpdatedAtUtc = asOfUtc;

        if (withdrawsCurrentPrice)
        {
            WithdrawCurrentPrice(listing);
        }

        SupplierPriceObservation? observation = null;

        if (decision.AcceptsPrice)
        {
            observation = AcceptPrice(listing, pending!, asOfUtc);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (observation is not null)
        {
            // After the save, because the state names the observation's own key and a row this
            // transaction created has none until then - the shape #937's own price state uses.
            // A withdrawn price is no longer the price this reading is compared with or replaces:
            // the accepted reading is this pack's first, whenever it was observed.
            ApplyCurrentPriceState(listing, observation, withdrawsCurrentPrice ? null : accepted);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SupplierListingReviewResult(
            listing.Id,
            SupplierListingReviewOutcome.Confirmed,
            listing.MatchStatus,
            decision.AcceptsPrice,
            decision.UsesPriceException,
            decision.Reason,
            observation?.Id,
            observation?.PackPrice,
            unitsPerPack);
    }

    /// <summary>
    /// Stores the held reading as the accepted price it has just become, and clears it from the
    /// pending store: a disagreement that has been resolved must not still be shown to a reviewer.
    ///
    /// <para>The observation keeps the <em>reading's</em> own provenance and instant - the run that
    /// read it and when - rather than recording the review as a price check, because what is stored
    /// is a price a supplier published at an instant and the review did not read a shelf.</para>
    /// </summary>
    private SupplierPriceObservation AcceptPrice(
        SupplierListing listing,
        SupplierListingPendingObservation pending,
        DateTime asOfUtc)
    {
        var observation = new SupplierPriceObservation
        {
            SupplierListingId = listing.Id,
            PriceCollectorRunId = pending.PriceCollectorRunId,
            PackPrice = pending.PackPrice,
            RegularPackPrice = pending.RegularPackPrice,
            Currency = pending.Currency,
            PriceBasis = pending.PriceBasis,
            SupplierGstRate = pending.SupplierGstRate,
            IsPromotion = pending.IsPromotion,
            PromotionEndsAtUtc = pending.PromotionEndsAtUtc,
            StockStatus = pending.StockStatus,
            ObservedAtUtc = pending.ObservedAtUtc,
            SourceReference = pending.SourceReference,
            SourceUrl = pending.SourceUrl,
            CreatedAtUtc = asOfUtc,
        };

        _db.SupplierPriceObservations.Add(observation);
        _db.SupplierListingPendingObservations.Remove(pending);

        return observation;
    }

    /// <summary>
    /// Takes away the price the listing was publishing, because the pack just confirmed is not the
    /// pack that price was read against (<see cref="SupplierListingReviewPolicy.ConfirmationWithdrawsCurrentPrice"/>).
    ///
    /// <para>All three price-check facts go together, which is the one coherent "never checked" state
    /// <see cref="SupplierListingPriceStatePolicy"/> describes and the database's own
    /// <c>CK_SupplierListings_PriceStateConsistent</c> allows: a listing cannot hold a last successful
    /// check with no observation behind it, and a price change it no longer has a price for is not a
    /// change. The invariant is asserted here rather than assumed, for the reason
    /// <see cref="ApplyCurrentPriceState"/> asserts it after a write.</para>
    ///
    /// <para><strong>The observation is not touched.</strong> It stays in
    /// <c>SupplierPriceObservations</c>, which is append-only history: what a supplier charged at an
    /// instant stays true after a reviewer corrected what pack it was. What is withdrawn is only this
    /// listing's claim that it is the price <em>now</em>.</para>
    /// </summary>
    private static void WithdrawCurrentPrice(SupplierListing listing)
    {
        listing.LatestObservationId = null;
        listing.LastCheckedAtUtc = null;
        listing.LastPriceChangeAtUtc = null;

        SupplierListingPriceStatePolicy.RequireConsistentState(
            listing.LatestObservationId,
            listing.LastCheckedAtUtc,
            listing.LastPriceChangeAtUtc);
    }

    /// <summary>
    /// Points the listing at its new accepted price, through the one rule that decides a listing's
    /// three price-check facts (<see cref="SupplierListingPriceStatePolicy"/>).
    ///
    /// <para>A reading observed no later than the one the listing already holds moves nothing: a
    /// listing's price, its freshness and its last change may only ever move forwards, which is the
    /// same sentence that policy states about an absence. The reading is still stored and still an
    /// accepted price - <c>SupplierPriceObservations</c> is append-only history.</para>
    /// </summary>
    private static void ApplyCurrentPriceState(
        SupplierListing listing,
        SupplierPriceObservation observation,
        AcceptedPrice? accepted)
    {
        if (!SupplierListingPriceStatePolicy.ReplacesCurrentReading(
            accepted?.ObservedAtUtc,
            observation.ObservedAtUtc))
        {
            return;
        }

        var state = SupplierListingPriceStatePolicy.StateAfterRun(
            accepted?.PackPrice,
            listing.LastPriceChangeAtUtc,
            [
                new AcceptedSupplierReading(
                    observation.Id,
                    observation.ObservedAtUtc,
                    observation.PackPrice),
            ]);

        listing.LatestObservationId = state.LatestObservationId;
        listing.LastCheckedAtUtc = state.LastCheckedAtUtc;
        listing.LastPriceChangeAtUtc = state.LastPriceChangeAtUtc;

        SupplierListingPriceStatePolicy.RequireConsistentState(
            listing.LatestObservationId,
            listing.LastCheckedAtUtc,
            listing.LastPriceChangeAtUtc);
    }

    /// <inheritdoc />
    public async Task<ProductMatchReviewResult?> DecideMatchAsync(
        ProductMatchReviewSubmission submission,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var waitingKey = (submission.WaitingKey ?? string.Empty).Trim();

        if (waitingKey.Length == 0)
        {
            return null;
        }

        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        // Tracked, because a match is about to write four of their columns through SaveChanges.
        var mappings = await MappingsAcrossBusinesses()
            .Where(mapping => mapping.WaitingKey == waitingKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var awaiting = mappings
            .Where(mapping => ProductMatchReviewPolicy.MayMatch(mapping.Status))
            .ToList();

        if (awaiting.Count == 0)
        {
            // Nothing is waiting under this key: it has been matched, recorded as not found or
            // withdrawn since the page was read, so there is no decision for anybody to make.
            return null;
        }

        var suggestions = await _db.ProductMatchSuggestions
            .Where(suggestion => suggestion.WaitingKey == waitingKey)
            .OrderBy(suggestion => suggestion.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var decided = submission.Decision switch
        {
            ProductMatchReviewDecision.MarkNotFound =>
                MarkNotFound(awaiting, asOfUtc),
            ProductMatchReviewDecision.RejectSuggestion =>
                RejectSuggestion(submission, suggestions, asOfUtc),
            ProductMatchReviewDecision.AcceptSuggestion =>
                await AcceptSuggestionAsync(
                        waitingKey,
                        submission,
                        suggestions,
                        awaiting,
                        asOfUtc,
                        cancellationToken)
                    .ConfigureAwait(false),
            ProductMatchReviewDecision.ChooseMasterProduct =>
                await ChooseMasterProductAsync(
                        waitingKey,
                        submission,
                        suggestions,
                        awaiting,
                        asOfUtc,
                        cancellationToken)
                    .ConfigureAwait(false),
            ProductMatchReviewDecision.CreateMasterProduct =>
                await CreateMasterProductAsync(
                        waitingKey,
                        submission,
                        suggestions,
                        awaiting,
                        asOfUtc,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new DomainValidationException(
                ProductMatchReviewPolicy.IncompleteDecisionMessage),
        };

        // One save for everything the decision implied, inside the one window that accepts the
        // mappings' match columns on this denied scope. The lease is closed before the commit: it
        // grants a write, never a transaction.
        using (_db.BeginPriceCatalogueReview())
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Read after the save, because a master product this decision created only has its key then.
        return new ProductMatchReviewResult(
            waitingKey,
            submission.Decision,
            decided.MasterProduct is null ? null : Describe(decided.MasterProduct),
            decided.MasterProductCreated,
            decided.Suggestion?.Id,
            decided.ListingsMoved);
    }

    /// <inheritdoc />
    public async Task<AutomaticMatchReviewResult?> DecideAutomaticMatchAsync(
        long suggestionId,
        AutomaticMatchReviewDecision decision,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var suggestion = await _db.ProductMatchSuggestions
            .Include(candidate => candidate.MasterProduct)
            .SingleOrDefaultAsync(candidate => candidate.Id == suggestionId, cancellationToken)
            .ConfigureAwait(false);

        if (suggestion is null)
        {
            return null;
        }

        ProductMatchReviewPolicy.RequireMayConfirmOrUndo(suggestion.Status);

        if (suggestion.MasterProduct is not { } master)
        {
            // An applied match always names the master product it applied; a row that does not is
            // not one, whatever its status says.
            throw new DomainConflictException(ProductMatchReviewPolicy.NotAnAutomaticMatchMessage);
        }

        var mappings = await MappingsAcrossBusinesses()
            .Where(mapping => mapping.WaitingKey == suggestion.WaitingKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var listingsMoved = decision == AutomaticMatchReviewDecision.Confirm
            ? ConfirmAutomaticMatch(suggestion, master, mappings, asOfUtc)
            : await UndoAutomaticMatchAsync(suggestion, master, mappings, asOfUtc, cancellationToken)
                .ConfigureAwait(false);

        using (_db.BeginPriceCatalogueReview())
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AutomaticMatchReviewResult(
            suggestion.Id,
            decision,
            suggestion.WaitingKey,
            Describe(master),
            listingsMoved);
    }

    /// <summary>
    /// Confirms one automatic match: the mappings it applied to are settled and the suggestion
    /// records that a person accepted it.
    ///
    /// <para><strong>The match source is left exactly as it is.</strong> How the match was
    /// <em>made</em> is still the collector's automatic one; what changed is that a person has
    /// checked it, which is what the mapping's status and the suggestion's own
    /// <see cref="ProductMatchSuggestionStatus.Accepted"/> row record. Rewriting the source would
    /// erase the fact that a matcher proposed it, which is the first thing anybody asks of a product
    /// being priced as something else.</para>
    /// </summary>
    private static int ConfirmAutomaticMatch(
        ProductMatchSuggestion suggestion,
        MasterProduct master,
        IReadOnlyList<ProductPriceTracking> mappings,
        DateTime asOfUtc)
    {
        foreach (var mapping in mappings.Where(mapping =>
            mapping.Status == ProductPriceTrackingStatus.AutoMatched
            && mapping.MasterProductId == master.Id))
        {
            mapping.Status = ProductPriceTrackingStatus.Matched;
            mapping.UpdatedAtUtc = asOfUtc;

            ProductPriceTrackingPolicy.RequireConsistentMatch(
                mapping.Status,
                mapping.MasterProductId,
                mapping.MatchSource);
        }

        suggestion.Status = ProductMatchSuggestionStatus.Accepted;
        suggestion.UpdatedAtUtc = asOfUtc;

        return 0;
    }

    /// <summary>
    /// Undoes one automatic match: the mappings it applied to return to awaiting a match, the
    /// listings that are still only proposals move back to the key they were discovered under, and
    /// the pair is recorded as undone so it is never applied automatically again.
    ///
    /// <para>What is deliberately <strong>not</strong> moved is in
    /// <see cref="ProductMatchReviewPolicy.MovesBackOnUndo"/> and
    /// <see cref="ProductMatchReviewPolicy.ReturnsToWaiting"/>: a confirmed listing is a person's own
    /// decision about that pack, a flagged one is a confirmed listing whose shelf moved, a refused one
    /// is a refusal, and a mapping a person settled or a later run matched to something else was never
    /// this suggestion's to move. No price, observation or check instant is touched by any of it.</para>
    /// </summary>
    private async Task<int> UndoAutomaticMatchAsync(
        ProductMatchSuggestion suggestion,
        MasterProduct master,
        IReadOnlyList<ProductPriceTracking> mappings,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        foreach (var mapping in mappings.Where(mapping => ProductMatchReviewPolicy.ReturnsToWaiting(
            mapping.Status,
            mapping.MasterProductId,
            master.Id)))
        {
            mapping.Status = ProductPriceTrackingStatus.Requested;
            mapping.MasterProductId = null;
            mapping.MatchSource = null;
            mapping.UpdatedAtUtc = asOfUtc;

            ProductPriceTrackingPolicy.RequireConsistentMatch(
                mapping.Status,
                mapping.MasterProductId,
                mapping.MatchSource);
        }

        var listings = await _db.SupplierListings
            .Where(listing => listing.MasterProductId == master.Id
                && listing.OriginWaitingKey == suggestion.WaitingKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var moved = 0;

        foreach (var listing in listings.Where(listing =>
            ProductMatchReviewPolicy.MovesBackOnUndo(listing.MatchStatus)))
        {
            listing.WaitingKey = suggestion.WaitingKey;
            listing.MasterProductId = null;
            listing.UpdatedAtUtc = asOfUtc;

            SupplierListingPolicy.RequireExactlyOneOwner(listing.MasterProductId, listing.WaitingKey);

            moved++;
        }

        suggestion.Status = ProductMatchSuggestionStatus.Undone;
        suggestion.UpdatedAtUtc = asOfUtc;

        return moved;
    }

    /// <summary>
    /// Records that nothing in the catalogue is this product: every mapping still awaiting a match
    /// under the key takes the answer, which is what stops the same search running every run.
    /// </summary>
    private static DecidedMatch MarkNotFound(
        IReadOnlyList<ProductPriceTracking> awaiting,
        DateTime asOfUtc)
    {
        foreach (var mapping in awaiting)
        {
            mapping.Status = ProductPriceTrackingStatus.NotFound;
            mapping.MasterProductId = null;
            mapping.MatchSource = null;
            mapping.UpdatedAtUtc = asOfUtc;

            ProductPriceTrackingPolicy.RequireConsistentMatch(
                mapping.Status,
                mapping.MasterProductId,
                mapping.MatchSource);
        }

        return new DecidedMatch(null, false, null, 0);
    }

    /// <summary>
    /// Refuses one tentative suggestion and leaves the key waiting. The row is kept for ever, which
    /// is what stops the matcher applying that pair again.
    /// </summary>
    private static DecidedMatch RejectSuggestion(
        ProductMatchReviewSubmission submission,
        IReadOnlyList<ProductMatchSuggestion> suggestions,
        DateTime asOfUtc)
    {
        var suggestion = Require(submission.SuggestionId, suggestions);

        ProductMatchReviewPolicy.RequireMayDecide(suggestion.Status);

        suggestion.Status = ProductMatchSuggestionStatus.Rejected;
        suggestion.UpdatedAtUtc = asOfUtc;

        return new DecidedMatch(null, false, suggestion, 0);
    }

    /// <summary>
    /// Accepts one tentative suggestion: the existing master product it named, or - when it described
    /// a new one - that description, created and matched.
    /// </summary>
    private async Task<DecidedMatch> AcceptSuggestionAsync(
        string waitingKey,
        ProductMatchReviewSubmission submission,
        List<ProductMatchSuggestion> suggestions,
        IReadOnlyList<ProductPriceTracking> awaiting,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        var suggestion = Require(submission.SuggestionId, suggestions);

        ProductMatchReviewPolicy.RequireMayDecide(suggestion.Status);

        if (suggestion.MasterProductId is { } existingId)
        {
            var existing = await FindMasterProductAsync(existingId, cancellationToken)
                .ConfigureAwait(false);

            return await ApplyMatchAsync(
                    new MatchDecisionContext(waitingKey, suggestions, awaiting),
                    existing,
                    created: false,
                    suggestion,
                    asOfUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // The suggestion described a new master product. It is created from that description - which
        // is still the reviewer's decision, because accepting is what creates it: nothing automatic
        // in this application ever writes a MasterProducts row.
        var master = await CreateAsync(
                suggestion.SuggestedBrand,
                suggestion.SuggestedName,
                suggestion.SuggestedSizeValue,
                suggestion.SuggestedSizeUnit,
                asOfUtc,
                cancellationToken)
            .ConfigureAwait(false);

        return await ApplyMatchAsync(
                new MatchDecisionContext(waitingKey, suggestions, awaiting),
                master,
                created: true,
                suggestion,
                asOfUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Matches the key to another existing master product the reviewer named.</summary>
    private async Task<DecidedMatch> ChooseMasterProductAsync(
        string waitingKey,
        ProductMatchReviewSubmission submission,
        List<ProductMatchSuggestion> suggestions,
        IReadOnlyList<ProductPriceTracking> awaiting,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        if (submission.MasterProductId is not { } masterProductId)
        {
            throw new DomainValidationException(ProductMatchReviewPolicy.IncompleteDecisionMessage);
        }

        var master = await FindMasterProductAsync(masterProductId, cancellationToken)
            .ConfigureAwait(false);

        return await ApplyMatchAsync(
                new MatchDecisionContext(waitingKey, suggestions, awaiting),
                master,
                created: false,
                acceptedSuggestion: null,
                asOfUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Creates the master product the reviewer described and matches the key to it.</summary>
    private async Task<DecidedMatch> CreateMasterProductAsync(
        string waitingKey,
        ProductMatchReviewSubmission submission,
        List<ProductMatchSuggestion> suggestions,
        IReadOnlyList<ProductPriceTracking> awaiting,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        var master = await CreateAsync(
                submission.NewBrand,
                submission.NewName,
                submission.NewSizeValue,
                submission.NewSizeUnit,
                asOfUtc,
                cancellationToken)
            .ConfigureAwait(false);

        return await ApplyMatchAsync(
                new MatchDecisionContext(waitingKey, suggestions, awaiting),
                master,
                created: true,
                acceptedSuggestion: null,
                asOfUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies one match: every mapping still awaiting it names the master product with the Super
    /// Admin as the source, the listings the key had collected move to that master product keeping
    /// their own match status and their origin key, and the decision is recorded on a suggestion row.
    ///
    /// <para><strong>A suggestion row is written because the match has to be readable as one.</strong>
    /// The collector results endpoint resolves a waiting key to its master product through the
    /// <see cref="ProductMatchSuggestionStatus.Accepted"/> and
    /// <see cref="ProductMatchSuggestionStatus.AutoApplied"/> rows of that key (#937), so a reviewer's
    /// match that left none would be invisible to a run searching while it was made. When the reviewer
    /// accepted a suggestion that named this very master product, that row <em>is</em> the record and
    /// becomes <c>Accepted</c>; otherwise this surface writes its own, under its own label.</para>
    ///
    /// <para><strong>Every other tentative suggestion of the key is refused by the same
    /// decision.</strong> Choosing one product is saying the others were wrong, and the refusal is
    /// what stops the matcher applying one of them automatically the next time the same description
    /// is searched for. Their reason, confidence and run label are left exactly as the matcher wrote
    /// them: the row is evidence of what was proposed, and only its status is this decision's to
    /// write.</para>
    /// </summary>
    private async Task<DecidedMatch> ApplyMatchAsync(
        MatchDecisionContext context,
        MasterProduct master,
        bool created,
        ProductMatchSuggestion? acceptedSuggestion,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        var (waitingKey, suggestions, awaiting) = context;

        foreach (var mapping in awaiting)
        {
            mapping.Status = ProductPriceTrackingStatus.Matched;
            mapping.MasterProductId = master.Id;
            mapping.MatchSource = ProductPriceTrackingMatchSource.SuperAdmin;
            mapping.UpdatedAtUtc = asOfUtc;

            ProductPriceTrackingPolicy.RequireConsistentMatch(
                mapping.Status,
                mapping.MasterProductId,
                mapping.MatchSource);
        }

        var moved = await MoveListingsAsync(waitingKey, master, asOfUtc, cancellationToken)
            .ConfigureAwait(false);

        foreach (var superseded in suggestions.Where(suggestion =>
            ProductMatchReviewPolicy.MayDecide(suggestion.Status)
            && !ReferenceEquals(suggestion, acceptedSuggestion)))
        {
            superseded.Status = ProductMatchSuggestionStatus.Rejected;
            superseded.UpdatedAtUtc = asOfUtc;
        }

        var record = acceptedSuggestion;

        if (record is not null)
        {
            record.Status = ProductMatchSuggestionStatus.Accepted;
            record.UpdatedAtUtc = asOfUtc;
        }

        if (record is null || record.MasterProductId != master.Id)
        {
            // Either the reviewer chose or created the master product themselves, or they accepted a
            // suggestion that described one - whose row cannot name a master product, because one
            // proposal per row is that table's own rule. Either way the key's accepted match needs a
            // row that names it.
            record = new ProductMatchSuggestion
            {
                WaitingKey = waitingKey,
                MasterProductId = master.Id,
                // A person's own decision rather than a matcher's estimate: the column is a
                // proportion of certainty and it decides nothing here.
                Confidence = 1m,
                Reason = created
                    ? ProductMatchReviewPolicy.CreatedByReviewerReason
                    : ProductMatchReviewPolicy.ChosenByReviewerReason,
                CollectorRun = ProductMatchReviewPolicy.ReviewCollectorRun,
                Status = ProductMatchSuggestionStatus.Accepted,
                CreatedAtUtc = asOfUtc,
                UpdatedAtUtc = asOfUtc,
            };

            _db.ProductMatchSuggestions.Add(record);
            suggestions.Add(record);
        }

        return new DecidedMatch(master, created, record, moved);
    }

    /// <summary>
    /// Moves every listing still held under one waiting key onto the master product a match applied,
    /// keeping the key it was discovered under so an undo can move it back and leaving its match
    /// status exactly as it is.
    ///
    /// <para>This is #941's own rule and not a second one: a match moves where a listing
    /// <em>hangs</em>, and it never decides that somebody confirmed the listing is that product - so a
    /// proposal stays a proposal and no pack size is confirmed by it. The active key has to be cleared
    /// because a listing belongs to exactly one of the two owners, which the database checks, and
    /// <c>OriginWaitingKey</c> exists precisely so the provenance survives that.</para>
    /// </summary>
    private async Task<int> MoveListingsAsync(
        string waitingKey,
        MasterProduct master,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        var listings = await _db.SupplierListings
            .Where(listing => listing.WaitingKey == waitingKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var listing in listings)
        {
            listing.MasterProductId = master.Id;
            listing.OriginWaitingKey ??= waitingKey;
            listing.WaitingKey = null;
            listing.UpdatedAtUtc = asOfUtc;

            SupplierListingPolicy.RequireExactlyOneOwner(listing.MasterProductId, listing.WaitingKey);
        }

        return listings.Count;
    }

    /// <summary>
    /// Creates one master product from a description, held to <see cref="MasterProductPolicy"/>'s own
    /// rules and to the catalogue's uniqueness rule.
    ///
    /// <para>The duplicate check is made <strong>inside</strong> the caller's own
    /// <c>BEGIN IMMEDIATE</c> transaction, so it is authoritative: nothing can insert the same
    /// description between the question and the write. The unique index over the normalised key is the
    /// database's own backstop under it.</para>
    /// </summary>
    private async Task<MasterProduct> CreateAsync(
        string? brand,
        string? name,
        decimal? sizeValue,
        MasterProductSizeUnit? sizeUnit,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        if (sizeValue is not { } size || sizeUnit is not { } unit)
        {
            throw new DomainValidationException(ProductMatchReviewPolicy.IncompleteDecisionMessage);
        }

        var validatedBrand = MasterProductPolicy.NormaliseBrand(brand);
        var validatedName = MasterProductPolicy.NormaliseName(name);
        var validatedSize = MasterProductPolicy.NormaliseSize(size, unit);
        var normalisedKey = MasterProductPolicy.NormalisedKey(
            validatedBrand,
            validatedName,
            validatedSize,
            unit);

        if (await _db.MasterProducts
            .AnyAsync(master => master.NormalisedKey == normalisedKey, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new DomainConflictException(ProductMatchReviewPolicy.DuplicateMasterProductMessage);
        }

        var created = new MasterProduct
        {
            Brand = validatedBrand,
            Name = validatedName,
            SizeValue = validatedSize,
            SizeUnit = unit,
            NormalisedKey = normalisedKey,
            CreatedAtUtc = asOfUtc,
            UpdatedAtUtc = asOfUtc,
        };

        _db.MasterProducts.Add(created);

        // Saved here, inside the caller's transaction, because everything the match then writes names
        // this row by its key: a mapping, a listing and a suggestion all carry a foreign key to it,
        // and a key the database has not assigned yet is a zero. Nothing is committed - a later
        // refusal rolls this insert back with the rest of the decision.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return created;
    }

    private async Task<MasterProduct> FindMasterProductAsync(
        long masterProductId,
        CancellationToken cancellationToken) =>
        await _db.MasterProducts
            .SingleOrDefaultAsync(master => master.Id == masterProductId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new DomainValidationException(
            ProductMatchReviewPolicy.UnknownMasterProductMessage);

    /// <summary>
    /// The suggestion a decision names, which must be one of this key's own: a reviewer deciding a
    /// suggestion of another key is deciding a product they are not looking at.
    /// </summary>
    private static ProductMatchSuggestion Require(
        long? suggestionId,
        IReadOnlyList<ProductMatchSuggestion> suggestions)
    {
        if (suggestionId is not { } id)
        {
            throw new DomainValidationException(ProductMatchReviewPolicy.IncompleteDecisionMessage);
        }

        // The suggestions here are this key's own, so a suggestion that is not among them belongs to
        // another key - a product the reviewer is not looking at.
        return suggestions.SingleOrDefault(suggestion => suggestion.Id == id)
            ?? throw new DomainValidationException(
                ProductMatchReviewPolicy.SuggestionBelongsToAnotherKeyMessage);
    }

    private async Task<AcceptedPrice?> LastAcceptedPriceAsync(
        SupplierListing listing,
        CancellationToken cancellationToken)
    {
        if (listing.LatestObservationId is not { } latestId)
        {
            return null;
        }

        return await _db.SupplierPriceObservations
            .AsNoTracking()
            .Where(observation => observation.Id == latestId)
            .Select(observation => new AcceptedPrice(
                observation.PackPrice,
                observation.ObservedAtUtc))
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The freshness limit a listing's reading is measured against: the supplier master record's own
    /// stored hours, never a limit decided from a supplier's name (issue #925).
    /// </summary>
    private static int FreshnessLimitHoursOf(SupplierListing listing) =>
        listing.SupplierPreset?.PriceCheckFreshnessHours
            ?? SupplierPriceFreshnessPolicy.DailyCheckLimitHours;

    private static IReadOnlyList<ReviewedWaitingProduct> WaitingProducts(
        IReadOnlyList<MappingFacts> mappings,
        IReadOnlyList<ProductMatchSuggestion> suggestions,
        IReadOnlyList<ReviewedSupplierListing> listings) =>
        [.. mappings
            .Where(mapping => mapping.Status == ProductPriceTrackingStatus.Requested)
            .GroupBy(mapping => mapping.WaitingKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var representative = Representative(group);

                return new ReviewedWaitingProduct(
                    group.Key,
                    representative.SearchName,
                    representative.UnitLabel,
                    // Distinct businesses, which is what the issue asks for: two of one business's
                    // own products that fold to one key are one business waiting.
                    group.Select(mapping => mapping.BusinessId).Distinct().Count(),
                    group.Min(mapping => mapping.CreatedAtUtc),
                    group.Max(mapping => mapping.UpdatedAtUtc),
                    [.. suggestions
                        .Where(suggestion =>
                            string.Equals(suggestion.WaitingKey, group.Key, StringComparison.Ordinal)
                            && ProductMatchSuggestionPolicy.AwaitsReview(suggestion.Status))
                        .Select(Describe)],
                    [.. listings.Where(listing => string.Equals(
                        listing.WaitingKey,
                        group.Key,
                        StringComparison.Ordinal))]);
            })
            .OrderBy(product => product.SearchName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(product => product.UnitLabel, StringComparer.OrdinalIgnoreCase)];

    private static IReadOnlyList<ReviewedAutomaticMatch> AutomaticMatches(
        IReadOnlyList<MappingFacts> mappings,
        IReadOnlyList<ProductMatchSuggestion> suggestions)
    {
        var byKey = mappings
            .GroupBy(mapping => mapping.WaitingKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, Representative, StringComparer.Ordinal);

        return
        [
            .. suggestions
                .Where(suggestion =>
                    suggestion.Status == ProductMatchSuggestionStatus.AutoApplied
                    && suggestion.MasterProduct is not null)
                .Select(suggestion =>
                {
                    byKey.TryGetValue(suggestion.WaitingKey, out var representative);

                    return new ReviewedAutomaticMatch(
                        suggestion.Id,
                        suggestion.WaitingKey,
                        representative?.SearchName,
                        representative?.UnitLabel,
                        Describe(suggestion.MasterProduct!),
                        suggestion.Confidence,
                        suggestion.Reason,
                        suggestion.CollectorRun,
                        suggestion.UpdatedAtUtc);
                })
                .OrderBy(match => match.SearchName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.SuggestionId),
        ];
    }

    /// <summary>
    /// The spelling a waiting key is shown under: the ordinally first of the mappings' own, which is
    /// the rule the collector work list states (#923's representative rule). So the description a
    /// reviewer reads is the description the collector was given.
    /// </summary>
    private static MappingFacts Representative(IEnumerable<MappingFacts> mappings) =>
        mappings
            .OrderBy(mapping => mapping.SearchName, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.UnitLabel, StringComparer.Ordinal)
            .First();

    private static ReviewedSupplierListing Describe(
        SupplierListing listing,
        IReadOnlyList<SupplierListing> confirmed,
        IReadOnlyDictionary<long, SupplierListingPendingObservation> pending,
        IReadOnlyDictionary<long, decimal> acceptedPrices,
        DateTime asOfUtc)
    {
        var reading = pending.TryGetValue(listing.Id, out var held) ? held : null;
        var lastAccepted = listing.LatestObservationId is { } latestId
            && acceptedPrices.TryGetValue(latestId, out var price)
                ? price
                : (decimal?)null;

        var match = confirmed.FirstOrDefault(candidate =>
            candidate.Id != listing.Id
            && SupplierListingReviewPolicy.IsStrongBarcodeMatch(
                listing.Barcode,
                listing.BarcodeScope,
                candidate.Barcode,
                candidate.BarcodeScope));

        var matchedMasterProduct = match?.MasterProduct is null ? null : Describe(match.MasterProduct);
        var barcodeMatch = match is null
            ? null
            : new ReviewedBarcodeMatch(
                match.Id,
                match.SupplierPreset?.Name ?? string.Empty,
                matchedMasterProduct);

        return new ReviewedSupplierListing(
            listing.Id,
            listing.SupplierPresetId,
            listing.SupplierPreset?.Name ?? string.Empty,
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
            listing.MasterProduct is null ? null : Describe(listing.MasterProduct),
            listing.WaitingKey,
            listing.OriginWaitingKey,
            listing.CreatedAtUtc,
            listing.UpdatedAtUtc,
            listing.LastCheckedAtUtc,
            listing.LastNotFoundAtUtc,
            lastAccepted,
            barcodeMatch,
            reading is null ? null : Describe(reading, lastAccepted, listing, asOfUtc));
    }

    private static ReviewedPendingObservation Describe(
        SupplierListingPendingObservation reading,
        decimal? lastAcceptedPackPrice,
        SupplierListing listing,
        DateTime asOfUtc) =>
        new(
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
            SupplierPriceFreshnessPolicy.IsFresh(
                reading.ObservedAtUtc,
                asOfUtc,
                FreshnessLimitHoursOf(listing)),
            SupplierListingReviewPolicy.NeedsPriceException(
                lastAcceptedPackPrice,
                reading.PackPrice));

    private static ReviewedMatchSuggestion Describe(ProductMatchSuggestion suggestion) =>
        new(
            suggestion.Id,
            suggestion.MasterProduct is null ? null : Describe(suggestion.MasterProduct),
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

    private static ReviewedMasterProduct Describe(MasterProduct master) =>
        new(master.Id, master.Brand, master.Name, master.SizeValue, master.SizeUnit);

    private static SupplierListingIdentity IdentityOf(SupplierListingPendingObservation reading) =>
        new(
            reading.Variant,
            reading.SizeValue,
            reading.SizeUnit,
            reading.Barcode,
            reading.BarcodeScope,
            reading.UnitsPerPack);

    /// <summary>
    /// The identity the catalogue holds for one listing, which is the identity its accepted price was
    /// read against.
    /// </summary>
    private static SupplierListingIdentity IdentityOf(SupplierListing listing) =>
        new(
            listing.Variant,
            listing.SizeValue,
            listing.SizeUnit,
            listing.Barcode,
            listing.BarcodeScope,
            listing.UnitsPerPack);

    /// <summary>
    /// What one price-tracking mapping contributes to the queue: a product description, a state, and
    /// the ownership key the count of waiting businesses is made from.
    ///
    /// <para>It is <c>private</c> and never leaves this adapter: what the port answers carries the
    /// count and nothing else of it - no business id, no product id and no mapping id.</para>
    /// </summary>
    private sealed record MappingFacts(
        int BusinessId,
        ProductPriceTrackingStatus Status,
        long? MasterProductId,
        string WaitingKey,
        string SearchName,
        string UnitLabel,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    /// <summary>One listing's last accepted price and when it was observed.</summary>
    private sealed record AcceptedPrice(decimal PackPrice, DateTime ObservedAtUtc);

    /// <summary>
    /// What one match decision did, before the save that gives a created master product and a new
    /// suggestion row their keys. It holds the entities rather than the ids for the reason #941's
    /// decided proposal does: an answer reporting a zero would be a key nothing could be looked up by.
    /// </summary>
    private sealed record DecidedMatch(
        MasterProduct? MasterProduct,
        bool MasterProductCreated,
        ProductMatchSuggestion? Suggestion,
        int ListingsMoved);

    /// <summary>
    /// The one waiting key an applied match reaches: its tentative suggestions and the mappings still
    /// awaiting it. The three travel together through every path that can apply a match, which is what
    /// <see cref="ApplyMatchAsync"/> groups them for.
    /// </summary>
    private sealed record MatchDecisionContext(
        string WaitingKey,
        List<ProductMatchSuggestion> Suggestions,
        IReadOnlyList<ProductPriceTracking> Awaiting);
}
