using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the collector results endpoint (issue #937, parent #817): what a submitted
/// row may do to the shared catalogue, what it may not, and what is recorded either way.
///
/// <para><strong>Two businesses throughout, on the denied scope a collector request carries.</strong>
/// The fixture's two differently spelled colas are here for the reason they are everywhere in this
/// catalogue - one product is one search however many businesses track it - and the store under
/// test is composed on a context with no business at all, which is the scope
/// <c>POST api/price-collector/results</c> actually has. Relational SQLite over the real migration
/// history rather than InMemory, because what is under test is the idempotency index, the foreign
/// keys, the check constraints and one transaction.</para>
///
/// <para><strong>The rules themselves are <c>PriceCollectorResultPolicyTests</c>'.</strong> The band
/// edges, each identity field and the two sanity checks are stated there over a table of cases;
/// what is asserted here is that the endpoint reaches those rules with the right facts and then
/// writes - or refuses to write - what they decided.</para>
/// </summary>
public class PriceCollectorResultsTests
{
    private static readonly DateTime RunStarted = new(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Expires = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Observed = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    private const string WaitingName = "Coca-Cola Classic";
    private const string WaitingUnit = "375mL";

    #region Work-item checks

    /// <summary>A row citing a work-item id nothing issued is refused, and writes nothing at all.</summary>
    [Fact]
    public async Task A_row_citing_an_unknown_work_item_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var runId = await fixture.SeedRunAsync(RunStarted, Expires);

        var receipt = await SubmitAsync(fixture, runId, Row(9_999, fixture.SupplierPresetId, "SKU-1"));

        var refused = Assert.Single(receipt!.Rejected);
        Assert.Equal(PriceCollectorResultPolicy.UnknownWorkItemReason, refused.Reason);
        Assert.Empty(await fixture.AllListingsAsync());
        Assert.Empty(await fixture.AllResultRowsAsync());
    }

    /// <summary>And one citing another run's work item is refused as that, not as unknown.</summary>
    [Fact]
    public async Task A_row_citing_another_runs_work_item_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var otherRunId = await fixture.SeedRunAsync(RunStarted, Expires);
        var otherWorkItemId = await fixture.SeedWorkItemAsync(otherRunId, cola, waitingKey: null);
        var runId = await fixture.SeedRunAsync(RunStarted, Expires);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Row(otherWorkItemId, fixture.SupplierPresetId, "SKU-1"));

        var refused = Assert.Single(receipt!.Rejected);
        Assert.Equal(PriceCollectorResultPolicy.WorkItemFromAnotherRunReason, refused.Reason);
        Assert.Empty(await fixture.AllListingsAsync());
    }

    /// <summary>A row read at a supplier the work item was not issued for cannot answer it.</summary>
    [Fact]
    public async Task A_row_whose_supplier_differs_from_the_work_items_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var otherSupplierId = await fixture.SeedSupplierPresetAsync("Beta Grocer");

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var receipt = await SubmitAsync(fixture, runId, Row(workItemId, otherSupplierId, "SKU-1"));

        var refused = Assert.Single(receipt!.Rejected);
        Assert.Equal(PriceCollectorResultPolicy.SupplierDiffersFromWorkItemReason, refused.Reason);
        Assert.Empty(await fixture.AllListingsAsync());

        // Recorded, because the work item is real and the key is complete: a repeat is answered
        // with this refusal rather than decided again.
        var recorded = Assert.Single(await fixture.AllResultRowsAsync());
        Assert.Equal(PriceCollectorRowOutcome.Rejected, recorded.Outcome);
    }

    #endregion

    #region No silent re-linking

    /// <summary>
    /// The criterion: a listing that already belongs to another product is refused, and the link it
    /// already had is not touched.
    /// </summary>
    [Fact]
    public async Task A_listing_of_another_product_is_refused_and_its_link_is_unchanged()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var pepsi = await fixture.SeedMasterProductAsync("Pepsi", "Max");

        var listingId = await fixture.SeedIdentifiedListingAsync(
            pepsi,
            waitingKey: null,
            "SKU-SHARED",
            SupplierListingMatchStatus.Confirmed);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Row(workItemId, fixture.SupplierPresetId, "SKU-SHARED"));

        var refused = Assert.Single(receipt!.Rejected);
        Assert.Equal(PriceCollectorResultPolicy.ListingBelongsToAnotherProductReason, refused.Reason);

        var listing = await fixture.ListingAsync(listingId);
        Assert.Equal(pepsi, listing.MasterProductId);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
        Assert.Null(listing.LatestObservationId);
        Assert.Empty(await fixture.AllObservationsAsync());
    }

    /// <summary>
    /// And when the search was for a waiting key, the refusal is also kept as matching evidence -
    /// once, and as a tentative suggestion a person decides on, never as a link.
    /// </summary>
    [Fact]
    public async Task A_master_products_listing_found_under_a_waiting_key_is_recorded_as_evidence()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var pepsi = await fixture.SeedMasterProductAsync("Pepsi", "Max");
        var waitingKey = PriceCatalogueWaitingKey.For(WaitingName, WaitingUnit);

        var listingId = await fixture.SeedIdentifiedListingAsync(
            pepsi,
            waitingKey: null,
            "SKU-SHARED",
            SupplierListingMatchStatus.Confirmed);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, masterProductId: null, waitingKey);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Row(workItemId, fixture.SupplierPresetId, "SKU-SHARED"));

        Assert.Equal(
            PriceCollectorResultPolicy.ListingBelongsToAnotherProductReason,
            Assert.Single(receipt!.Rejected).Reason);

        var evidence = Assert.Single(await fixture.AllSuggestionsAsync());
        Assert.Equal(waitingKey, evidence.WaitingKey);
        Assert.Equal(pepsi, evidence.MasterProductId);
        Assert.Equal(ProductMatchSuggestionStatus.Tentative, evidence.Status);

        // The link itself is untouched: only #928 may change it.
        Assert.Equal(pepsi, (await fixture.ListingAsync(listingId)).MasterProductId);

        // And a second run finding the same thing adds no second row - one refusal is one piece of
        // evidence, not one per run for ever.
        var secondRunId = await fixture.SeedRunAsync(RunStarted, Expires);
        var secondWorkItemId = await fixture.SeedWorkItemAsync(
            secondRunId,
            masterProductId: null,
            waitingKey);

        await SubmitAsync(fixture, secondRunId, Row(secondWorkItemId, fixture.SupplierPresetId, "SKU-SHARED"));

        Assert.Single(await fixture.AllSuggestionsAsync());
    }

    #endregion

    #region Same-run key resolution

    /// <summary>
    /// The criterion: a work item issued for a waiting key, matched to a master product since,
    /// resolves to that master product when its rows are processed - a known listing of that master
    /// is this product's listing, and a new pack attaches to the master rather than to the key.
    /// </summary>
    [Fact]
    public async Task A_matched_waiting_key_resolves_to_its_master_product_for_its_own_rows()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var waitingKey = PriceCatalogueWaitingKey.For(WaitingName, WaitingUnit);

        var knownId = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-30",
            SupplierListingMatchStatus.Confirmed);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, masterProductId: null, waitingKey);

        // The match #941 would have applied earlier in the same run, arranged directly because that
        // issue lands later.
        await fixture.SeedMatchSuggestionAsync(waitingKey, cola, ProductMatchSuggestionStatus.AutoApplied);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Row(workItemId, fixture.SupplierPresetId, "SKU-30"),
            Row(workItemId, fixture.SupplierPresetId, "SKU-24", packQuantity: 24, packPrice: 24m));

        // The known confirmed listing was priced as this product's listing rather than refused.
        var accepted = Assert.Single(receipt!.Accepted);
        Assert.Equal(knownId, accepted.ListingId);

        // And the new pack is attached to the master product, not held under the key.
        var created = Assert.Single(
            (await fixture.AllListingsAsync()).Where(listing => listing.Id != knownId));
        Assert.Equal(cola, created.MasterProductId);
        Assert.Null(created.WaitingKey);
        Assert.Equal(waitingKey, created.OriginWaitingKey);
        Assert.Equal(24, created.UnitsPerPack);
    }

    /// <summary>
    /// And while nothing has matched the key, a newly found listing is held under that key - which
    /// is where #941 and #928 later move it from.
    /// </summary>
    [Fact]
    public async Task A_new_listing_under_an_unmatched_waiting_key_is_stored_against_that_key()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var waitingKey = PriceCatalogueWaitingKey.For(WaitingName, WaitingUnit);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, masterProductId: null, waitingKey);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Row(workItemId, fixture.SupplierPresetId, "SKU-NEW"));

        var held = Assert.Single(receipt!.Pending);
        var listing = Assert.Single(await fixture.AllListingsAsync());

        Assert.Equal(listing.Id, held.ListingId);
        Assert.Null(listing.MasterProductId);
        Assert.Equal(waitingKey, listing.WaitingKey);
        Assert.Equal(waitingKey, listing.OriginWaitingKey);
        Assert.Equal(SupplierListingMatchStatus.Proposed, listing.MatchStatus);

        // A proposal's first price is evidence, never a price: no observation, and the listing has
        // no latest accepted one.
        Assert.Empty(await fixture.AllObservationsAsync());
        Assert.Null(listing.LatestObservationId);
        Assert.Single(await fixture.AllPendingObservationsAsync());
    }

    #endregion

    #region Accepting a price

    /// <summary>
    /// A confirmed listing whose identity still matches and whose price is inside its band is
    /// accepted: one observation, the listing's own price state moved, and nothing pending left.
    /// </summary>
    [Fact]
    public async Task A_confirmed_listing_inside_its_band_is_accepted_as_an_observation()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var listingId = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-30",
            SupplierListingMatchStatus.Confirmed);

        var earlierRunId = await fixture.SeedRunAsync(RunStarted.AddDays(-1), null);
        await fixture.SeedAcceptedPriceAsync(
            listingId,
            earlierRunId,
            30m,
            RunStarted.AddDays(-1),
            SupplierPriceBasis.ExclusiveOfGst);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Row(workItemId, fixture.SupplierPresetId, "SKU-30", packPrice: 33m, gstRate: 0.10m));

        var accepted = Assert.Single(receipt!.Accepted);
        Assert.Null(accepted.Reason);

        var observation = Assert.Single(
            (await fixture.AllObservationsAsync()).Where(row => row.PriceCollectorRunId == runId));
        Assert.Equal(33m, observation.PackPrice);
        Assert.Equal(SupplierPriceObservationPolicy.Currency, observation.Currency);
        Assert.Equal(0.10m, observation.SupplierGstRate);
        Assert.Equal(Observed, observation.ObservedAtUtc);
        Assert.Equal(observation.Id, accepted.ObservationId);

        var listing = await fixture.ListingAsync(listingId);
        Assert.Equal(observation.Id, listing.LatestObservationId);
        Assert.Equal(Observed, listing.LastCheckedAtUtc);
        Assert.Equal(Observed, listing.LastPriceChangeAtUtc);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
        Assert.Empty(await fixture.AllPendingObservationsAsync());
    }

    /// <summary>The supplier's own GST rate stays unknown when the supplier does not publish one.</summary>
    [Fact]
    public async Task An_unpublished_supplier_gst_rate_is_stored_as_unknown()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var listingId = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-30",
            SupplierListingMatchStatus.Confirmed);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        await SubmitAsync(fixture, runId, Row(workItemId, fixture.SupplierPresetId, "SKU-30"));

        var observation = Assert.Single(await fixture.AllObservationsAsync());
        Assert.Equal(listingId, observation.SupplierListingId);
        Assert.Null(observation.SupplierGstRate);
    }

    /// <summary>
    /// Both band edges are inside the band, and a price past either one is held with the listing
    /// flagged rather than priced or discarded.
    /// </summary>
    [Theory]
    [InlineData(15.00, true)]   // exactly 50% below
    [InlineData(60.00, true)]   // exactly 100% above
    [InlineData(14.99, false)]
    [InlineData(60.01, false)]
    public async Task The_band_edges_are_accepted_and_anything_past_them_goes_to_review(
        double packPrice,
        bool accepted)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var listingId = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-30",
            SupplierListingMatchStatus.Confirmed);

        var earlierRunId = await fixture.SeedRunAsync(RunStarted.AddDays(-1), null);
        await fixture.SeedAcceptedPriceAsync(
            listingId,
            earlierRunId,
            30m,
            RunStarted.AddDays(-1),
            SupplierPriceBasis.ExclusiveOfGst);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Row(workItemId, fixture.SupplierPresetId, "SKU-30", packPrice: (decimal)packPrice));

        var listing = await fixture.ListingAsync(listingId);

        if (accepted)
        {
            Assert.Single(receipt!.Accepted);
            Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
            Assert.Empty(await fixture.AllPendingObservationsAsync());
            return;
        }

        var review = Assert.Single(receipt!.SentToReview);
        Assert.Equal(PriceCollectorResultPolicy.PriceOutsideBandReason, review.Reason);
        Assert.Equal(SupplierListingMatchStatus.NeedsReview, listing.MatchStatus);

        // The old price is still the listing's price: a reading that was not accepted replaces
        // nothing.
        Assert.Equal(30m, (await fixture.AllObservationsAsync()).Single().PackPrice);
        Assert.Equal(
            (decimal)packPrice,
            Assert.Single(await fixture.AllPendingObservationsAsync()).PackPrice);
    }

    #endregion

    #region Every other failed check refuses the row with a reason

    /// <summary>
    /// The payload checks, the window and the host, each refusing one row with its own reason and
    /// writing nothing about the listing. They are stated together because the rule is one rule -
    /// "every other failed check rejects the row with a reason" - and the reasons are what a
    /// collector reads to fix itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedRows))]
    public async Task A_row_that_fails_any_other_check_is_refused_with_its_reason(
        string change,
        string expectedReason)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SetSupplierCollectionAsync(
            fixture.SupplierPresetId,
            Inventory.Domain.Suppliers.SupplierPriceConnector.Kmart,
            url: "https://alpha.example");

        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var listingId = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-30",
            SupplierListingMatchStatus.Confirmed);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var row = change switch
        {
            "zero price" => Row(workItemId, fixture.SupplierPresetId, "SKU-30", packPrice: 0m),
            "currency" => Row(workItemId, fixture.SupplierPresetId, "SKU-30", currency: "USD"),
            "pack of zero" => Row(workItemId, fixture.SupplierPresetId, "SKU-30", packQuantity: 0),
            "barcode without scope" => Row(
                workItemId,
                fixture.SupplierPresetId,
                "SKU-30",
                barcodeScope: null),
            "gst rate" => Row(workItemId, fixture.SupplierPresetId, "SKU-30", gstRate: 10m),
            "basis" => Row(
                workItemId,
                fixture.SupplierPresetId,
                "SKU-30",
                priceBasis: SupplierPriceBasis.InclusiveOfGst),
            "before the run" => Row(
                workItemId,
                fixture.SupplierPresetId,
                "SKU-30",
                observedAtUtc: RunStarted.AddMinutes(-1)),
            "in the future" => Row(
                workItemId,
                fixture.SupplierPresetId,
                "SKU-30",
                observedAtUtc: Now.AddMinutes(1)),
            "another site" => Row(
                workItemId,
                fixture.SupplierPresetId,
                "SKU-30",
                sourceUrl: "https://beta.example/p/1"),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown case."),
        };

        var receipt = await SubmitAsync(fixture, runId, row);

        Assert.Equal(expectedReason, Assert.Single(receipt!.Rejected).Reason);
        Assert.Empty(await fixture.AllObservationsAsync());
        Assert.Empty(await fixture.AllPendingObservationsAsync());
        Assert.Equal(
            SupplierListingMatchStatus.Confirmed,
            (await fixture.ListingAsync(listingId)).MatchStatus);
    }

    public static TheoryData<string, string> RefusedRows() =>
        new()
        {
            { "zero price", SupplierPriceObservationPolicy.PackPriceNotPositiveMessage },
            { "currency", SupplierPriceObservationPolicy.CurrencyNotSupportedMessage },
            { "pack of zero", SupplierListingPolicy.UnitsPerPackNotPositiveMessage },
            { "barcode without scope", SupplierListingPolicy.BarcodeScopeRequiredMessage },
            { "gst rate", SupplierPriceObservationPolicy.GstRateOutOfRangeMessage },
            { "basis", PriceCollectorResultPolicy.PriceBasisDiffersFromSupplierReason },
            { "before the run", PriceCollectorResultPolicy.ObservedBeforeRunStartedReason },
            { "in the future", PriceCollectorResultPolicy.ObservedInFutureReason },
            { "another site", PriceCollectorResultPolicy.SourceLinkHostMismatchReason },
        };

    #endregion

    #region Identity and pack changes

    /// <summary>Every identity field the issue names sends a confirmed listing to review.</summary>
    [Theory]
    [InlineData("variant")]
    [InlineData("size")]
    [InlineData("unit")]
    [InlineData("barcode")]
    [InlineData("scope")]
    [InlineData("pack")]
    public async Task An_identity_change_sends_a_confirmed_listing_to_review(string field)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var listingId = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-30",
            SupplierListingMatchStatus.Confirmed);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var receipt = await SubmitAsync(fixture, runId, Changed(field, workItemId, fixture.SupplierPresetId));

        var review = Assert.Single(receipt!.SentToReview);
        Assert.Equal(PriceCollectorResultPolicy.IdentityChangedReason, review.Reason);

        var listing = await fixture.ListingAsync(listingId);
        Assert.Equal(SupplierListingMatchStatus.NeedsReview, listing.MatchStatus);

        // The confirmed identity is left exactly as a person confirmed it: the reading is beside
        // it, not over it.
        Assert.Equal("Classic", listing.Variant);
        Assert.Equal(375m, listing.SizeValue);
        Assert.Equal(30, listing.UnitsPerPack);
        Assert.Equal("0012345678905", listing.Barcode);
        Assert.Empty(await fixture.AllObservationsAsync());
        Assert.Single(await fixture.AllPendingObservationsAsync());
    }

    /// <summary>And a refused listing whose identity changed is a new proposal rather than a refusal.</summary>
    [Fact]
    public async Task A_rejected_listing_whose_identity_changed_becomes_proposed_again()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var listingId = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-30",
            SupplierListingMatchStatus.Rejected);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var receipt = await SubmitAsync(fixture, runId, Changed("pack", workItemId, fixture.SupplierPresetId));

        Assert.Single(receipt!.SentToReview);
        Assert.Equal(
            SupplierListingMatchStatus.Proposed,
            (await fixture.ListingAsync(listingId)).MatchStatus);
    }

    #endregion

    #region Upsert, never duplicate

    /// <summary>
    /// The criterion: rediscovering a listing updates it and never creates a second one, and each
    /// match status survives the rediscovery.
    /// </summary>
    [Theory]
    [InlineData(SupplierListingMatchStatus.Proposed)]
    [InlineData(SupplierListingMatchStatus.Rejected)]
    [InlineData(SupplierListingMatchStatus.Confirmed)]
    public async Task Rediscovering_a_listing_updates_it_and_keeps_its_status(
        SupplierListingMatchStatus status)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var listingId = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-30",
            status);

        var firstRunId = await fixture.SeedRunAsync(RunStarted, Expires);
        var firstWorkItemId = await fixture.SeedWorkItemAsync(firstRunId, cola, waitingKey: null);
        await SubmitAsync(fixture, firstRunId, Row(firstWorkItemId, fixture.SupplierPresetId, "SKU-30"));

        var secondRunId = await fixture.SeedRunAsync(RunStarted, Expires);
        var secondWorkItemId = await fixture.SeedWorkItemAsync(secondRunId, cola, waitingKey: null);
        await SubmitAsync(fixture, secondRunId, Row(secondWorkItemId, fixture.SupplierPresetId, "SKU-30"));

        var listing = Assert.Single(await fixture.AllListingsAsync());
        Assert.Equal(listingId, listing.Id);
        Assert.Equal(status, listing.MatchStatus);

        // No second proposal either: one listing holds one held reading, replaced rather than
        // appended.
        Assert.True((await fixture.AllPendingObservationsAsync()).Count <= 1);
    }

    /// <summary>A second held reading replaces the first rather than being appended beside it.</summary>
    [Fact]
    public async Task A_pending_observation_is_replaced_rather_than_appended()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-30",
            SupplierListingMatchStatus.Proposed);

        var firstRunId = await fixture.SeedRunAsync(RunStarted, Expires);
        var firstWorkItemId = await fixture.SeedWorkItemAsync(firstRunId, cola, waitingKey: null);
        await SubmitAsync(
            fixture,
            firstRunId,
            Row(firstWorkItemId, fixture.SupplierPresetId, "SKU-30", packPrice: 30m));

        var secondRunId = await fixture.SeedRunAsync(RunStarted, Expires);
        var secondWorkItemId = await fixture.SeedWorkItemAsync(secondRunId, cola, waitingKey: null);
        await SubmitAsync(
            fixture,
            secondRunId,
            Row(secondWorkItemId, fixture.SupplierPresetId, "SKU-30", packPrice: 31m));

        var pending = Assert.Single(await fixture.AllPendingObservationsAsync());
        Assert.Equal(31m, pending.PackPrice);
        Assert.Equal(secondRunId, pending.PriceCollectorRunId);
    }

    #endregion

    #region Idempotency and the batch bound

    /// <summary>
    /// The criterion: one search may return a 24-pack and a 30-pack, both are kept, and each is
    /// idempotent on its own key - accepting one never suppresses the other.
    /// </summary>
    [Fact]
    public async Task Two_packs_from_one_search_are_both_kept_and_each_is_idempotent()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var first = await SubmitAsync(
            fixture,
            runId,
            Row(workItemId, fixture.SupplierPresetId, "SKU-30", packQuantity: 30, packPrice: 30m),
            Row(workItemId, fixture.SupplierPresetId, "SKU-24", packQuantity: 24, packPrice: 25m));

        Assert.Equal(2, first!.Pending.Count);
        Assert.Equal(2, (await fixture.AllListingsAsync()).Count);

        var again = await SubmitAsync(
            fixture,
            runId,
            Row(workItemId, fixture.SupplierPresetId, "SKU-30", packQuantity: 30, packPrice: 99m),
            Row(workItemId, fixture.SupplierPresetId, "SKU-24", packQuantity: 24, packPrice: 99m));

        // The earlier outcomes, verbatim, and nothing written: still two listings, two recorded
        // rows and the prices the first submission held.
        Assert.Equal(2, again!.Pending.Count);
        Assert.Equal(2, (await fixture.AllListingsAsync()).Count);
        Assert.Equal(2, (await fixture.AllResultRowsAsync()).Count);
        Assert.Equal(
            [25m, 30m],
            (await fixture.AllPendingObservationsAsync())
                .Select(row => row.PackPrice)
                .Order()
                .ToArray());
    }

    /// <summary>Two rows claiming one key inside one batch: the first is decided, the second refused.</summary>
    [Fact]
    public async Task A_key_repeated_inside_one_batch_is_refused_the_second_time()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Row(workItemId, fixture.SupplierPresetId, "SKU-30"),
            Row(workItemId, fixture.SupplierPresetId, "SKU-30", packPrice: 99m));

        Assert.Single(receipt!.Pending);
        Assert.Equal(
            PriceCollectorResultPolicy.DuplicateRowInBatchReason,
            Assert.Single(receipt.Rejected).Reason);
        Assert.Single(await fixture.AllListingsAsync());
    }

    /// <summary>A batch past the bound is refused whole, with nothing stored.</summary>
    [Fact]
    public async Task A_batch_larger_than_the_bound_is_refused_whole()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var rows = Enumerable
            .Range(0, PriceCollectorResultPolicy.MaxRowsPerBatch + 1)
            .Select(index => Row(workItemId, fixture.SupplierPresetId, $"SKU-{index:D4}"))
            .ToList();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => new RecordPriceCollectorResults(
                    fixture.DeniedResultStore(),
                    new InventoryApi.Tests.Application.Time.FakeClock(Now))
                .Handle(new PriceCollectorResultBatch(runId, rows), CancellationToken.None));

        Assert.Equal(PriceCollectorResultPolicy.BatchTooLargeMessage, refusal.Message);
        Assert.Empty(await fixture.AllListingsAsync());
        Assert.Empty(await fixture.AllResultRowsAsync());
    }

    #endregion

    #region Search outcomes

    /// <summary>
    /// The criterion: a search split across three batches is finished only by its outcome call with
    /// the right total. A wrong total is refused and leaves the search unanswered, rows after the
    /// answer are refused, and no listing is recorded as not found before the answer.
    /// </summary>
    [Fact]
    public async Task A_search_split_across_batches_is_finished_only_by_the_right_total()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var absentId = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-GONE",
            SupplierListingMatchStatus.Confirmed);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        foreach (var sku in new[] { "SKU-1", "SKU-2", "SKU-3" })
        {
            await SubmitAsync(fixture, runId, Row(workItemId, fixture.SupplierPresetId, sku));
        }

        // Nothing is marked missing while the search is unanswered.
        Assert.Null((await fixture.ListingAsync(absentId)).LastNotFoundAtUtc);

        var wrongTotal = await Assert.ThrowsAsync<DomainConflictException>(
            () => AnswerAsync(fixture, runId, workItemId, PriceCollectorWorkItemOutcome.Complete, 2));

        Assert.Equal(PriceCollectorWorkItemPolicy.SubmittedTotalMismatchMessage, wrongTotal.Message);
        Assert.Null((await fixture.ListingAsync(absentId)).LastNotFoundAtUtc);
        Assert.Null(Assert.Single(await fixture.AllWorkItemsAsync()).Outcome);

        var receipt = await AnswerAsync(
            fixture,
            runId,
            workItemId,
            PriceCollectorWorkItemOutcome.Complete,
            3);

        Assert.Equal(PriceCollectorWorkItemOutcome.Complete, receipt!.Outcome);
        Assert.Equal(1, receipt.ListingsRecordedNotFound);
        Assert.Equal(Now, (await fixture.ListingAsync(absentId)).LastNotFoundAtUtc);

        // And a row that arrives afterwards is refused.
        var late = await SubmitAsync(fixture, runId, Row(workItemId, fixture.SupplierPresetId, "SKU-4"));
        Assert.Equal(
            PriceCollectorResultPolicy.SearchAlreadyAnsweredReason,
            Assert.Single(late!.Rejected).Reason);
    }

    /// <summary>
    /// Only a complete or no-match search may record a listing as missing. A truncated or failed
    /// one saw less than everything, so an absence proves nothing.
    /// </summary>
    [Theory]
    [InlineData(PriceCollectorWorkItemOutcome.Complete, true)]
    [InlineData(PriceCollectorWorkItemOutcome.NoMatches, true)]
    [InlineData(PriceCollectorWorkItemOutcome.Truncated, false)]
    [InlineData(PriceCollectorWorkItemOutcome.Failed, false)]
    public async Task Only_a_complete_or_no_match_search_records_a_listing_as_not_found(
        PriceCollectorWorkItemOutcome outcome,
        bool recorded)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var absentId = await fixture.SeedIdentifiedListingAsync(
            cola,
            waitingKey: null,
            "SKU-GONE",
            SupplierListingMatchStatus.Confirmed);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var receipt = await AnswerAsync(
            fixture,
            runId,
            workItemId,
            outcome,
            submittedRowCount: 0,
            outcome == PriceCollectorWorkItemOutcome.Failed
                ? PriceCollectorSearchFailureReason.Timeout
                : null);

        Assert.Equal(recorded ? 1 : 0, receipt!.ListingsRecordedNotFound);
        Assert.Equal(recorded ? Now : null, (await fixture.ListingAsync(absentId)).LastNotFoundAtUtc);
    }

    /// <summary>A second, different answer is refused; an identical one is accepted and changes nothing.</summary>
    [Fact]
    public async Task A_search_is_answered_once_and_an_identical_retry_is_not_a_conflict()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        await AnswerAsync(fixture, runId, workItemId, PriceCollectorWorkItemOutcome.NoMatches, 0);

        var again = await AnswerAsync(
            fixture,
            runId,
            workItemId,
            PriceCollectorWorkItemOutcome.NoMatches,
            0);

        Assert.Equal(PriceCollectorWorkItemOutcome.NoMatches, again!.Outcome);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => AnswerAsync(fixture, runId, workItemId, PriceCollectorWorkItemOutcome.Truncated, 0));

        Assert.Equal(PriceCollectorWorkItemPolicy.AlreadyAnsweredDifferentlyMessage, conflict.Message);
        Assert.Equal(
            PriceCollectorWorkItemOutcome.NoMatches,
            Assert.Single(await fixture.AllWorkItemsAsync()).Outcome);
    }

    /// <summary>A collector may not claim the completion's own "not searched" answer.</summary>
    [Fact]
    public async Task A_collector_cannot_answer_a_search_as_not_searched()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => AnswerAsync(fixture, runId, workItemId, PriceCollectorWorkItemOutcome.NotSearched, 0));

        Assert.Equal(PriceCollectorWorkItemPolicy.UnreportableOutcomeMessage, refusal.Message);
        Assert.Null(Assert.Single(await fixture.AllWorkItemsAsync()).Outcome);
    }

    /// <summary>"Nothing matched" and "it failed" both require that no row was submitted for it.</summary>
    [Fact]
    public async Task A_no_match_answer_contradicted_by_submitted_rows_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        await SubmitAsync(fixture, runId, Row(workItemId, fixture.SupplierPresetId, "SKU-1"));

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => AnswerAsync(fixture, runId, workItemId, PriceCollectorWorkItemOutcome.NoMatches, 1));

        Assert.Equal(PriceCollectorWorkItemPolicy.OutcomeRequiresNoRowsMessage, refusal.Message);
    }

    #endregion

    #region Run completion and expiry

    /// <summary>
    /// Closing a run turns every unanswered search into <c>NotSearched</c>, records one count per
    /// outcome, and then refuses every later result.
    /// </summary>
    [Fact]
    public async Task Completing_a_run_records_its_counts_and_closes_it_to_results()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var pepsi = await fixture.SeedMasterProductAsync("Pepsi", "Max");

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var answeredId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);
        var unansweredId = await fixture.SeedWorkItemAsync(runId, pepsi, waitingKey: null);

        await AnswerAsync(fixture, runId, answeredId, PriceCollectorWorkItemOutcome.Complete, 0);

        var completion = await CompleteAsync(fixture, runId);

        Assert.Equal(PriceCollectorRunStatus.PartiallySucceeded, completion!.Status);
        Assert.Equal(1, completion.Counts.Complete);
        Assert.Equal(1, completion.Counts.NotSearched);

        var stored = await fixture.AllWorkItemsAsync();
        Assert.Equal(
            PriceCollectorWorkItemOutcome.NotSearched,
            stored.Single(item => item.Id == unansweredId).Outcome);

        var run = Assert.Single(
            (await fixture.AllRunsAsync()).Where(candidate => candidate.Id == runId));
        Assert.Equal(1, run.CompleteSearchCount);
        Assert.Equal(1, run.NotSearchedCount);
        Assert.Equal(Now, run.CompletedAtUtc);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => SubmitAsync(fixture, runId, Row(unansweredId, fixture.SupplierPresetId, "SKU-1")));

        Assert.Equal(PriceCollectorRunPolicy.AlreadyFinishedMessage, refusal.Message);
    }

    /// <summary>
    /// And a run left open past its lifetime is expired the next time it is touched, with the same
    /// effect on its searches - then the touch itself is refused.
    /// </summary>
    [Fact]
    public async Task Touching_an_overdue_run_expires_it_with_the_same_effect_and_refuses_the_call()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        // Expired an hour before the call.
        var runId = await fixture.SeedRunAsync(RunStarted, Now.AddHours(-1));
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => SubmitAsync(fixture, runId, Row(workItemId, fixture.SupplierPresetId, "SKU-1")));

        Assert.Equal(PriceCollectorRunPolicy.AlreadyFinishedMessage, refusal.Message);

        var run = Assert.Single(await fixture.AllRunsAsync());
        Assert.Equal(PriceCollectorRunStatus.Expired, run.Status);
        Assert.Equal(Now, run.CompletedAtUtc);
        Assert.Equal(1, run.NotSearchedCount);

        Assert.Equal(
            PriceCollectorWorkItemOutcome.NotSearched,
            Assert.Single(await fixture.AllWorkItemsAsync()).Outcome);
        Assert.Empty(await fixture.AllListingsAsync());
    }

    /// <summary>A run nothing issued is not a run whose rows could be refused one by one.</summary>
    [Fact]
    public async Task An_unknown_run_is_not_found()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        Assert.Null(await SubmitAsync(fixture, 9_999, Row(1, fixture.SupplierPresetId, "SKU-1")));
        Assert.Null(await CompleteAsync(fixture, 9_999));
    }

    #endregion

    #region The boundary

    /// <summary>
    /// Nothing a business owns is written by any of it. The whole surface is the shared catalogue,
    /// and a collector request carries no business at all - so a submission, an answer and a
    /// completion together must leave both businesses' products, purchases, suppliers, stock and
    /// price-tracking mappings exactly as they were.
    /// </summary>
    [Fact]
    public async Task Nothing_tenant_owned_is_written_by_a_submission_an_answer_or_a_completion()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        await fixture.MatchAsync(mine.Id, cola);
        await fixture.MatchAsync(theirs.Id, cola);

        var before = await fixture.TenantOwnedFingerprintAsync();

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var workItemId = await fixture.SeedWorkItemAsync(runId, cola, waitingKey: null);

        await SubmitAsync(fixture, runId, Row(workItemId, fixture.SupplierPresetId, "SKU-30"));
        await AnswerAsync(fixture, runId, workItemId, PriceCollectorWorkItemOutcome.Complete, 1);
        await CompleteAsync(fixture, runId);

        Assert.Equal(before, await fixture.TenantOwnedFingerprintAsync());

        // And the catalogue did change, so the comparison above is not passing for want of work.
        Assert.Single(await fixture.AllListingsAsync());
    }

    #endregion

    #region Helpers

    private static Task<PriceCollectorResultReceipt?> SubmitAsync(
        PriceCatalogueFixture fixture,
        long runId,
        params PriceCollectorResultRowSubmission[] rows) =>
        new RecordPriceCollectorResults(
                fixture.DeniedResultStore(),
                new InventoryApi.Tests.Application.Time.FakeClock(Now))
            .Handle(new PriceCollectorResultBatch(runId, rows), CancellationToken.None);

    private static Task<PriceCollectorSearchAnswerReceipt?> AnswerAsync(
        PriceCatalogueFixture fixture,
        long runId,
        long workItemId,
        PriceCollectorWorkItemOutcome outcome,
        int submittedRowCount,
        PriceCollectorSearchFailureReason? failureReason = null) =>
        new AnswerPriceCollectorSearch(
                fixture.DeniedResultStore(),
                new InventoryApi.Tests.Application.Time.FakeClock(Now))
            .Handle(
                new PriceCollectorSearchAnswer(
                    runId,
                    workItemId,
                    outcome,
                    failureReason,
                    submittedRowCount),
                CancellationToken.None);

    private static Task<PriceCollectorRunCompletion?> CompleteAsync(
        PriceCatalogueFixture fixture,
        long runId) =>
        new CompletePriceCollectorRun(
                fixture.DeniedResultStore(),
                new InventoryApi.Tests.Application.Time.FakeClock(Now))
            .Handle(runId, CancellationToken.None);

    /// <summary>
    /// One submitted row, with the fixture's own supplier basis (its preset publishes ex-GST
    /// prices) and the identity <c>SeedIdentifiedListingAsync</c> stores, so a test says only what
    /// it is changing.
    /// </summary>
    private static PriceCollectorResultRowSubmission Row(
        long workItemId,
        int supplierPresetId,
        string supplierProductId,
        decimal packPrice = 30m,
        int? packQuantity = 30,
        string? variant = "Classic",
        decimal? sizeValue = 375m,
        MasterProductSizeUnit? sizeUnit = MasterProductSizeUnit.Millilitre,
        string? barcode = "0012345678905",
        BarcodeScope? barcodeScope = BarcodeScope.Unit,
        decimal? gstRate = null,
        DateTime? observedAtUtc = null,
        string? sourceUrl = null,
        string? currency = SupplierPriceObservationPolicy.Currency,
        SupplierPriceBasis priceBasis = SupplierPriceBasis.ExclusiveOfGst) =>
        new(
            workItemId,
            supplierPresetId,
            supplierProductId,
            "Alpha Cola 375 ml",
            variant,
            sizeValue,
            sizeUnit,
            barcode,
            barcodeScope,
            packQuantity,
            packPrice,
            RegularPackPrice: null,
            currency,
            priceBasis,
            gstRate,
            IsPromotion: false,
            PromotionEndsAtUtc: null,
            SupplierStockStatus.InStock,
            observedAtUtc ?? Observed,
            "result-1",
            sourceUrl);

    /// <summary>The same row with exactly one identity field moved, named by the theory case.</summary>
    private static PriceCollectorResultRowSubmission Changed(
        string field,
        long workItemId,
        int supplierPresetId) => field switch
        {
            "variant" => Row(workItemId, supplierPresetId, "SKU-30", variant: "Zero Sugar"),
            "size" => Row(workItemId, supplierPresetId, "SKU-30", sizeValue: 600m),
            "unit" => Row(
                workItemId,
                supplierPresetId,
                "SKU-30",
                sizeUnit: MasterProductSizeUnit.Gram),
            "barcode" => Row(workItemId, supplierPresetId, "SKU-30", barcode: "0012345678906"),
            "scope" => Row(
                workItemId,
                supplierPresetId,
                "SKU-30",
                barcodeScope: BarcodeScope.Carton),
            "pack" => Row(workItemId, supplierPresetId, "SKU-30", packQuantity: 24),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown identity field."),
        };

    #endregion
}
