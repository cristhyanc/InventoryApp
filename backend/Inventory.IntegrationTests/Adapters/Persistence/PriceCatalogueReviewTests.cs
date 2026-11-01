using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The Super Admin's review of the shared price catalogue (issue #928, parent #817) over the real
/// adapter, the real migration history and <strong>two</strong> businesses, on the
/// <strong>denied</strong> business scope a platform-admin request carries.
///
/// <para>Two businesses throughout, as AGENTS.md § Tenant ownership requires of any change to
/// ownership or enforcement: a match is decided for a waiting <em>key</em>, so the property under
/// test is that one decision reaches both businesses' mappings and that nothing about either of them
/// leaves the adapter. Relational SQLite rather than InMemory, because what is under test is the
/// central query filters, the narrow write window, the check constraints and the unique indexes -
/// none of which an InMemory provider has.</para>
///
/// <para>The denied scope is the shape that matters: every endpoint on this surface is class 2
/// (<c>PlatformAdminPolicy</c> plus the platform marker), so the request resolves no business at all
/// and the central filters match no mapping. That is exactly why the adapter makes this issue's
/// reviewed <c>IgnoreQueryFilters</c> read and takes the review write lease.</para>
/// </summary>
public class PriceCatalogueReviewTests
{
    /// <summary>
    /// "Now" for every decision here: a day and an hour after the fixture's own instant, so a reading
    /// observed when the fixture was seeded is 25 hours old - inside the 30-hour daily limit - and a
    /// reading dated two days earlier is outside it.
    /// </summary>
    private static readonly DateTime Now = new(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>A reading old enough for the supplier's own freshness limit to refuse it.</summary>
    private static readonly DateTime LongAgo = new(2026, 10, 25, 5, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The collector run after a review, for the one test that states "priced on the next run" over
    /// the real results endpoint: started after the decision and still open when it submits.
    /// </summary>
    private static readonly DateTime NextRunStarted = new(2026, 10, 28, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextRunExpires = new(2026, 10, 28, 13, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextRunObserved = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextRunNow = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    private static string ColaKey => PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL");

    #region The queue

    /// <summary>
    /// The first criterion: a waiting product is listed by its search name and unit label, with the
    /// <em>count</em> of businesses waiting and never which business asked - and two businesses'
    /// different spellings of one product are one entry, because the waiting key is the fold of both
    /// values.
    /// </summary>
    [Fact]
    public async Task A_waiting_product_is_one_entry_with_a_count_of_the_businesses_waiting()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessPepsiId);

        var queue = await fixture.DeniedReviewStore().ReadQueueAsync(Now, CancellationToken.None);

        var cola = Assert.Single(
            queue.WaitingProducts,
            product => product.WaitingKey == ColaKey);

        Assert.Equal(2, cola.BusinessesWaitingCount);
        Assert.Equal("Coca-Cola Classic", cola.SearchName);
        Assert.Equal("375mL", cola.UnitLabel);

        var pepsi = Assert.Single(
            queue.WaitingProducts,
            product => product.SearchName == "Pepsi Max");
        Assert.Equal(1, pepsi.BusinessesWaitingCount);
    }

    /// <summary>
    /// A waiting product carries the evidence the reviewer decides on: the matcher's tentative
    /// suggestion with its confidence, reason and run, and the listings a search found under the key.
    /// </summary>
    [Fact]
    public async Task A_waiting_product_carries_its_suggestions_and_its_proposed_listings()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var suggestionId = await fixture.SeedSuggestionAsync(
            ColaKey,
            master,
            ProductMatchSuggestionStatus.Tentative,
            confidence: 0.6m,
            collectorRun: "price-collector-run-7");
        await fixture.SeedWaitingListingAsync(ColaKey, "SKU-COLA-30");

        var queue = await fixture.DeniedReviewStore().ReadQueueAsync(Now, CancellationToken.None);
        var cola = Assert.Single(queue.WaitingProducts, product => product.WaitingKey == ColaKey);

        var suggestion = Assert.Single(cola.Suggestions);
        Assert.Equal(suggestionId, suggestion.Id);
        Assert.Equal(0.6m, suggestion.Confidence);
        Assert.Equal("price-collector-run-7", suggestion.CollectorRun);
        Assert.Equal("Coca-Cola", suggestion.MasterProduct!.Brand);

        var listing = Assert.Single(cola.ProposedListings);
        Assert.Equal("SKU-COLA-30", listing.SupplierProductId);
        Assert.Equal("Alpha Grocer", listing.SupplierName);
    }

    /// <summary>
    /// The third criterion's first half: an automatic match is listed <em>separately</em>, with its
    /// run, confidence and reason, and with the search text of the mappings it applied to.
    /// </summary>
    [Fact]
    public async Task An_automatic_match_is_listed_separately_with_its_run_confidence_and_reason()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var tracking = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.MatchAsync(
            tracking.Id,
            master,
            ProductPriceTrackingStatus.AutoMatched,
            ProductPriceTrackingMatchSource.CollectorAutomaticMatch);
        var suggestionId = await fixture.SeedSuggestionAsync(
            ColaKey,
            master,
            ProductMatchSuggestionStatus.AutoApplied,
            confidence: 1m,
            collectorRun: "price-collector-run-9");

        var queue = await fixture.DeniedReviewStore().ReadQueueAsync(Now, CancellationToken.None);

        Assert.Empty(queue.WaitingProducts);
        var automatic = Assert.Single(queue.AutomaticMatches);
        Assert.Equal(suggestionId, automatic.SuggestionId);
        Assert.Equal(master, automatic.MasterProduct.Id);
        Assert.Equal(1m, automatic.Confidence);
        Assert.Equal("price-collector-run-9", automatic.CollectorRun);
        Assert.Equal("Coca-Cola Classic", automatic.SearchName);
    }

    /// <summary>
    /// A listing waiting for a decision carries everything that was submitted about it, including its
    /// latest pending reading and the server's own answers about whether that reading could become a
    /// price today.
    /// </summary>
    [Fact]
    public async Task A_listing_awaiting_a_decision_carries_its_evidence_and_its_pending_reading()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var listingId = await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "SKU-COLA-30",
            SupplierListingMatchStatus.Proposed);
        var runId = await fixture.SeedRunAsync(PriceCatalogueFixture.CreatedAt, null);
        var workItemId = await fixture.SeedWorkItemAsync(runId, master, null);
        await fixture.SeedPendingObservationAsync(
            listingId,
            runId,
            workItemId,
            packPrice: 42.50m,
            observedAtUtc: PriceCatalogueFixture.CreatedAt);

        var queue = await fixture.DeniedReviewStore().ReadQueueAsync(Now, CancellationToken.None);

        var listing = Assert.Single(queue.Listings);
        Assert.Equal(listingId, listing.Id);
        Assert.Equal("Classic", listing.Variant);
        Assert.Equal(375m, listing.SizeValue);
        Assert.Equal(30, listing.UnitsPerPack);
        Assert.Equal("0012345678905", listing.Barcode);
        Assert.Equal(BarcodeScope.Unit, listing.BarcodeScope);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, listing.FirstSeenAtUtc);

        var pending = listing.PendingObservation!;
        Assert.Equal(42.50m, pending.PackPrice);
        Assert.Equal(workItemId, pending.PriceCollectorWorkItemId);
        Assert.Equal(runId, pending.PriceCollectorRunId);
        Assert.True(pending.IsFresh);
        Assert.False(pending.NeedsPriceException);
    }

    /// <summary>
    /// The eighth criterion: a listing whose barcode is a confirmed listing's barcode in the same
    /// scope is marked as a strong match and shown <strong>first</strong> - and it still needs
    /// confirming.
    /// </summary>
    [Fact]
    public async Task A_barcode_that_a_confirmed_listing_carries_is_a_strong_match_and_is_shown_first()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Kinder", "Bueno");
        var otherSupplier = await fixture.SeedSupplierPresetAsync("Beta Grocer");

        var confirmedId = await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "WW-43G",
            SupplierListingMatchStatus.Confirmed,
            barcode: "80052760",
            barcodeScope: BarcodeScope.Unit);

        var plainId = await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "JB-OTHER",
            SupplierListingMatchStatus.Proposed,
            barcode: null,
            barcodeScope: null,
            supplierPresetId: otherSupplier);

        var strongId = await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "JB-KB",
            SupplierListingMatchStatus.Proposed,
            barcode: "80052760",
            barcodeScope: BarcodeScope.Unit,
            supplierPresetId: otherSupplier);

        var queue = await fixture.DeniedReviewStore().ReadQueueAsync(Now, CancellationToken.None);

        // The confirmed listing is not in the queue at all, and the strong match leads it.
        Assert.DoesNotContain(confirmedId, queue.Listings.Select(listing => listing.Id));
        Assert.Equal([strongId, plainId], queue.Listings.Select(listing => listing.Id).ToArray());

        var strong = queue.Listings[0];
        Assert.Equal(confirmedId, strong.StrongBarcodeMatch!.SupplierListingId);
        Assert.Equal("Kinder", strong.StrongBarcodeMatch.MasterProduct!.Brand);
        Assert.Equal(SupplierListingMatchStatus.Proposed, strong.MatchStatus);
        Assert.Null(queue.Listings[1].StrongBarcodeMatch);
    }

    /// <summary>
    /// A carton code equal to another listing's unit code is <strong>not</strong> a strong match: the
    /// two describe packs of different sizes, which is the mistake the barcode scope exists for.
    /// </summary>
    [Fact]
    public async Task A_barcode_in_another_scope_is_not_a_strong_match()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var otherSupplier = await fixture.SeedSupplierPresetAsync("Beta Grocer");

        await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "WW-CAN",
            SupplierListingMatchStatus.Confirmed,
            barcode: "9300675000628",
            barcodeScope: BarcodeScope.Unit);

        await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "JB-CARTON",
            SupplierListingMatchStatus.Proposed,
            barcode: "9300675000628",
            barcodeScope: BarcodeScope.Carton,
            supplierPresetId: otherSupplier);

        var queue = await fixture.DeniedReviewStore().ReadQueueAsync(Now, CancellationToken.None);

        Assert.Null(Assert.Single(queue.Listings).StrongBarcodeMatch);
    }

    /// <summary>
    /// The seventh criterion's first half: a refused listing is not shown again. It is kept in the
    /// catalogue - a refusal has to outlive the row it refused - and it is simply not part of what
    /// anybody is asked to decide.
    /// </summary>
    [Fact]
    public async Task A_rejected_listing_is_not_in_the_queue()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var rejectedId = await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "SKU-REFUSED",
            SupplierListingMatchStatus.Rejected);

        var queue = await fixture.DeniedReviewStore().ReadQueueAsync(Now, CancellationToken.None);

        Assert.Empty(queue.Listings);
        Assert.True(await fixture.ListingExistsAsync(rejectedId));
    }

    /// <summary>
    /// The queue publishes the whole master catalogue, ordered as a person reads it, because one of
    /// the criteria is that a reviewer may pick <em>another</em> existing master product - and a
    /// picker that offered only the products a suggestion happened to name would make that reachable
    /// for some keys and not others.
    /// </summary>
    [Fact]
    public async Task The_queue_publishes_the_master_catalogue_for_the_picker()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Pepsi", "Max");
        await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar");
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var queue = await fixture.DeniedReviewStore().ReadQueueAsync(Now, CancellationToken.None);

        Assert.Equal(
            ["Coca-Cola Classic", "Coca-Cola Zero Sugar", "Pepsi Max"],
            queue.MasterProducts
                .Select(master => $"{master.Brand} {master.Name}")
                .ToArray());
    }

    #endregion

    #region Confirming a listing and its pending price

    /// <summary>
    /// The fourth and fifth criteria together: confirming a listing confirms it, and accepts the
    /// reading it was holding as the listing's first accepted price, because that reading is inside
    /// the supplier's freshness limit and describes the pack that was confirmed.
    /// </summary>
    [Fact]
    public async Task Confirming_a_listing_accepts_a_fresh_pending_reading_as_its_first_price()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(fixture, packPrice: 42.50m);

        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(arranged.ListingId),
            Now,
            CancellationToken.None);

        Assert.True(result!.PriceAccepted);
        Assert.False(result.PriceExceptionUsed);
        Assert.Null(result.PriceReason);
        Assert.Equal(42.50m, result.AcceptedPackPrice);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, result.MatchStatus);

        var listing = await fixture.ListingAsync(arranged.ListingId);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);

        var observation = Assert.Single(await fixture.AllObservationsAsync());
        Assert.Equal(42.50m, observation.PackPrice);
        Assert.Equal(arranged.RunId, observation.PriceCollectorRunId);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, observation.ObservedAtUtc);
        Assert.Equal(observation.Id, listing.LatestObservationId);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, listing.LastCheckedAtUtc);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, listing.LastPriceChangeAtUtc);

        // Accepting it resolves the disagreement, so nothing is left pending.
        Assert.Empty(await fixture.AllPendingObservationsAsync());
    }

    /// <summary>
    /// The fifth criterion's second half: a reading older than the supplier's own freshness limit is
    /// <strong>not</strong> accepted, and the listing is confirmed with no current price - priced on
    /// the next run.
    /// </summary>
    [Fact]
    public async Task Confirming_a_listing_does_not_accept_a_stale_pending_reading()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(
            fixture,
            packPrice: 42.50m,
            observedAtUtc: LongAgo);

        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(arranged.ListingId),
            Now,
            CancellationToken.None);

        Assert.False(result!.PriceAccepted);
        Assert.Equal(SupplierListingReviewPolicy.StaleReadingReason, result.PriceReason);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, result.MatchStatus);

        var listing = await fixture.ListingAsync(arranged.ListingId);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
        Assert.Null(listing.LatestObservationId);
        Assert.Null(listing.LastCheckedAtUtc);
        Assert.Empty(await fixture.AllObservationsAsync());

        // The reading is kept: it is still the evidence of what was read, and the next run replaces it.
        Assert.Single(await fixture.AllPendingObservationsAsync());
    }

    /// <summary>
    /// The fifth criterion's last sentence: a corrected pack quantity is the one stored, and the
    /// price is accepted - so the unit price a comparison derives is the reviewer's quantity rather
    /// than the one the collector read off a name.
    /// </summary>
    [Fact]
    public async Task A_corrected_pack_quantity_is_stored_and_the_price_is_still_accepted()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(fixture, packPrice: 42.50m);

        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(arranged.ListingId) with { UnitsPerPack = 36 },
            Now,
            CancellationToken.None);

        Assert.True(result!.PriceAccepted);
        Assert.Equal(36, result.ConfirmedUnitsPerPack);

        var listing = await fixture.ListingAsync(arranged.ListingId);
        Assert.Equal(36, listing.UnitsPerPack);
        Assert.Equal(42.50m, Assert.Single(await fixture.AllObservationsAsync()).PackPrice);
    }

    /// <summary>
    /// <strong>The regression of the review finding of 30 October 2026.</strong> A confirmation that
    /// changes the pack must never leave the price the listing was already publishing in place: the
    /// comparison reads the listing's own pack quantity beside the observation the listing points at,
    /// so a $40 30-pack confirmed as a 36-pack was being presented as $40 for 36 - a per-unit price no
    /// supplier ever published.
    ///
    /// <para>Stated end to end over the <strong>real</strong> cheapest-supplier summary (#930) rather
    /// than over the listing's columns alone, because what was wrong was what a business saw. While
    /// the listing is flagged its price is recorded but compared with nothing; the confirmation is
    /// what makes it eligible again, and after a confirmation that moved the pack the summary reports
    /// "not checked yet" rather than a wrong number. The accepted observation is still in the
    /// append-only history, and the next collector run's reading of the pack that was confirmed
    /// restores a correct comparison.</para>
    /// </summary>
    [Fact]
    public async Task An_old_packs_price_is_not_published_against_a_newly_confirmed_pack()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangeComparedListingAsync(
            fixture,
            acceptedPackPrice: 40.00m,
            pendingPackPrice: 42.50m,
            pendingObservedAtUtc: LongAgo);

        // What the business sees while the listing is flagged: the price the catalogue holds, per unit
        // against the 30-pack it was read for, and compared with nothing until somebody confirms the
        // listing. The confirmation is what makes it eligible again - which is exactly why a
        // confirmation that moves the pack is the moment a wrong number would reach a business.
        var before = await ComparedOfferAsync(fixture, Now);
        Assert.True(before.HasPrice);
        Assert.Equal(40.00m, before.PackPrice);
        Assert.Equal(30, before.PackQuantity);
        Assert.NotNull(before.UnitPrice);
        Assert.Equal(SupplierOfferIneligibility.ListingNotConfirmed, before.Ineligibility);
        Assert.False(before.IsCheapest);

        // The reviewer corrects the pack to 36 while the reading in front of them is too old to be
        // guidance, so no replacement price is accepted.
        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(arranged.ListingId) with { UnitsPerPack = 36 },
            Now,
            CancellationToken.None);

        Assert.False(result!.PriceAccepted);
        Assert.Equal(SupplierListingReviewPolicy.StaleReadingReason, result.PriceReason);
        Assert.Equal(36, result.ConfirmedUnitsPerPack);

        // The listing is confirmed at the reviewed pack and holds no current price at all - the one
        // coherent "never checked" state, which the database's own check constraint also allows.
        var listing = await fixture.ListingAsync(arranged.ListingId);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
        Assert.Equal(36, listing.UnitsPerPack);
        Assert.Null(listing.LatestObservationId);
        Assert.Null(listing.LastCheckedAtUtc);
        Assert.Null(listing.LastPriceChangeAtUtc);

        // History is preserved: what the supplier charged for the pack that was there stays recorded.
        var history = Assert.Single(await fixture.AllObservationsAsync());
        Assert.Equal(arranged.AcceptedObservationId, history.Id);
        Assert.Equal(40.00m, history.PackPrice);

        // And the business is told "not checked yet" rather than shown the old pack's price under the
        // new pack: no price, no unit price, and nothing to be cheapest.
        var after = await ComparedOfferAsync(fixture, Now);
        Assert.True(after.HasListing);
        Assert.False(after.HasPrice);
        Assert.Null(after.PackPrice);
        Assert.Null(after.UnitPrice);
        Assert.Equal(36, after.PackQuantity);
        Assert.Null(after.Ineligibility);

        var summaryAfter = await SummaryAsync(fixture, Now);
        Assert.Null(Assert.Single(summaryAfter.Products).CheapestUnitPrice);

        // Priced on the next run, which is the issue's own answer: the real collector results
        // endpoint reads the pack that was confirmed and that reading becomes the price.
        var nextRunId = await fixture.SeedRunAsync(NextRunStarted, NextRunExpires);
        var nextWorkItemId = await fixture.SeedWorkItemAsync(nextRunId, arranged.MasterProductId, null);

        var receipt = await new RecordPriceCollectorResults(
                fixture.DeniedResultStore(),
                new InventoryApi.Tests.Application.Time.FakeClock(NextRunNow))
            .Handle(
                new PriceCollectorResultBatch(
                    nextRunId,
                    [ConfirmedPackRow(nextWorkItemId, fixture.SupplierPresetId, 55.00m)]),
                CancellationToken.None);

        Assert.Single(receipt!.Accepted);

        var restored = await ComparedOfferAsync(fixture, NextRunNow);
        Assert.True(restored.HasPrice);
        Assert.Equal(55.00m, restored.PackPrice);
        Assert.Equal(36, restored.PackQuantity);
        Assert.NotNull(restored.UnitPrice);

        var summaryRestored = await SummaryAsync(fixture, NextRunNow);
        Assert.Equal(
            restored.UnitPrice,
            Assert.Single(summaryRestored.Products).CheapestUnitPrice);

        // Still append-only: the withdrawn price and the new one are both there.
        Assert.Equal(
            [40.00m, 55.00m],
            (await fixture.AllObservationsAsync()).Select(row => row.PackPrice).Order().ToArray());
    }

    /// <summary>
    /// The same withdrawal on every path that refuses the held reading, because none of them is a
    /// reason to keep publishing the old pack's price: a reading too old to be guidance, a reading of
    /// another pack, and a reading outside the band with no exception approved.
    ///
    /// <para>The listing ends confirmed at the reviewed pack with no current price in all three, and
    /// the accepted observation stays in the append-only history in all three. A refused reading is
    /// never accepted just to fill the gap that leaves - which is what each case's own reason says.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("stale")]
    [InlineData("another pack")]
    [InlineData("out of band")]
    public async Task A_refused_reading_leaves_a_corrected_pack_with_no_current_price(string refusal)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(
            fixture,
            packPrice: refusal == "out of band" ? 9.00m : 42.50m,
            observedAtUtc: refusal == "stale" ? LongAgo : PriceCatalogueFixture.CreatedAt,
            lastAcceptedPackPrice: 40.00m,
            status: SupplierListingMatchStatus.NeedsReview);

        var submission = Confirm(arranged.ListingId) with { UnitsPerPack = 36 };

        if (refusal == "another pack")
        {
            submission = submission with { Variant = "Zero Sugar" };
        }

        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            submission,
            Now,
            CancellationToken.None);

        Assert.False(result!.PriceAccepted);
        Assert.False(result.PriceExceptionUsed);
        Assert.Equal(
            refusal switch
            {
                "stale" => SupplierListingReviewPolicy.StaleReadingReason,
                "another pack" => SupplierListingReviewPolicy.ReadingDescribesAnotherPackReason,
                _ => SupplierListingReviewPolicy.PriceExceptionNotApprovedReason,
            },
            result.PriceReason);

        var listing = await fixture.ListingAsync(arranged.ListingId);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
        Assert.Equal(36, listing.UnitsPerPack);
        Assert.Null(listing.LatestObservationId);
        Assert.Null(listing.LastCheckedAtUtc);
        Assert.Null(listing.LastPriceChangeAtUtc);

        Assert.Equal(40.00m, Assert.Single(await fixture.AllObservationsAsync()).PackPrice);
    }

    /// <summary>
    /// The control case, and the documented handling of a <em>compatible</em> historical price: a
    /// confirmation that corrects nothing leaves the price the listing was publishing exactly where it
    /// was, because that accepted reading is still a reading of this pack. It is not re-accepted and
    /// the comparison is unchanged; its own freshness limit is what eventually stops it being
    /// compared (#925).
    /// </summary>
    [Fact]
    public async Task A_confirmation_that_corrects_nothing_keeps_the_price_the_listing_was_publishing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangeComparedListingAsync(
            fixture,
            acceptedPackPrice: 40.00m,
            pendingPackPrice: 42.50m,
            pendingObservedAtUtc: LongAgo);

        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(arranged.ListingId),
            Now,
            CancellationToken.None);

        Assert.False(result!.PriceAccepted);
        Assert.Equal(SupplierListingReviewPolicy.StaleReadingReason, result.PriceReason);

        var listing = await fixture.ListingAsync(arranged.ListingId);
        Assert.Equal(30, listing.UnitsPerPack);
        Assert.Equal(arranged.AcceptedObservationId, listing.LatestObservationId);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, listing.LastCheckedAtUtc);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, listing.LastPriceChangeAtUtc);

        var offer = await ComparedOfferAsync(fixture, Now);
        Assert.True(offer.HasPrice);
        Assert.Equal(40.00m, offer.PackPrice);
        Assert.Equal(30, offer.PackQuantity);
    }

    /// <summary>
    /// A reading that describes a different <em>product</em> from the one confirmed is not accepted,
    /// whatever its price says: the variant is part of what a per-unit price means.
    /// </summary>
    [Fact]
    public async Task A_pending_reading_of_another_variant_is_not_accepted()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(fixture, packPrice: 42.50m);

        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(arranged.ListingId) with { Variant = "Zero Sugar" },
            Now,
            CancellationToken.None);

        Assert.False(result!.PriceAccepted);
        Assert.Equal(
            SupplierListingReviewPolicy.ReadingDescribesAnotherPackReason,
            result.PriceReason);
        Assert.Empty(await fixture.AllObservationsAsync());

        var listing = await fixture.ListingAsync(arranged.ListingId);
        Assert.Equal("Zero Sugar", listing.Variant);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
    }

    /// <summary>
    /// The sixth criterion's last sentence: without the explicit price exception the band still
    /// applies, so a price past its edge is not accepted and the listing keeps the price it had.
    /// </summary>
    [Fact]
    public async Task An_out_of_band_pending_price_is_not_accepted_without_an_exception()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(
            fixture,
            packPrice: 9.00m,
            lastAcceptedPackPrice: 40.00m,
            status: SupplierListingMatchStatus.NeedsReview);

        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(arranged.ListingId),
            Now,
            CancellationToken.None);

        Assert.False(result!.PriceAccepted);
        Assert.Equal(
            SupplierListingReviewPolicy.PriceExceptionNotApprovedReason,
            result.PriceReason);

        var listing = await fixture.ListingAsync(arranged.ListingId);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
        Assert.Equal(40.00m, Assert.Single(await fixture.AllObservationsAsync()).PackPrice);
        Assert.Single(await fixture.AllPendingObservationsAsync());
    }

    /// <summary>
    /// The sixth criterion: with the exception explicitly approved the price is accepted despite the
    /// band, and it <strong>becomes the new reference price</strong> - which the test states by
    /// reading the listing's own latest observation, because that is what the next run's band is
    /// measured against (#937).
    /// </summary>
    [Fact]
    public async Task An_approved_price_exception_accepts_the_price_and_becomes_the_new_reference()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(
            fixture,
            packPrice: 9.00m,
            lastAcceptedPackPrice: 40.00m,
            status: SupplierListingMatchStatus.NeedsReview);

        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(arranged.ListingId) with { ApprovePriceException = true },
            Now,
            CancellationToken.None);

        Assert.True(result!.PriceAccepted);
        Assert.True(result.PriceExceptionUsed);
        Assert.Equal(9.00m, result.AcceptedPackPrice);

        var listing = await fixture.ListingAsync(arranged.ListingId);
        var accepted = (await fixture.AllObservationsAsync())
            .Single(observation => observation.Id == listing.LatestObservationId);
        Assert.Equal(9.00m, accepted.PackPrice);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, listing.LastPriceChangeAtUtc);
        Assert.Empty(await fixture.AllPendingObservationsAsync());
    }

    /// <summary>
    /// An approved exception a stale reading does not get to use: an approval overrides the band and
    /// nothing else. It is the one check a reviewer may deliberately waive, and freshness is not it.
    /// </summary>
    [Fact]
    public async Task An_approved_price_exception_does_not_accept_a_stale_reading()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(
            fixture,
            packPrice: 9.00m,
            observedAtUtc: LongAgo,
            lastAcceptedPackPrice: 40.00m,
            status: SupplierListingMatchStatus.NeedsReview);

        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(arranged.ListingId) with { ApprovePriceException = true },
            Now,
            CancellationToken.None);

        Assert.False(result!.PriceAccepted);
        Assert.False(result.PriceExceptionUsed);
        Assert.Equal(SupplierListingReviewPolicy.StaleReadingReason, result.PriceReason);
    }

    /// <summary>
    /// Refusing a listing records the refusal and accepts no price at all, and the held reading goes
    /// with it: a reading nobody will ever price is not evidence of a disagreement any more.
    /// </summary>
    [Fact]
    public async Task Refusing_a_listing_records_the_refusal_and_accepts_no_price()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(fixture, packPrice: 42.50m);

        var result = await fixture.DeniedReviewStore().DecideListingAsync(
            new SupplierListingReviewSubmission(
                arranged.ListingId,
                SupplierListingReviewOutcome.Rejected,
                UnitsPerPack: null,
                Barcode: null,
                BarcodeScope: null,
                Variant: null,
                SizeValue: null,
                SizeUnit: null,
                ApprovePriceException: false),
            Now,
            CancellationToken.None);

        Assert.Equal(SupplierListingMatchStatus.Rejected, result!.MatchStatus);
        Assert.False(result.PriceAccepted);

        var listing = await fixture.ListingAsync(arranged.ListingId);
        Assert.Equal(SupplierListingMatchStatus.Rejected, listing.MatchStatus);

        // The identity is left exactly as it was: a refusal is not an edit.
        Assert.Equal("Classic", listing.Variant);
        Assert.Equal(30, listing.UnitsPerPack);
        Assert.Empty(await fixture.AllObservationsAsync());
        Assert.Empty(await fixture.AllPendingObservationsAsync());
    }

    /// <summary>
    /// Deciding a listing that has already been decided is refused rather than applied: the row the
    /// reviewer was looking at has moved since, and re-confirming against the older state is how a
    /// confirmation silently re-confirms an identity nobody looked at.
    /// </summary>
    [Fact]
    public async Task Deciding_a_listing_that_is_no_longer_awaiting_a_decision_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(fixture, packPrice: 42.50m);

        await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(arranged.ListingId),
            Now,
            CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.DeniedReviewStore().DecideListingAsync(
                Confirm(arranged.ListingId),
                Now,
                CancellationToken.None));

        Assert.Equal(SupplierListingReviewPolicy.NotAwaitingReviewMessage, refusal.Message);
        Assert.Single(await fixture.AllObservationsAsync());
    }

    /// <summary>A listing nothing holds is no decision: the adapter answers nothing and writes nothing.</summary>
    [Fact]
    public async Task Deciding_a_listing_that_does_not_exist_answers_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        Assert.Null(await fixture.DeniedReviewStore().DecideListingAsync(
            Confirm(4242),
            Now,
            CancellationToken.None));
    }

    /// <summary>
    /// A submitted identity is held to the catalogue's own rules: a barcode with no scope is refused
    /// and the listing is left exactly as it was, decision included.
    /// </summary>
    [Fact]
    public async Task A_submitted_identity_the_catalogue_would_not_store_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangePendingListingAsync(fixture, packPrice: 42.50m);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.DeniedReviewStore().DecideListingAsync(
                Confirm(arranged.ListingId) with { BarcodeScope = null },
                Now,
                CancellationToken.None));

        var listing = await fixture.ListingAsync(arranged.ListingId);
        Assert.Equal(SupplierListingMatchStatus.Proposed, listing.MatchStatus);
        Assert.Equal(BarcodeScope.Unit, listing.BarcodeScope);
        Assert.Empty(await fixture.AllObservationsAsync());
    }

    #endregion

    #region Matching a waiting product

    /// <summary>
    /// The second criterion: accepting the matcher's suggestion matches <strong>every</strong>
    /// mapping with that waiting key - in both businesses - moves the key's listings to the master
    /// product as proposals, and records the acceptance on the suggestion row.
    /// </summary>
    [Fact]
    public async Task Accepting_a_suggestion_matches_every_waiting_mapping_and_moves_the_listings()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var suggestionId = await fixture.SeedSuggestionAsync(
            ColaKey,
            master,
            ProductMatchSuggestionStatus.Tentative);
        await fixture.SeedWaitingListingAsync(ColaKey, "SKU-COLA-30");

        var result = await fixture.DeniedReviewStore().DecideMatchAsync(
            new ProductMatchReviewSubmission(
                ColaKey,
                ProductMatchReviewDecision.AcceptSuggestion,
                suggestionId,
                MasterProductId: null,
                NewBrand: null,
                NewName: null,
                NewSizeValue: null,
                NewSizeUnit: null),
            Now,
            CancellationToken.None);

        Assert.Equal(master, result!.MasterProduct!.Id);
        Assert.False(result.MasterProductCreated);
        Assert.Equal(1, result.ListingsMovedCount);

        var mappings = await fixture.AllMappingsAsync();
        Assert.All(
            mappings.Where(mapping => mapping.WaitingKey == ColaKey),
            mapping =>
            {
                Assert.Equal(ProductPriceTrackingStatus.Matched, mapping.Status);
                Assert.Equal(master, mapping.MasterProductId);
                Assert.Equal(ProductPriceTrackingMatchSource.SuperAdmin, mapping.MatchSource);
                Assert.Equal(Now, mapping.UpdatedAtUtc);
            });
        Assert.Equal(2, mappings.Count(mapping => mapping.MasterProductId == master));

        var listing = Assert.Single(await fixture.AllListingsAsync());
        Assert.Equal(master, listing.MasterProductId);
        Assert.Null(listing.WaitingKey);
        Assert.Equal(ColaKey, listing.OriginWaitingKey);
        Assert.Equal(SupplierListingMatchStatus.Proposed, listing.MatchStatus);

        Assert.Equal(
            ProductMatchSuggestionStatus.Accepted,
            (await fixture.SuggestionAsync(suggestionId)).Status);
    }

    /// <summary>
    /// Picking another existing master product matches the key to it and <strong>refuses</strong> the
    /// suggestion that proposed something else - choosing one product is saying the other was wrong,
    /// and a refused pair is never applied automatically again.
    /// </summary>
    [Fact]
    public async Task Picking_another_master_product_matches_it_and_refuses_the_other_suggestion()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var proposed = await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar");
        var chosen = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var suggestionId = await fixture.SeedSuggestionAsync(
            ColaKey,
            proposed,
            ProductMatchSuggestionStatus.Tentative);

        var result = await fixture.DeniedReviewStore().DecideMatchAsync(
            new ProductMatchReviewSubmission(
                ColaKey,
                ProductMatchReviewDecision.ChooseMasterProduct,
                SuggestionId: null,
                chosen,
                NewBrand: null,
                NewName: null,
                NewSizeValue: null,
                NewSizeUnit: null),
            Now,
            CancellationToken.None);

        Assert.Equal(chosen, result!.MasterProduct!.Id);

        var mapping = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(chosen, mapping.MasterProductId);
        Assert.Equal(ProductPriceTrackingStatus.Matched, mapping.Status);

        Assert.Equal(
            ProductMatchSuggestionStatus.Rejected,
            (await fixture.SuggestionAsync(suggestionId)).Status);

        // And the reviewer's own decision is recorded as the key's accepted match, which is what the
        // results endpoint resolves a waiting key through (#937).
        var accepted = Assert.Single(
            await fixture.AllSuggestionsAsync(),
            suggestion => suggestion.Status == ProductMatchSuggestionStatus.Accepted);
        Assert.Equal(chosen, accepted.MasterProductId);
        Assert.Equal(ProductMatchReviewPolicy.ReviewCollectorRun, accepted.CollectorRun);
    }

    /// <summary>
    /// Creating a master product from a suggestion: the row is created with the description the
    /// reviewer confirmed, the key is matched to it, and the suggestion that described it is accepted.
    /// This is one of the two writers of <c>MasterProducts</c> in the application, and the only one
    /// that matches a key to the product it creates (issue #1024's create does not).
    /// </summary>
    [Fact]
    public async Task Accepting_a_suggested_new_master_product_creates_it_and_matches_the_key()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var suggestionId = await fixture.SeedSuggestionAsync(
            ColaKey,
            masterProductId: null,
            ProductMatchSuggestionStatus.Tentative,
            suggestedBrand: "Coca-Cola",
            suggestedName: "Classic",
            suggestedSizeValue: 375m,
            suggestedSizeUnit: MasterProductSizeUnit.Millilitre);

        var result = await fixture.DeniedReviewStore().DecideMatchAsync(
            new ProductMatchReviewSubmission(
                ColaKey,
                ProductMatchReviewDecision.AcceptSuggestion,
                suggestionId,
                MasterProductId: null,
                NewBrand: null,
                NewName: null,
                NewSizeValue: null,
                NewSizeUnit: null),
            Now,
            CancellationToken.None);

        Assert.True(result!.MasterProductCreated);

        var master = Assert.Single(await fixture.AllMasterProductsAsync());
        Assert.Equal("Coca-Cola", master.Brand);
        Assert.Equal("Classic", master.Name);
        Assert.Equal(375m, master.SizeValue);
        Assert.Equal(
            MasterProductPolicy.NormalisedKey("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre),
            master.NormalisedKey);
        Assert.Equal(master.Id, result.MasterProduct!.Id);

        var mapping = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(master.Id, mapping.MasterProductId);

        Assert.Equal(
            ProductMatchSuggestionStatus.Accepted,
            (await fixture.SuggestionAsync(suggestionId)).Status);

        // The accepted record that names the created master product, which the describing row cannot
        // carry: one proposal per row is the table's own rule.
        Assert.Single(
            await fixture.AllSuggestionsAsync(),
            suggestion => suggestion.MasterProductId == master.Id
                && suggestion.Status == ProductMatchSuggestionStatus.Accepted);
    }

    /// <summary>
    /// A created master product is held to the catalogue's own uniqueness rule: one description is
    /// one master product, so a description another row already holds is refused and nothing is
    /// written.
    /// </summary>
    [Fact]
    public async Task Creating_a_master_product_that_already_exists_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.DeniedReviewStore().DecideMatchAsync(
                new ProductMatchReviewSubmission(
                    ColaKey,
                    ProductMatchReviewDecision.CreateMasterProduct,
                    SuggestionId: null,
                    MasterProductId: null,
                    "Coca-Cola",
                    "Classic",
                    375m,
                    MasterProductSizeUnit.Millilitre),
                Now,
                CancellationToken.None));

        Assert.Single(await fixture.AllMasterProductsAsync());
        Assert.Equal(
            ProductPriceTrackingStatus.Requested,
            Assert.Single(await fixture.AllMappingsAsync()).Status);
    }

    /// <summary>
    /// Refusing a suggestion keeps the key waiting and keeps the refusal for ever, which is what stops
    /// the matcher applying that pair again.
    /// </summary>
    [Fact]
    public async Task Refusing_a_suggestion_keeps_the_key_waiting()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar");
        var suggestionId = await fixture.SeedSuggestionAsync(
            ColaKey,
            master,
            ProductMatchSuggestionStatus.Tentative);

        var result = await fixture.DeniedReviewStore().DecideMatchAsync(
            new ProductMatchReviewSubmission(
                ColaKey,
                ProductMatchReviewDecision.RejectSuggestion,
                suggestionId,
                MasterProductId: null,
                NewBrand: null,
                NewName: null,
                NewSizeValue: null,
                NewSizeUnit: null),
            Now,
            CancellationToken.None);

        Assert.Null(result!.MasterProduct);

        var stored = await fixture.SuggestionAsync(suggestionId);
        Assert.Equal(ProductMatchSuggestionStatus.Rejected, stored.Status);
        Assert.True(ProductMatchSuggestionPolicy.BlocksAutomaticMatching(stored.Status));

        var mapping = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(ProductPriceTrackingStatus.Requested, mapping.Status);
        Assert.Null(mapping.MasterProductId);
    }

    /// <summary>
    /// Marking a waiting product "not found" records an answer rather than leaving a gap: the
    /// mappings stop being searched for and name no master product.
    /// </summary>
    [Fact]
    public async Task Marking_a_waiting_product_not_found_records_that_answer()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var result = await fixture.DeniedReviewStore().DecideMatchAsync(
            new ProductMatchReviewSubmission(
                ColaKey,
                ProductMatchReviewDecision.MarkNotFound,
                SuggestionId: null,
                MasterProductId: null,
                NewBrand: null,
                NewName: null,
                NewSizeValue: null,
                NewSizeUnit: null),
            Now,
            CancellationToken.None);

        Assert.Null(result!.MasterProduct);
        Assert.All(await fixture.AllMappingsAsync(), mapping =>
        {
            Assert.Equal(ProductPriceTrackingStatus.NotFound, mapping.Status);
            Assert.Null(mapping.MasterProductId);
            Assert.Null(mapping.MatchSource);
        });
    }

    /// <summary>
    /// A mapping a person already settled, one a run auto-matched and one already recorded as not
    /// found are not changed by a decision about the key: only a mapping still awaiting a match is.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.Matched)]
    [InlineData(ProductPriceTrackingStatus.AutoMatched)]
    public async Task A_mapping_that_is_not_awaiting_a_match_is_left_alone(
        ProductPriceTrackingStatus status)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var settled = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var waiting = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        var other = await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar");
        var chosen = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.MatchAsync(
            settled.Id,
            other,
            status,
            status == ProductPriceTrackingStatus.AutoMatched
                ? ProductPriceTrackingMatchSource.CollectorAutomaticMatch
                : ProductPriceTrackingMatchSource.SuperAdmin);

        await fixture.DeniedReviewStore().DecideMatchAsync(
            new ProductMatchReviewSubmission(
                ColaKey,
                ProductMatchReviewDecision.ChooseMasterProduct,
                SuggestionId: null,
                chosen,
                NewBrand: null,
                NewName: null,
                NewSizeValue: null,
                NewSizeUnit: null),
            Now,
            CancellationToken.None);

        var mappings = await fixture.AllMappingsAsync();
        var untouched = mappings.Single(mapping => mapping.Id == settled.Id);
        Assert.Equal(status, untouched.Status);
        Assert.Equal(other, untouched.MasterProductId);

        var matched = mappings.Single(mapping => mapping.Id == waiting.Id);
        Assert.Equal(chosen, matched.MasterProductId);
    }

    /// <summary>A key nothing is waiting under is no decision: the adapter answers nothing.</summary>
    [Fact]
    public async Task Deciding_a_key_nothing_is_waiting_under_answers_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        Assert.Null(await fixture.DeniedReviewStore().DecideMatchAsync(
            new ProductMatchReviewSubmission(
                ColaKey,
                ProductMatchReviewDecision.ChooseMasterProduct,
                SuggestionId: null,
                master,
                NewBrand: null,
                NewName: null,
                NewSizeValue: null,
                NewSizeUnit: null),
            Now,
            CancellationToken.None));
    }

    /// <summary>A master product this catalogue does not hold is refused, and nothing is written.</summary>
    [Fact]
    public async Task Choosing_a_master_product_that_does_not_exist_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.DeniedReviewStore().DecideMatchAsync(
                new ProductMatchReviewSubmission(
                    ColaKey,
                    ProductMatchReviewDecision.ChooseMasterProduct,
                    SuggestionId: null,
                    4242,
                    NewBrand: null,
                    NewName: null,
                    NewSizeValue: null,
                    NewSizeUnit: null),
                Now,
                CancellationToken.None));

        Assert.Equal(ProductMatchReviewPolicy.UnknownMasterProductMessage, refusal.Message);
        Assert.Equal(
            ProductPriceTrackingStatus.Requested,
            Assert.Single(await fixture.AllMappingsAsync()).Status);
    }

    /// <summary>A suggestion that is not waiting for a decision is refused rather than decided twice.</summary>
    [Fact]
    public async Task Accepting_a_suggestion_that_was_already_decided_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var suggestionId = await fixture.SeedSuggestionAsync(
            ColaKey,
            master,
            ProductMatchSuggestionStatus.Rejected);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.DeniedReviewStore().DecideMatchAsync(
                new ProductMatchReviewSubmission(
                    ColaKey,
                    ProductMatchReviewDecision.AcceptSuggestion,
                    suggestionId,
                    MasterProductId: null,
                    NewBrand: null,
                    NewName: null,
                    NewSizeValue: null,
                    NewSizeUnit: null),
                Now,
                CancellationToken.None));

        Assert.Equal(ProductMatchReviewPolicy.SuggestionNotAwaitingReviewMessage, refusal.Message);
    }

    #endregion

    #region Automatic matches

    /// <summary>
    /// The third criterion's confirm half: confirming an automatic match settles the mappings it
    /// applied to and records the acceptance, leaving how the match was made exactly as it was.
    /// </summary>
    [Fact]
    public async Task Confirming_an_automatic_match_settles_the_mappings_it_applied_to()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangeAutomaticMatchAsync(fixture);

        var result = await fixture.DeniedReviewStore().DecideAutomaticMatchAsync(
            arranged.SuggestionId,
            AutomaticMatchReviewDecision.Confirm,
            Now,
            CancellationToken.None);

        Assert.Equal(arranged.MasterProductId, result!.MasterProduct.Id);

        var mapping = Assert.Single(
            await fixture.AllMappingsAsync(),
            candidate => candidate.Id == arranged.AutoMatchedMappingId);
        Assert.Equal(ProductPriceTrackingStatus.Matched, mapping.Status);
        Assert.Equal(arranged.MasterProductId, mapping.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.CollectorAutomaticMatch, mapping.MatchSource);

        Assert.Equal(
            ProductMatchSuggestionStatus.Accepted,
            (await fixture.SuggestionAsync(arranged.SuggestionId)).Status);
    }

    /// <summary>
    /// The third criterion's undo half, in full: the mappings return to awaiting a match, the proposed
    /// listings that came from the key move back to it, the confirmed listing is untouched, and the
    /// pair is recorded as undone - which is what stops it being applied automatically again.
    /// </summary>
    [Fact]
    public async Task Undoing_an_automatic_match_returns_the_mappings_and_the_proposed_listings()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangeAutomaticMatchAsync(fixture);

        var result = await fixture.DeniedReviewStore().DecideAutomaticMatchAsync(
            arranged.SuggestionId,
            AutomaticMatchReviewDecision.Undo,
            Now,
            CancellationToken.None);

        Assert.Equal(1, result!.ListingsMovedCount);

        var mapping = Assert.Single(
            await fixture.AllMappingsAsync(),
            candidate => candidate.Id == arranged.AutoMatchedMappingId);
        Assert.Equal(ProductPriceTrackingStatus.Requested, mapping.Status);
        Assert.Null(mapping.MasterProductId);
        Assert.Null(mapping.MatchSource);
        Assert.Equal(ColaKey, mapping.WaitingKey);

        var listings = await fixture.AllListingsAsync();
        var proposed = listings.Single(listing => listing.Id == arranged.ProposedListingId);
        Assert.Equal(ColaKey, proposed.WaitingKey);
        Assert.Null(proposed.MasterProductId);
        Assert.Equal(ColaKey, proposed.OriginWaitingKey);

        var confirmed = listings.Single(listing => listing.Id == arranged.ConfirmedListingId);
        Assert.Equal(arranged.MasterProductId, confirmed.MasterProductId);
        Assert.Null(confirmed.WaitingKey);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, confirmed.MatchStatus);

        var suggestion = await fixture.SuggestionAsync(arranged.SuggestionId);
        Assert.Equal(ProductMatchSuggestionStatus.Undone, suggestion.Status);
        Assert.True(ProductMatchSuggestionPolicy.BlocksAutomaticMatching(suggestion.Status));
    }

    /// <summary>
    /// The undo's last clause, proved through the <strong>real matcher</strong>: an undone pair is
    /// never auto-applied again, however obvious it looks on the next run.
    /// </summary>
    [Fact]
    public async Task An_undone_pair_is_never_applied_automatically_again()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var arranged = await ArrangeAutomaticMatchAsync(fixture);

        await fixture.DeniedReviewStore().DecideAutomaticMatchAsync(
            arranged.SuggestionId,
            AutomaticMatchReviewDecision.Undo,
            Now,
            CancellationToken.None);

        var runId = await fixture.SeedRunAsync(Now, Now.AddHours(6));
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, ColaKey);

        var receipt = await fixture.DeniedMatchStore().SubmitAsync(
            new ProductMatchBatch(
                runId,
                [
                    new ProductMatchProposal(
                        ColaKey,
                        arranged.MasterProductId,
                        SuggestedBrand: null,
                        SuggestedName: null,
                        SuggestedSizeValue: null,
                        SuggestedSizeUnit: null,
                        ProductMatchConfidence.High,
                        "The description matches exactly."),
                ]),
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.Empty(receipt!.AutoMatched);
        Assert.Equal(
            ProductPriceTrackingStatus.Requested,
            Assert.Single(
                await fixture.AllMappingsAsync(),
                mapping => mapping.Id == arranged.AutoMatchedMappingId).Status);
    }

    /// <summary>
    /// A match a person already decided is not an automatic match: confirming or undoing it is
    /// refused rather than applied a second time.
    /// </summary>
    [Fact]
    public async Task Deciding_something_that_is_not_an_automatic_match_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var suggestionId = await fixture.SeedSuggestionAsync(
            ColaKey,
            master,
            ProductMatchSuggestionStatus.Accepted);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.DeniedReviewStore().DecideAutomaticMatchAsync(
                suggestionId,
                AutomaticMatchReviewDecision.Undo,
                Now,
                CancellationToken.None));

        Assert.Equal(ProductMatchReviewPolicy.NotAnAutomaticMatchMessage, refusal.Message);
    }

    /// <summary>A suggestion nothing holds is no decision either.</summary>
    [Fact]
    public async Task Deciding_an_automatic_match_that_does_not_exist_answers_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        Assert.Null(await fixture.DeniedReviewStore().DecideAutomaticMatchAsync(
            4242,
            AutomaticMatchReviewDecision.Confirm,
            Now,
            CancellationToken.None));
    }

    #endregion

    #region The boundary

    /// <summary>
    /// The tenancy boundary, stated as a fingerprint: a listing decision, a match and an undo
    /// together leave both businesses' products, purchases, suppliers and stock exactly as they were,
    /// and the only thing that moves on a mapping is its own match columns.
    /// </summary>
    [Fact]
    public async Task A_review_writes_nothing_of_either_business_but_the_mappings_match_columns()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        var pepsi = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessPepsiId);
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var before = await fixture.AllMappingsAsync();

        await fixture.DeniedReviewStore().DecideMatchAsync(
            new ProductMatchReviewSubmission(
                ColaKey,
                ProductMatchReviewDecision.ChooseMasterProduct,
                SuggestionId: null,
                master,
                NewBrand: null,
                NewName: null,
                NewSizeValue: null,
                NewSizeUnit: null),
            Now,
            CancellationToken.None);

        var after = await fixture.AllMappingsAsync();

        foreach (var mapping in after)
        {
            var original = before.Single(row => row.Id == mapping.Id);

            Assert.Equal(original.BusinessId, mapping.BusinessId);
            Assert.Equal(original.ProductId, mapping.ProductId);
            Assert.Equal(original.WaitingKey, mapping.WaitingKey);
            Assert.Equal(original.SearchName, mapping.SearchName);
            Assert.Equal(original.UnitLabel, mapping.UnitLabel);
            Assert.Equal(original.CreatedAtUtc, mapping.CreatedAtUtc);
        }

        // The other product's mapping is not in the key at all, so nothing about it moved.
        var untouched = after.Single(mapping => mapping.Id == pepsi.Id);
        Assert.Equal(ProductPriceTrackingStatus.Requested, untouched.Status);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, untouched.UpdatedAtUtc);
    }

    /// <summary>
    /// The window is the platform administrator's, not a member's: a review store on a context that
    /// already names a business writes nothing at all, because the lease it needs may be taken only
    /// while the scope is denied.
    /// </summary>
    [Fact]
    public async Task A_review_from_a_request_that_already_names_a_business_writes_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.ReviewStoreFor(fixture.FirstBusinessId).DecideMatchAsync(
                new ProductMatchReviewSubmission(
                    ColaKey,
                    ProductMatchReviewDecision.ChooseMasterProduct,
                    SuggestionId: null,
                    master,
                    NewBrand: null,
                    NewName: null,
                    NewSizeValue: null,
                    NewSizeUnit: null),
                Now,
                CancellationToken.None));

        Assert.Equal(
            ProductPriceTrackingStatus.Requested,
            Assert.Single(await fixture.AllMappingsAsync()).Status);
    }

    #endregion

    private static SupplierListingReviewSubmission Confirm(long listingId) =>
        new(
            listingId,
            SupplierListingReviewOutcome.Confirmed,
            UnitsPerPack: 30,
            Barcode: "0012345678905",
            BarcodeScope.Unit,
            Variant: "Classic",
            SizeValue: 375m,
            MasterProductSizeUnit.Millilitre,
            ApprovePriceException: false);

    private sealed record ArrangedListing(long ListingId, long RunId, long WorkItemId);

    /// <summary>
    /// One listing of one master product, awaiting a decision, holding one reading - and optionally
    /// with an accepted price already behind it, so the band has something to be a band around.
    /// </summary>
    private static async Task<ArrangedListing> ArrangePendingListingAsync(
        PriceCatalogueFixture fixture,
        decimal packPrice,
        DateTime? observedAtUtc = null,
        decimal? lastAcceptedPackPrice = null,
        SupplierListingMatchStatus status = SupplierListingMatchStatus.Proposed)
    {
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var listingId = await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "SKU-COLA-30",
            status);
        var runId = await fixture.SeedRunAsync(PriceCatalogueFixture.CreatedAt, null);
        var workItemId = await fixture.SeedWorkItemAsync(runId, master, null);

        if (lastAcceptedPackPrice is { } last)
        {
            await fixture.SeedAcceptedPriceAsync(
                listingId,
                runId,
                last,
                PriceCatalogueFixture.CreatedAt.AddDays(-1));
        }

        await fixture.SeedPendingObservationAsync(
            listingId,
            runId,
            workItemId,
            packPrice,
            observedAtUtc ?? PriceCatalogueFixture.CreatedAt);

        return new ArrangedListing(listingId, runId, workItemId);
    }

    private sealed record ArrangedComparedListing(
        long ListingId,
        long MasterProductId,
        long AcceptedObservationId);

    /// <summary>
    /// One shelf a business is already comparing, flagged for review: its own linked supplier, its own
    /// matched mapping, a <em>confirmed</em> listing of the master product that mapping names, an
    /// accepted price still inside the supplier's freshness limit, and the reading #937 held back.
    ///
    /// <para>It is arranged so the whole claim can be stated over the real cheapest-supplier summary:
    /// everything a price needs to reach a business is here, so a comparison that changes after a
    /// review changed because of the review.</para>
    /// </summary>
    private static async Task<ArrangedComparedListing> ArrangeComparedListingAsync(
        PriceCatalogueFixture fixture,
        decimal acceptedPackPrice,
        decimal pendingPackPrice,
        DateTime pendingObservedAtUtc)
    {
        await fixture.SeedSupplierAsync(
            fixture.FirstBusinessId,
            "Alpha Grocer",
            fixture.SupplierPresetId);

        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.MatchAsync(mapping.Id, master);

        var listingId = await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "SKU-COLA-30",
            SupplierListingMatchStatus.Confirmed);

        var runId = await fixture.SeedRunAsync(PriceCatalogueFixture.CreatedAt, null);
        var workItemId = await fixture.SeedWorkItemAsync(runId, master, null);

        // The price the business compares today, accepted against the 30-pack the catalogue holds.
        var acceptedObservationId = await fixture.SeedObservationAsync(
            listingId,
            runId,
            acceptedPackPrice);

        // And then a run flagged it, so the reviewer is looking at a held reading on a listing that
        // already has a price - which is the state the finding of 30 October 2026 is about.
        await fixture.SetListingStatusAsync(listingId, SupplierListingMatchStatus.NeedsReview);
        await fixture.SeedPendingObservationAsync(
            listingId,
            runId,
            workItemId,
            pendingPackPrice,
            pendingObservedAtUtc);

        return new ArrangedComparedListing(listingId, master, acceptedObservationId);
    }

    /// <summary>
    /// The first business's whole cheapest-supplier answer for its cola, through the
    /// <strong>real</strong> use case over the <strong>real</strong> adapter on that business's own
    /// scope: nothing about the comparison is restated here.
    /// </summary>
    private static Task<CheapestSupplierSummary> SummaryAsync(
        PriceCatalogueFixture fixture,
        DateTime asOfUtc) =>
        new GetCheapestSupplierSummary(
                new InventoryApi.Tests.Application.Gst.FakeCurrentBusinessProvider(
                    fixture.FirstBusinessId),
                fixture.SummaryStoreFor(fixture.FirstBusinessId),
                new InventoryApi.Tests.Application.Time.FakeClock(asOfUtc))
            .Handle([fixture.FirstBusinessColaId], CancellationToken.None);

    /// <summary>The one supplier offer in that answer.</summary>
    private static async Task<SupplierUnitPriceOffer> ComparedOfferAsync(
        PriceCatalogueFixture fixture,
        DateTime asOfUtc) =>
        Assert.Single(Assert.Single((await SummaryAsync(fixture, asOfUtc)).Products).Suppliers);

    /// <summary>
    /// One collector row reading the pack the reviewer confirmed: the stored identity exactly, on the
    /// supplier master record's own price basis and with the rate that supplier charges, so the only
    /// thing it says is the price.
    /// </summary>
    private static PriceCollectorResultRowSubmission ConfirmedPackRow(
        long workItemId,
        int supplierPresetId,
        decimal packPrice) =>
        new(
            workItemId,
            supplierPresetId,
            "SKU-COLA-30",
            "Alpha Grocer cola",
            "Classic",
            375m,
            MasterProductSizeUnit.Millilitre,
            "0012345678905",
            BarcodeScope.Unit,
            36,
            packPrice,
            RegularPackPrice: null,
            SupplierPriceObservationPolicy.Currency,
            SupplierPriceBasis.ExclusiveOfGst,
            SupplierGstRate: 0.1000m,
            IsPromotion: false,
            PromotionEndsAtUtc: null,
            SupplierStockStatus.InStock,
            NextRunObserved,
            "review-regression",
            SourceUrl: null);

    private sealed record ArrangedAutomaticMatch(
        long SuggestionId,
        long MasterProductId,
        long AutoMatchedMappingId,
        long ProposedListingId,
        long ConfirmedListingId);

    /// <summary>
    /// One automatic match exactly as #941 leaves one: the mappings auto-matched, the suggestion
    /// recorded as auto-applied, and the key's listings moved to the master product keeping their own
    /// match status and their origin key.
    /// </summary>
    private static async Task<ArrangedAutomaticMatch> ArrangeAutomaticMatchAsync(
        PriceCatalogueFixture fixture)
    {
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.MatchAsync(
            mapping.Id,
            master,
            ProductPriceTrackingStatus.AutoMatched,
            ProductPriceTrackingMatchSource.CollectorAutomaticMatch);

        var suggestionId = await fixture.SeedSuggestionAsync(
            ColaKey,
            master,
            ProductMatchSuggestionStatus.AutoApplied,
            confidence: 1m,
            collectorRun: "price-collector-run-9");

        var proposedListingId = await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "SKU-MOVED",
            SupplierListingMatchStatus.Proposed);
        var confirmedListingId = await fixture.SeedIdentifiedListingAsync(
            master,
            waitingKey: null,
            "SKU-CONFIRMED",
            SupplierListingMatchStatus.Confirmed);

        await fixture.SetOriginWaitingKeyAsync(proposedListingId, ColaKey);
        await fixture.SetOriginWaitingKeyAsync(confirmedListingId, ColaKey);

        return new ArrangedAutomaticMatch(
            suggestionId,
            master,
            mapping.Id,
            proposedListingId,
            confirmedListingId);
    }
}
