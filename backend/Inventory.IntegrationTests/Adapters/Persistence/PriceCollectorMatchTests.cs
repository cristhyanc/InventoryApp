using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the collector matches endpoint (issue #941, parent #817): which proposals are
/// applied automatically, which mappings an application may change, what a proposal that is not
/// obvious becomes, and what the whole flow may never touch.
///
/// <para><strong>Two businesses throughout, on the denied scope a collector request carries.</strong>
/// The fixture's two differently spelled colas are the case this issue exists inside - one product is
/// one search however many businesses track it - and they are what makes "two businesses waiting on
/// the same name and unit label are both matched by one proposal, and neither learns of the other"
/// assertable at all. The store under test is composed on a context with no business, which is the
/// scope <c>POST api/price-collector/matches</c> actually has, so the reviewed cross-business read and
/// the narrow matching write are both exercised as the endpoint has them.</para>
///
/// <para><strong>The rule itself is <c>AutomaticProductMatchPolicyTests</c>'.</strong> Each condition
/// of "obvious" is stated there over a table of cases; what is asserted here is that the endpoint
/// reaches that rule with the right facts - the search text the mappings carry, the whole master
/// catalogue, the refusals a Super Admin left - and then writes, or refuses to write, exactly what it
/// decided.</para>
/// </summary>
public class PriceCollectorMatchTests
{
    private static readonly DateTime RunStarted = new(2026, 10, 29, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Expires = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    private const string WaitingName = "Coca-Cola Classic";
    private const string WaitingUnit = "375mL";
    private const string MatchReason = "The brand, the variant and the size all agree.";

    private static string WaitingKey => PriceCatalogueWaitingKey.For(WaitingName, WaitingUnit);

    #region An obvious match is applied

    /// <summary>
    /// The issue's own case: two businesses waiting on the same name and unit label are both matched
    /// by one proposal, the match is recorded as automatic with the run, the confidence and the
    /// reason, and neither business appears anywhere in the answer.
    /// </summary>
    [Fact]
    public async Task One_proposal_matches_every_waiting_business_and_names_none_of_them()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var first = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var second = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        Assert.Equal(first.WaitingKey, second.WaitingKey);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola));

        var applied = Assert.Single(receipt!.AutoMatched);
        Assert.Equal(WaitingKey, applied.WaitingKey);
        Assert.Equal(cola, applied.MasterProductId);
        Assert.Null(applied.Reason);
        Assert.NotNull(applied.SuggestionId);

        // Both businesses' mappings, and nothing about either of them in the answer: no count of how
        // many moved, which would be a count of the businesses tracking the product.
        var mappings = await fixture.AllTrackingsAsync();
        Assert.Equal(2, mappings.Count);
        Assert.All(mappings, mapping =>
        {
            Assert.Equal(ProductPriceTrackingStatus.AutoMatched, mapping.Status);
            Assert.Equal(cola, mapping.MasterProductId);
            Assert.Equal(ProductPriceTrackingMatchSource.CollectorAutomaticMatch, mapping.MatchSource);
            Assert.Equal(Now, mapping.UpdatedAtUtc);
        });

        // The search text, the key, the product and the owner are left exactly as they were: the
        // match writes the status, the master product and the source, and nothing else.
        Assert.Equal([fixture.FirstBusinessId, fixture.SecondBusinessId], mappings.Select(m => m.BusinessId));
        Assert.Equal(
            [fixture.FirstBusinessColaId, fixture.SecondBusinessColaId],
            mappings.Select(m => m.ProductId));
        Assert.Equal([WaitingName, "coca-cola  classic"], mappings.Select(m => m.SearchName));
        Assert.All(mappings, mapping => Assert.Equal(WaitingKey, mapping.WaitingKey));

        var suggestion = Assert.Single(await fixture.AllSuggestionsAsync());
        Assert.Equal(ProductMatchSuggestionStatus.AutoApplied, suggestion.Status);
        Assert.Equal(WaitingKey, suggestion.WaitingKey);
        Assert.Equal(cola, suggestion.MasterProductId);
        Assert.Equal(AutomaticProductMatchPolicy.HighConfidenceProportion, suggestion.Confidence);
        Assert.Equal(MatchReason, suggestion.Reason);
        Assert.Equal(ProductMatchSuggestionPolicy.CollectorRunFor(runId), suggestion.CollectorRun);
        Assert.Equal(Now, suggestion.CreatedAtUtc);
    }

    /// <summary>
    /// The listings a previous run found under the key move to the master product, keep the key they
    /// came from so an undo can move them back, and stay proposed - a match moves where a listing
    /// hangs and never decides that somebody confirmed it.
    /// </summary>
    [Fact]
    public async Task The_listings_found_under_the_key_move_to_the_master_product_and_stay_proposed()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var pepsi = await fixture.SeedMasterProductAsync("Pepsi", "Max");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.SeedWaitingListingAsync(WaitingKey, "SKU-COLA-30");
        var otherKeysListing = await fixture.SeedListingAsync(pepsi, "SKU-PEPSI-30");

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        Assert.Single((await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola)))!.AutoMatched);

        var listings = await fixture.AllListingsAsync();
        var moved = Assert.Single(listings.Where(listing => listing.SupplierProductId == "SKU-COLA-30"));

        Assert.Equal(cola, moved.MasterProductId);
        Assert.Null(moved.WaitingKey);
        Assert.Equal(WaitingKey, moved.OriginWaitingKey);
        Assert.Equal(SupplierListingMatchStatus.Proposed, moved.MatchStatus);
        Assert.Equal(Now, moved.UpdatedAtUtc);

        // And nothing else moved: another product's listing is another product's.
        var untouched = Assert.Single(listings.Where(listing => listing.Id == otherKeysListing));
        Assert.Equal(pepsi, untouched.MasterProductId);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, untouched.UpdatedAtUtc);
    }

    #endregion

    #region Only the mappings that are still awaiting a match

    /// <summary>
    /// The issue's third criterion, state by state: a mapping a person settled, one an earlier run
    /// matched, one that was looked for and not found, and one suspended on its own product's details
    /// are all left exactly as they are - only the mappings still awaiting a match move.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.Matched)]
    [InlineData(ProductPriceTrackingStatus.AutoMatched)]
    [InlineData(ProductPriceTrackingStatus.NotFound)]
    public async Task A_mapping_that_is_not_awaiting_a_match_is_untouched(
        ProductPriceTrackingStatus status)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var pepsi = await fixture.SeedMasterProductAsync("Pepsi", "Max");

        // The first business is waiting; the second one's mapping for the same key is not.
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var settled = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        if (status == ProductPriceTrackingStatus.NotFound)
        {
            await fixture.MarkNotFoundAsync(settled.Id);
        }
        else
        {
            await fixture.MatchAsync(
                settled.Id,
                pepsi,
                status,
                status == ProductPriceTrackingStatus.Matched
                    ? ProductPriceTrackingMatchSource.SuperAdmin
                    : ProductPriceTrackingMatchSource.CollectorAutomaticMatch);
        }

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        Assert.Single((await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola)))!.AutoMatched);

        var mappings = await fixture.AllTrackingsAsync();

        var waiting = Assert.Single(mappings.Where(mapping => mapping.BusinessId == fixture.FirstBusinessId));
        Assert.Equal(ProductPriceTrackingStatus.AutoMatched, waiting.Status);
        Assert.Equal(cola, waiting.MasterProductId);

        var other = Assert.Single(mappings.Where(mapping => mapping.BusinessId == fixture.SecondBusinessId));
        Assert.Equal(status, other.Status);
        Assert.Equal(
            status == ProductPriceTrackingStatus.NotFound ? null : pepsi,
            other.MasterProductId);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, other.UpdatedAtUtc);
    }

    /// <summary>
    /// A mapping whose product was renamed or relabelled after the work list was issued carries
    /// another waiting key now, so the match does not reach it: the key is the whole of what an
    /// automatic match is about, and the previous size's search is not this product's.
    /// </summary>
    [Fact]
    public async Task A_mapping_whose_product_changed_since_the_work_list_was_issued_is_skipped()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        // Through the real product edit, which is what refreshes the mapping's key (issue #938).
        await fixture.UpdateProductAsync(
            fixture.SecondBusinessId,
            fixture.SecondBusinessColaId,
            "coca-cola  classic",
            "1.25L");

        Assert.Single((await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola)))!.AutoMatched);

        var mappings = await fixture.AllTrackingsAsync();

        Assert.Equal(
            ProductPriceTrackingStatus.AutoMatched,
            Assert.Single(mappings.Where(m => m.BusinessId == fixture.FirstBusinessId)).Status);

        var moved = Assert.Single(mappings.Where(m => m.BusinessId == fixture.SecondBusinessId));
        Assert.Equal(ProductPriceTrackingStatus.Requested, moved.Status);
        Assert.Null(moved.MasterProductId);
        Assert.NotEqual(WaitingKey, moved.WaitingKey);
    }

    /// <summary>
    /// A mapping suspended on its own product's details (issue #938) is not awaiting a match in the
    /// catalogue's sense: it names no master product, nothing is searched for it, and a match for the
    /// key it still carries leaves it alone.
    /// </summary>
    [Fact]
    public async Task A_suspended_mapping_is_skipped()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var suspended = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        // The generic unit label, which cannot be searched for at all: the mapping is suspended and
        // keeps the text it was suspended with.
        await fixture.UpdateProductAsync(
            fixture.SecondBusinessId,
            fixture.SecondBusinessColaId,
            "coca-cola  classic",
            "unit");

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        Assert.Single((await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola)))!.AutoMatched);

        var stored = Assert.Single(
            (await fixture.AllTrackingsAsync()).Where(mapping => mapping.Id == suspended.Id));

        Assert.Equal(ProductPriceTrackingStatus.AwaitingProductDetails, stored.Status);
        Assert.Null(stored.MasterProductId);
        Assert.Null(stored.MatchSource);
    }

    /// <summary>
    /// A key nothing is waiting under any more is refused rather than recorded: there is no search
    /// text to check a master product against, and a suggestion about a key no business is waiting on
    /// is a decision nobody needs to make.
    /// </summary>
    [Fact]
    public async Task A_key_nothing_is_waiting_under_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.UntrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        Assert.Empty(await fixture.AllTrackingsAsync());

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola));

        var refused = Assert.Single(receipt!.Rejected);
        Assert.Equal(AutomaticProductMatchPolicy.NothingAwaitingThisKeyReason, refused.Reason);
        Assert.Empty(await fixture.AllSuggestionsAsync());
    }

    #endregion

    #region Everything else is a tentative match

    /// <summary>
    /// A proposal the backend cannot call obvious is kept for the Super Admin, the mappings stay
    /// awaiting a match, and the reason reported is the condition that refused it.
    /// </summary>
    [Fact]
    public async Task A_proposal_that_is_not_obvious_is_stored_as_a_tentative_match()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Proposal(WaitingKey, cola, ProductMatchConfidence.Medium));

        var tentative = Assert.Single(receipt!.Tentative);
        Assert.Equal(AutomaticProductMatchPolicy.ConfidenceBelowHighReason, tentative.Reason);
        Assert.Equal(cola, tentative.MasterProductId);

        var suggestion = Assert.Single(await fixture.AllSuggestionsAsync());
        Assert.Equal(ProductMatchSuggestionStatus.Tentative, suggestion.Status);
        Assert.Equal(cola, suggestion.MasterProductId);
        Assert.Equal(AutomaticProductMatchPolicy.MediumConfidenceProportion, suggestion.Confidence);
        Assert.Equal(suggestion.Id, tentative.SuggestionId);

        var mapping = Assert.Single(await fixture.AllTrackingsAsync());
        Assert.Equal(ProductPriceTrackingStatus.Requested, mapping.Status);
        Assert.Null(mapping.MasterProductId);
        Assert.Empty(await fixture.AllListingsAsync());
    }

    /// <summary>
    /// A suggested new master product is stored as the suggestion it is and <strong>never</strong>
    /// created: creating one is a person's decision, which is this issue's own exclusion.
    /// </summary>
    [Fact]
    public async Task A_suggested_new_master_product_is_stored_and_never_created()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            new ProductMatchProposal(
                WaitingKey,
                MasterProductId: null,
                "Coca-Cola",
                "Classic",
                375m,
                MasterProductSizeUnit.Millilitre,
                ProductMatchConfidence.High,
                MatchReason));

        var tentative = Assert.Single(receipt!.Tentative);
        Assert.Equal(AutomaticProductMatchPolicy.SuggestedNewMasterReason, tentative.Reason);
        Assert.Null(tentative.MasterProductId);

        Assert.Empty(await fixture.AllMasterProductsAsync());

        var suggestion = Assert.Single(await fixture.AllSuggestionsAsync());
        Assert.Null(suggestion.MasterProductId);
        Assert.Equal("Coca-Cola", suggestion.SuggestedBrand);
        Assert.Equal("Classic", suggestion.SuggestedName);
        Assert.Equal(375m, suggestion.SuggestedSizeValue);
        Assert.Equal(MasterProductSizeUnit.Millilitre, suggestion.SuggestedSizeUnit);
        Assert.Equal(ProductMatchSuggestionStatus.Tentative, suggestion.Status);

        Assert.Equal(
            ProductPriceTrackingStatus.Requested,
            Assert.Single(await fixture.AllTrackingsAsync()).Status);
    }

    /// <summary>
    /// Two master products that both pass every check are an ambiguity a person resolves: the
    /// proposal is kept as a tentative match and nothing is applied.
    /// </summary>
    [Fact]
    public async Task Two_suitable_master_products_apply_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic", 0.375m, MasterProductSizeUnit.Litre);

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola));

        var tentative = Assert.Single(receipt!.Tentative);
        Assert.Equal(
            AutomaticProductMatchPolicy.SeveralSuitableCandidatesReason,
            tentative.Reason);

        Assert.Equal(
            ProductPriceTrackingStatus.Requested,
            Assert.Single(await fixture.AllTrackingsAsync()).Status);
    }

    /// <summary>
    /// A pair that already has a suggestion gets no second row: a reviewer looking at one decision
    /// must not be handed the same evidence once per run. The existing row is reported instead - and
    /// it is stamped with the run that decided the key, because that row is the only record there is
    /// of what this run decided (see the retry regressions below). The matcher's own evidence, the
    /// reason and the confidence the row was first stored with, is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_pair_that_already_has_a_suggestion_is_not_suggested_twice()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.SeedMatchSuggestionAsync(
            WaitingKey,
            cola,
            ProductMatchSuggestionStatus.Tentative);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Proposal(WaitingKey, cola, ProductMatchConfidence.Low));

        var tentative = Assert.Single(receipt!.Tentative);
        Assert.Equal(AutomaticProductMatchPolicy.AlreadySuggestedReason, tentative.Reason);

        var suggestion = Assert.Single(await fixture.AllSuggestionsAsync());
        Assert.Equal(ProductMatchSuggestionPolicy.CollectorRunFor(runId), suggestion.CollectorRun);
        Assert.Equal(Now, suggestion.UpdatedAtUtc);
        Assert.Equal(suggestion.Id, tentative.SuggestionId);

        // Reused, not rewritten: the status, the pair and the evidence the row already held are the
        // reviewer's, and the stamp above is the run's own record of having decided the key.
        Assert.Equal(ProductMatchSuggestionStatus.Tentative, suggestion.Status);
        Assert.Equal(cola, suggestion.MasterProductId);
        Assert.Equal("Seeded by the fixture.", suggestion.Reason);
        Assert.Equal(0.9m, suggestion.Confidence);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, suggestion.CreatedAtUtc);
    }

    #endregion

    #region The refusals a Super Admin leaves behind

    /// <summary>
    /// The issue's last criterion: a pair a Super Admin rejected, or undid after it had been applied,
    /// is never matched automatically - and no fresh suggestion is written for it either, because the
    /// refusal is already recorded and is kept precisely so the pair is not proposed again.
    /// </summary>
    [Theory]
    [InlineData(ProductMatchSuggestionStatus.Rejected)]
    [InlineData(ProductMatchSuggestionStatus.Undone)]
    public async Task A_refused_pair_is_never_matched_automatically(
        ProductMatchSuggestionStatus refusal)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.SeedMatchSuggestionAsync(WaitingKey, cola, refusal);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola));

        var refused = Assert.Single(receipt!.Rejected);
        Assert.Equal(AutomaticProductMatchPolicy.PairRefusedReason, refused.Reason);

        // The refusal row, and no second row beside it.
        var suggestion = Assert.Single(await fixture.AllSuggestionsAsync());
        Assert.Equal(refusal, suggestion.Status);

        var mapping = Assert.Single(await fixture.AllTrackingsAsync());
        Assert.Equal(ProductPriceTrackingStatus.Requested, mapping.Status);
        Assert.Null(mapping.MasterProductId);
    }

    /// <summary>
    /// And a refusal of one master product leaves another one matchable: a refusal is about a pair,
    /// not about the key.
    /// </summary>
    [Fact]
    public async Task A_refusal_of_one_master_product_leaves_another_matchable()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var refusedCola = await fixture.SeedMasterProductAsync(
            "Coca-Cola",
            "Classic",
            0.375m,
            MasterProductSizeUnit.Litre);

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.SeedMatchSuggestionAsync(
            WaitingKey,
            refusedCola,
            ProductMatchSuggestionStatus.Rejected);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola));

        var applied = Assert.Single(receipt!.AutoMatched);
        Assert.Equal(cola, applied.MasterProductId);

        Assert.Equal(
            ProductPriceTrackingStatus.AutoMatched,
            Assert.Single(await fixture.AllTrackingsAsync()).Status);
    }

    #endregion

    #region What a proposal must name

    /// <summary>
    /// A key this run issued no work for is refused: the keys are the work list's, and a key nothing
    /// was asked about is not an answer to anything.
    /// </summary>
    [Fact]
    public async Task A_waiting_key_this_run_did_not_issue_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        var otherRunId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(otherRunId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola));

        var refused = Assert.Single(receipt!.Rejected);
        Assert.Equal(AutomaticProductMatchPolicy.UnknownWaitingKeyReason, refused.Reason);
        Assert.Empty(await fixture.AllSuggestionsAsync());
    }

    /// <summary>A master product this catalogue does not hold is refused, and nothing is stored.</summary>
    [Fact]
    public async Task An_unknown_master_product_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(fixture, runId, Proposal(WaitingKey, masterProductId: 9_999));

        var refused = Assert.Single(receipt!.Rejected);
        Assert.Equal(AutomaticProductMatchPolicy.UnknownMasterProductReason, refused.Reason);
        Assert.Empty(await fixture.AllSuggestionsAsync());
    }

    /// <summary>
    /// A proposal that names both a master product and a new description, or neither, is refused by
    /// the suggestion's own rule - and so is one with no reason at all.
    /// </summary>
    [Fact]
    public async Task A_proposal_that_is_not_exactly_one_complete_proposal_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            new ProductMatchProposal(
                WaitingKey,
                cola,
                "Coca-Cola",
                "Classic",
                375m,
                MasterProductSizeUnit.Millilitre,
                ProductMatchConfidence.High,
                MatchReason),
            new ProductMatchProposal(
                WaitingKey,
                MasterProductId: null,
                SuggestedBrand: null,
                SuggestedName: null,
                SuggestedSizeValue: null,
                SuggestedSizeUnit: null,
                ProductMatchConfidence.High,
                MatchReason),
            Proposal(WaitingKey, cola, reason: "   "),
            Proposal(WaitingKey, cola, confidence: (ProductMatchConfidence)9));

        Assert.Equal(4, receipt!.Rejected.Count);
        Assert.Equal(
            [
                ProductMatchSuggestionPolicy.OneProposalRequiredMessage,
                ProductMatchSuggestionPolicy.OneProposalRequiredMessage,
                ProductMatchSuggestionPolicy.ReasonRequiredMessage,
                AutomaticProductMatchPolicy.UnknownConfidenceMessage,
            ],
            receipt.Rejected.Select(rejected => rejected.Reason));

        Assert.Empty(await fixture.AllSuggestionsAsync());
        Assert.Equal(
            ProductPriceTrackingStatus.Requested,
            Assert.Single(await fixture.AllTrackingsAsync()).Status);
    }

    #endregion

    #region Idempotency, and the run's own state

    /// <summary>
    /// Re-submitting the same run and key writes nothing and is answered with the outcome that was
    /// recorded - not with a fresh decision against a catalogue that may have moved since.
    /// </summary>
    [Fact]
    public async Task Resubmitting_the_same_run_and_key_is_idempotent()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var first = await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola));
        var applied = Assert.Single(first!.AutoMatched);

        var second = await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola));
        var repeat = Assert.Single(second!.AutoMatched);

        Assert.Equal(AutomaticProductMatchPolicy.AlreadyDecidedInThisRunReason, repeat.Reason);
        Assert.Equal(cola, repeat.MasterProductId);
        Assert.Equal(applied.SuggestionId, repeat.SuggestionId);

        Assert.Single(await fixture.AllSuggestionsAsync());
        Assert.Equal(Now, Assert.Single(await fixture.AllTrackingsAsync()).UpdatedAtUtc);
    }

    /// <summary>
    /// A tentative proposal is idempotent on the same terms, and the second submission neither writes
    /// a second suggestion nor re-decides the key.
    /// </summary>
    [Fact]
    public async Task Resubmitting_a_tentative_proposal_is_idempotent()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var proposal = Proposal(WaitingKey, cola, ProductMatchConfidence.Low);

        var first = Assert.Single((await SubmitAsync(fixture, runId, proposal))!.Tentative);
        var second = Assert.Single((await SubmitAsync(fixture, runId, proposal))!.Tentative);

        Assert.Equal(AutomaticProductMatchPolicy.AlreadyDecidedInThisRunReason, second.Reason);
        Assert.Equal(first.SuggestionId, second.SuggestionId);
        Assert.Single(await fixture.AllSuggestionsAsync());
    }

    /// <summary>
    /// A proposal answered with a suggestion an <em>earlier</em> run left is just as idempotent: the
    /// reused row records this run's decision, so a second request of the same run and key is
    /// answered with the outcome that was recorded even though its confidence is now high enough to
    /// have been applied. Without that record the key would be decided afresh and the match applied,
    /// which is two different answers to one run and one key.
    /// </summary>
    [Fact]
    public async Task Retrying_a_reused_suggestion_at_a_higher_confidence_keeps_the_recorded_outcome()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.SeedWaitingListingAsync(WaitingKey, "SKU-COLA-30");
        await fixture.SeedMatchSuggestionAsync(
            WaitingKey,
            cola,
            ProductMatchSuggestionStatus.Tentative);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var first = Assert.Single(
            (await SubmitAsync(
                fixture,
                runId,
                Proposal(WaitingKey, cola, ProductMatchConfidence.Low)))!.Tentative);

        Assert.Equal(AutomaticProductMatchPolicy.AlreadySuggestedReason, first.Reason);

        // The same run and key again, this time at the confidence that would otherwise have applied
        // the match - the proposal every other condition of "obvious" agrees with.
        var receipt = await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola));

        Assert.Empty(receipt!.AutoMatched);
        var repeat = Assert.Single(receipt.Tentative);
        Assert.Equal(AutomaticProductMatchPolicy.AlreadyDecidedInThisRunReason, repeat.Reason);
        Assert.Equal(cola, repeat.MasterProductId);
        Assert.Equal(first.SuggestionId, repeat.SuggestionId);

        // Still one suggestion, still tentative, and nothing a match would have changed has moved.
        var suggestion = Assert.Single(await fixture.AllSuggestionsAsync());
        Assert.Equal(ProductMatchSuggestionStatus.Tentative, suggestion.Status);

        var mapping = Assert.Single(await fixture.AllTrackingsAsync());
        Assert.Equal(ProductPriceTrackingStatus.Requested, mapping.Status);
        Assert.Null(mapping.MasterProductId);
        Assert.Null(mapping.MatchSource);

        var listing = Assert.Single(await fixture.AllListingsAsync());
        Assert.Null(listing.MasterProductId);
        Assert.Equal(WaitingKey, listing.WaitingKey);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, listing.UpdatedAtUtc);
    }

    /// <summary>
    /// And the retry cannot change its mind about <em>which</em> master product the key is either: a
    /// second request of the same run naming another master product - the one that is in fact the
    /// single suitable candidate - is answered with the decision that was recorded, not with the
    /// match it would have applied had the key never been decided.
    /// </summary>
    [Fact]
    public async Task Retrying_a_reused_suggestion_with_another_master_product_keeps_the_recorded_outcome()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var pepsi = await fixture.SeedMasterProductAsync("Pepsi", "Max");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.SeedWaitingListingAsync(WaitingKey, "SKU-COLA-30");
        await fixture.SeedMatchSuggestionAsync(
            WaitingKey,
            pepsi,
            ProductMatchSuggestionStatus.Tentative);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        // Answered with the suggestion the earlier run left for that pair: the proposed master is
        // not the suitable one, so it is kept rather than applied.
        var first = Assert.Single((await SubmitAsync(fixture, runId, Proposal(WaitingKey, pepsi)))!.Tentative);
        Assert.Equal(AutomaticProductMatchPolicy.AlreadySuggestedReason, first.Reason);
        Assert.Equal(pepsi, first.MasterProductId);

        var receipt = await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola));

        Assert.Empty(receipt!.AutoMatched);
        var repeat = Assert.Single(receipt.Tentative);
        Assert.Equal(AutomaticProductMatchPolicy.AlreadyDecidedInThisRunReason, repeat.Reason);
        Assert.Equal(pepsi, repeat.MasterProductId);
        Assert.Equal(first.SuggestionId, repeat.SuggestionId);

        var suggestion = Assert.Single(await fixture.AllSuggestionsAsync());
        Assert.Equal(pepsi, suggestion.MasterProductId);
        Assert.Equal(ProductMatchSuggestionStatus.Tentative, suggestion.Status);

        var mapping = Assert.Single(await fixture.AllTrackingsAsync());
        Assert.Equal(ProductPriceTrackingStatus.Requested, mapping.Status);
        Assert.Null(mapping.MasterProductId);
        Assert.Null(mapping.MatchSource);

        var listing = Assert.Single(await fixture.AllListingsAsync());
        Assert.Null(listing.MasterProductId);
        Assert.Equal(WaitingKey, listing.WaitingKey);
    }

    /// <summary>
    /// A pair whose only suggestion has already been <em>decided</em> - applied by an earlier run,
    /// here - is not the one the reuse above is about: this run records its own tentative suggestion
    /// rather than restamping somebody else's decision as its own, the decided row is left exactly as
    /// it was, and the key is just as settled for the rest of the run.
    /// </summary>
    [Fact]
    public async Task A_pair_whose_suggestion_was_already_decided_gets_this_runs_own_suggestion()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.SeedMatchSuggestionAsync(
            WaitingKey,
            cola,
            ProductMatchSuggestionStatus.AutoApplied);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var first = Assert.Single(
            (await SubmitAsync(
                fixture,
                runId,
                Proposal(WaitingKey, cola, ProductMatchConfidence.Medium)))!.Tentative);

        Assert.Equal(AutomaticProductMatchPolicy.ConfidenceBelowHighReason, first.Reason);

        var suggestions = await fixture.AllSuggestionsAsync();
        Assert.Equal(2, suggestions.Count);

        var applied = Assert.Single(
            suggestions.Where(s => s.Status == ProductMatchSuggestionStatus.AutoApplied));
        Assert.Equal("fixture", applied.CollectorRun);
        Assert.Equal(PriceCatalogueFixture.CreatedAt, applied.UpdatedAtUtc);

        var recorded = Assert.Single(
            suggestions.Where(s => s.Status == ProductMatchSuggestionStatus.Tentative));
        Assert.Equal(ProductMatchSuggestionPolicy.CollectorRunFor(runId), recorded.CollectorRun);
        Assert.Equal(recorded.Id, first.SuggestionId);

        // And that row is this run's record of the key, so the retry is answered rather than applied.
        var repeat = Assert.Single((await SubmitAsync(fixture, runId, Proposal(WaitingKey, cola)))!.Tentative);
        Assert.Equal(AutomaticProductMatchPolicy.AlreadyDecidedInThisRunReason, repeat.Reason);
        Assert.Equal(2, (await fixture.AllSuggestionsAsync()).Count);

        var mapping = Assert.Single(await fixture.AllTrackingsAsync());
        Assert.Equal(ProductPriceTrackingStatus.Requested, mapping.Status);
        Assert.Null(mapping.MasterProductId);
    }

    /// <summary>
    /// Two proposals for one key inside a single batch have no first outcome to be answered with, so
    /// the later one is refused rather than letting ordering decide which was stored.
    /// </summary>
    [Fact]
    public async Task A_duplicate_key_inside_one_batch_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var pepsi = await fixture.SeedMasterProductAsync("Pepsi", "Max");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Proposal(WaitingKey, cola),
            Proposal(WaitingKey, pepsi));

        Assert.Single(receipt!.AutoMatched);
        Assert.Equal(
            AutomaticProductMatchPolicy.DuplicateProposalInBatchReason,
            Assert.Single(receipt.Rejected).Reason);

        Assert.Single(await fixture.AllSuggestionsAsync());
    }

    /// <summary>A run nothing issued is not a run whose proposals could be refused one by one.</summary>
    [Fact]
    public async Task An_unknown_run_answers_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        Assert.Null(await SubmitAsync(fixture, 9_999, Proposal(WaitingKey, masterProductId: 1)));
    }

    /// <summary>A finished run accepts no proposals, and an overdue one is expired on touch.</summary>
    [Fact]
    public async Task A_closed_or_overdue_run_accepts_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var overdueRunId = await fixture.SeedRunAsync(RunStarted, Now.AddHours(-1));
        await fixture.SeedWorkItemAsync(overdueRunId, masterProductId: null, WaitingKey);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => SubmitAsync(fixture, overdueRunId, Proposal(WaitingKey, cola)));

        // Expired, in its own committed transaction, and then refused again as the finished run it is.
        Assert.Equal(PriceCollectorRunStatus.Expired, await fixture.RunStatusAsync(overdueRunId));

        await Assert.ThrowsAsync<DomainConflictException>(
            () => SubmitAsync(fixture, overdueRunId, Proposal(WaitingKey, cola)));

        Assert.Empty(await fixture.AllSuggestionsAsync());
        Assert.Equal(
            ProductPriceTrackingStatus.Requested,
            Assert.Single(await fixture.AllTrackingsAsync()).Status);
    }

    #endregion

    #region The boundary

    /// <summary>
    /// The whole flow leaves everything either business owns exactly as it was, apart from the three
    /// mapping columns a match is allowed to write. A tentative proposal and a refused one write
    /// nothing tenant-owned at all, which is what this fingerprint states.
    /// </summary>
    [Fact]
    public async Task A_tentative_or_refused_proposal_writes_nothing_a_business_owns()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var cola = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var pepsi = await fixture.SeedMasterProductAsync("Pepsi", "Max");

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessPepsiId);

        var runId = await fixture.SeedRunAsync(RunStarted, Expires);
        await fixture.SeedWorkItemAsync(runId, masterProductId: null, WaitingKey);

        var before = await fixture.TenantOwnedFingerprintAsync();

        var receipt = await SubmitAsync(
            fixture,
            runId,
            Proposal(WaitingKey, cola, ProductMatchConfidence.Medium),
            Proposal(PriceCatalogueWaitingKey.For("Pepsi Max", "375mL"), pepsi));

        Assert.Single(receipt!.Tentative);
        Assert.Single(receipt.Rejected);
        Assert.Equal(before, await fixture.TenantOwnedFingerprintAsync());
    }

    #endregion

    private static ProductMatchProposal Proposal(
        string? waitingKey,
        long? masterProductId,
        ProductMatchConfidence confidence = ProductMatchConfidence.High,
        string? reason = MatchReason) =>
        new(
            waitingKey,
            masterProductId,
            SuggestedBrand: null,
            SuggestedName: null,
            SuggestedSizeValue: null,
            SuggestedSizeUnit: null,
            confidence,
            reason);

    private static Task<ProductMatchReceipt?> SubmitAsync(
        PriceCatalogueFixture fixture,
        long runId,
        params ProductMatchProposal[] proposals) =>
        fixture.DeniedMatchStore().SubmitAsync(
            new ProductMatchBatch(runId, proposals),
            Now,
            CancellationToken.None);
}
