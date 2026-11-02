using Inventory.Application.PriceCatalogue;
using Inventory.Application.Products;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the link a product created from a master product suggestion gets
/// (issue #1051, parent #817): the create that writes one <c>Matched</c> mapping with the new
/// <c>BusinessSelection</c> source, and the three conflicts that write nothing at all.
///
/// <para>Two businesses over real SQLite and the real migration history, because what is under test
/// is the boundary itself - the ownership stamp, the central query filters, the foreign keys and
/// above all <strong>one transaction</strong> around the product and its link. None of those exists
/// in an InMemory provider.</para>
///
/// <para>The master products are seeded directly: they are platform rows, no endpoint a business can
/// reach writes one, and arranging them is the only way to state "the catalogue said this" before a
/// create asserts it still does.</para>
/// </summary>
public class CreateProductMasterProductLinkTests
{
    private const string Brand = "Zedco";
    private const string Name = "Classic";

    /// <summary>The instant a Super Admin's decision is made, after everything the fixture seeds.</summary>
    private static readonly DateTime ReviewedAt = PriceCatalogueFixture.CreatedAt.AddHours(3);

    /// <summary>What #1049's formatter prefills a 375 ml Zedco Classic's product form with.</summary>
    private static readonly MasterProductPrefillText Prefill = MasterProductPrefill.For(
        Brand,
        Name,
        375m,
        MasterProductSizeUnit.Millilitre);

    #region A valid selection

    /// <summary>
    /// The seventh criterion: one product, and one mapping for it in the same write - matched, naming
    /// the master product, recording that the business selected it, and carrying the search text the
    /// #938 switch itself would have captured with the key that text folds to.
    /// </summary>
    [Fact]
    public async Task A_valid_selection_creates_the_product_and_one_matched_mapping()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);

        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);

        var mapping = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(fixture.FirstBusinessId, mapping.BusinessId);
        Assert.Equal(created.ProductId, mapping.ProductId);
        Assert.Equal(ProductPriceTrackingStatus.Matched, mapping.Status);
        Assert.Equal(masterProductId, mapping.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.BusinessSelection, mapping.MatchSource);
        Assert.Equal(Prefill.ProductName, mapping.SearchName);
        Assert.Equal(Prefill.UnitLabel, mapping.UnitLabel);
        Assert.Equal(
            PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel),
            mapping.WaitingKey);
        Assert.Equal(mapping.CreatedAtUtc, mapping.UpdatedAtUtc);
    }

    /// <summary>
    /// The documented normalisation, through the real write: whitespace and case apart, the operator
    /// submitted the catalogue's own description, so the link is made - and the search text stored is
    /// the <em>product's</em> own text, exactly as the switch captures it, with the waiting key still
    /// folding to the one the catalogue's spelling produces.
    /// </summary>
    [Fact]
    public async Task The_link_survives_whitespace_and_case_and_still_records_the_products_own_text()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            "  zedco   classic  ",
            " 375ML ",
            masterProductId);

        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);

        var product = Assert.Single(
            (await fixture.AllProductsAsync()).Where(row => row.Id == created.ProductId));
        var mapping = Assert.Single(await fixture.AllMappingsAsync());

        Assert.Equal(masterProductId, mapping.MasterProductId);
        Assert.Equal(product.Name, mapping.SearchName);
        Assert.Equal(product.Unit, mapping.UnitLabel);
        Assert.Equal(
            PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel),
            mapping.WaitingKey);
    }

    /// <summary>
    /// Nothing shared is written by a link: the master products, the match suggestions and the
    /// supplier listings are read and left byte for byte as they were, and no second mapping appears
    /// anywhere.
    /// </summary>
    [Fact]
    public async Task A_link_writes_nothing_but_its_own_mapping()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        await fixture.SeedListingAsync(masterProductId, "zedco-375", fixture.SupplierPresetId);
        await fixture.SeedMatchSuggestionAsync(
            PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel),
            masterProductId,
            ProductMatchSuggestionStatus.Tentative);

        var mastersBefore = await Describe(fixture);

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);

        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);
        Assert.Equal(mastersBefore, await Describe(fixture));
        Assert.Single(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// Two businesses may link their own new product to the same master product, which is the whole
    /// point of a shared catalogue - and each one's mapping is stamped with its own business.
    /// </summary>
    [Fact]
    public async Task Two_businesses_may_each_link_their_own_product_to_one_master_product()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);

        var first = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);
        var second = await fixture.CreateProductAsync(
            fixture.SecondBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);

        Assert.Equal(ProductWriteOutcome.Succeeded, first.Outcome);
        Assert.Equal(ProductWriteOutcome.Succeeded, second.Outcome);

        var mappings = await fixture.AllMappingsAsync();
        Assert.Equal(2, mappings.Count);
        Assert.Equal(
            [fixture.FirstBusinessId, fixture.SecondBusinessId],
            mappings.Select(mapping => mapping.BusinessId).Order().ToArray());
        Assert.All(mappings, mapping => Assert.Equal(masterProductId, mapping.MasterProductId));
    }

    #endregion

    #region No selection, and the operator's opt-out

    /// <summary>
    /// A create that carries no selection is the create it always was: a product, and no mapping. The
    /// mappings table is what states it, because that is the only row this issue could have added.
    /// </summary>
    [Fact]
    public async Task A_create_without_a_selection_writes_no_mapping()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync(Brand, Name);

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel);

        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// The third criterion: with supplier price tracking turned off the selection is ignored, because
    /// the link <em>is</em> the tracking row. The product is created unlinked and untracked.
    /// </summary>
    [Fact]
    public async Task Turning_tracking_off_ignores_the_selection()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId,
            trackSupplierPrices: false);

        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// And it is ignored <em>before</em> anything is read, so an opt-out is never refused for a
    /// selection that has stopped standing: a master product nothing names cannot refuse a product
    /// nobody asked to link.
    /// </summary>
    [Fact]
    public async Task Turning_tracking_off_is_not_refused_by_a_selection_that_no_longer_stands()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            selectedMasterProductId: 987654L,
            trackSupplierPrices: false);

        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    #endregion

    #region The three conflicts, each writing nothing

    /// <summary>
    /// The fourth criterion: a selection naming no master product at all - an id nothing has, or one
    /// deleted since the lookup - is <c>master_product_unavailable</c>, and <strong>nothing is
    /// created</strong>, the product included.
    /// </summary>
    [Fact]
    public async Task An_unknown_master_product_refuses_the_create_and_writes_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var productsBefore = (await fixture.AllProductsAsync()).Count;

        var attempt = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            selectedMasterProductId: 987654L);

        AssertConflict(MasterProductSelectionPolicy.UnavailableCode, attempt);
        Assert.Equal(productsBefore, (await fixture.AllProductsAsync()).Count);
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// The same answer for a master product that was there when the operator looked it up and has
    /// since been removed from the shared catalogue, which is what makes the read authoritative
    /// rather than a repeat of what the form was told.
    /// </summary>
    [Fact]
    public async Task A_master_product_deleted_after_the_lookup_refuses_the_create()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        await fixture.DeleteMasterProductAsync(masterProductId);

        var attempt = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);

        AssertConflict(MasterProductSelectionPolicy.UnavailableCode, attempt);
        Assert.Empty(await fixture.AllProductsNamed(Prefill.ProductName));
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// The fifth criterion, in both halves: an edited Name and an edited unit label are both
    /// <c>master_product_changed</c>, and neither creates anything. A product the operator renamed is
    /// a different product, and a size they retyped is a different size.
    /// </summary>
    [Theory]
    [InlineData("Zedco Classic Twin Pack", "375ml")]
    [InlineData("Zedco Diet", "375ml")]
    [InlineData("Zedco Classic", "1.25l")]
    [InlineData("Zedco Classic", "375 ml")]
    public async Task An_edited_name_or_unit_label_refuses_the_create(string name, string unit)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);

        var attempt = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            name,
            unit,
            masterProductId);

        AssertConflict(MasterProductSelectionPolicy.ChangedCode, attempt);
        Assert.Empty(await fixture.AllProductsNamed(name));
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// The master product edited between the lookup and the create: the submitted values were the
    /// prefill of the row as it <em>was</em>, and the comparison is against the row as it is. This is
    /// the case the in-transaction read exists for.
    /// </summary>
    [Fact]
    public async Task A_master_product_edited_after_the_lookup_refuses_the_create()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        await fixture.RenameMasterProductAsync(masterProductId, "No Sugar");

        var attempt = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);

        AssertConflict(MasterProductSelectionPolicy.ChangedCode, attempt);
        Assert.Empty(await fixture.AllProductsNamed(Prefill.ProductName));
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// The sixth criterion: a <c>(waiting key, master product)</c> pair a Super Admin refused, or
    /// undid after it had been applied, is never matched again - and a business picking the product by
    /// hand is not a way around that. Both refusing statuses, because both are kept for exactly this
    /// reason.
    /// </summary>
    [Theory]
    [InlineData(ProductMatchSuggestionStatus.Rejected)]
    [InlineData(ProductMatchSuggestionStatus.Undone)]
    public async Task A_refused_pair_refuses_the_create(ProductMatchSuggestionStatus status)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        await fixture.SeedMatchSuggestionAsync(
            PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel),
            masterProductId,
            status);

        var attempt = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);

        AssertConflict(MasterProductSelectionPolicy.RefusedCode, attempt);
        Assert.Empty(await fixture.AllProductsNamed(Prefill.ProductName));
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// And no other suggestion status refuses anything: a proposal still waiting for a reviewer, one
    /// a person accepted, one a run applied and the record of a repeated proposal are all about a
    /// pair nobody has ruled out.
    /// </summary>
    [Theory]
    [InlineData(ProductMatchSuggestionStatus.Tentative)]
    [InlineData(ProductMatchSuggestionStatus.AutoApplied)]
    [InlineData(ProductMatchSuggestionStatus.Accepted)]
    [InlineData(ProductMatchSuggestionStatus.Repeated)]
    public async Task Only_a_refused_pair_refuses_the_create(ProductMatchSuggestionStatus status)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        await fixture.SeedMatchSuggestionAsync(
            PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel),
            masterProductId,
            status);

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);

        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);
        Assert.Equal(masterProductId, Assert.Single(await fixture.AllMappingsAsync()).MasterProductId);
    }

    /// <summary>
    /// A refusal is of one pair and not of a key or a product: another master product refused under
    /// the same waiting key leaves this selection alone, and the same master product refused under
    /// another key does too.
    /// </summary>
    [Fact]
    public async Task A_refusal_of_another_pair_refuses_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        var otherMasterProductId = await fixture.SeedMasterProductAsync(Brand, "No Sugar");
        var waitingKey = PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel);

        await fixture.SeedMatchSuggestionAsync(
            waitingKey,
            otherMasterProductId,
            ProductMatchSuggestionStatus.Rejected);
        await fixture.SeedMatchSuggestionAsync(
            PriceCatalogueWaitingKey.For("Zedco Diet", "375ml"),
            masterProductId,
            ProductMatchSuggestionStatus.Rejected);

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);

        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);
        Assert.Equal(masterProductId, Assert.Single(await fixture.AllMappingsAsync()).MasterProductId);
    }

    #endregion

    #region One transaction

    /// <summary>
    /// The seventh criterion's second half: a failure of the mapping insert takes the product with
    /// it. The database refuses every insert into the mappings table, so the product is written, its
    /// link fails, and the create leaves <strong>nothing</strong> behind - because a product the
    /// operator asked to be linked and tracked is not a product that may survive its link failing.
    /// </summary>
    [Fact]
    public async Task A_failed_mapping_insert_rolls_the_product_back()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        var productsBefore = (await fixture.AllProductsAsync()).Count;
        await fixture.RefuseMappingInsertsAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId));

        Assert.Equal(productsBefore, (await fixture.AllProductsAsync()).Count);
        Assert.Empty(await fixture.AllProductsNamed(Prefill.ProductName));
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    #endregion

    #region The other business

    /// <summary>
    /// Another business's mapping of the same master product is untouched by this business's create,
    /// in both directions: a successful link leaves it exactly as it was, and so does a refused one.
    /// The central query filters are what make it unreachable rather than anything written here.
    /// </summary>
    [Fact]
    public async Task Another_businesss_mapping_is_untouched()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);

        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        await fixture.MatchAsync(theirs.Id, masterProductId);
        var before = await fixture.TenantOwnedFingerprintAsync();

        AssertConflict(
            MasterProductSelectionPolicy.ChangedCode,
            await fixture.CreateProductAsync(
                fixture.FirstBusinessId,
                "Zedco Diet",
                "375ml",
                masterProductId));

        Assert.Equal(before, await fixture.TenantOwnedFingerprintAsync());

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);

        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);

        var mappings = await fixture.AllMappingsAsync();
        var untouched = Assert.Single(mappings.Where(mapping => mapping.Id == theirs.Id));
        Assert.Equal(fixture.SecondBusinessId, untouched.BusinessId);
        Assert.Equal(masterProductId, untouched.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.SuperAdmin, untouched.MatchSource);
        Assert.Equal(theirs.UpdatedAtUtc, untouched.UpdatedAtUtc);
    }

    #endregion

    #region What the Super Admin review makes of it

    /// <summary>
    /// The eighth criterion, first half: the review treats a <c>BusinessSelection</c> mapping as the
    /// settled match it is, exactly like a <c>Matched</c> mapping any other source decided. A Super
    /// Admin matching the same waiting key to another master product moves only the mapping still
    /// <em>awaiting</em> a match (<c>ProductMatchReviewPolicy.MayMatch</c>); this business's own
    /// decision is left standing, untouched instant included.
    /// </summary>
    [Fact]
    public async Task A_review_match_of_the_same_key_leaves_a_business_selection_settled()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        var otherMasterProductId = await fixture.SeedMasterProductAsync(Brand, "No Sugar");
        var waitingKey = PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel);

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);
        var ours = Assert.Single(await fixture.AllMappingsAsync());

        // Another business's product that folds to the same key and is still awaiting a match.
        var theirProduct = await fixture.SeedProductAsync(
            fixture.SecondBusinessId,
            "  ZEDCO   classic ",
            "375ML");
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, theirProduct);
        Assert.Equal(waitingKey, theirs.WaitingKey);

        var decided = await fixture.DeniedReviewStore().DecideMatchAsync(
            new ProductMatchReviewSubmission(
                waitingKey,
                ProductMatchReviewDecision.ChooseMasterProduct,
                SuggestionId: null,
                otherMasterProductId,
                NewBrand: null,
                NewName: null,
                NewSizeValue: null,
                NewSizeUnit: null),
            ReviewedAt,
            CancellationToken.None);

        Assert.Equal(otherMasterProductId, decided!.MasterProduct!.Id);

        var mappings = await fixture.AllMappingsAsync();

        var settled = Assert.Single(mappings.Where(mapping => mapping.Id == ours.Id));
        Assert.Equal(created.ProductId, settled.ProductId);
        Assert.Equal(ProductPriceTrackingStatus.Matched, settled.Status);
        Assert.Equal(masterProductId, settled.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.BusinessSelection, settled.MatchSource);
        Assert.Equal(ours.UpdatedAtUtc, settled.UpdatedAtUtc);

        var moved = Assert.Single(mappings.Where(mapping => mapping.Id == theirs.Id));
        Assert.Equal(ProductPriceTrackingStatus.Matched, moved.Status);
        Assert.Equal(otherMasterProductId, moved.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.SuperAdmin, moved.MatchSource);
    }

    /// <summary>
    /// The eighth criterion, second half: an undo of an <em>automatic</em> match under the same key
    /// returns the mapping that match applied to and leaves this one exactly as it is - because an
    /// undo of a matcher's guess must not undo a decision a person made
    /// (<c>ProductMatchReviewPolicy.ReturnsToWaiting</c>), and a business creating a product from the
    /// catalogue's own description of it is such a decision.
    /// </summary>
    [Fact]
    public async Task An_undo_of_an_automatic_match_leaves_a_business_selection_alone()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        var waitingKey = PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel);

        await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);
        var ours = Assert.Single(await fixture.AllMappingsAsync());

        var theirProduct = await fixture.SeedProductAsync(
            fixture.SecondBusinessId,
            "Zedco Classic",
            "375ml");
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, theirProduct);
        await fixture.MatchAsync(
            theirs.Id,
            masterProductId,
            ProductPriceTrackingStatus.AutoMatched,
            ProductPriceTrackingMatchSource.CollectorAutomaticMatch);
        var suggestionId = await fixture.SeedSuggestionAsync(
            waitingKey,
            masterProductId,
            ProductMatchSuggestionStatus.AutoApplied);

        _ = await fixture.DeniedReviewStore().DecideAutomaticMatchAsync(
            suggestionId,
            AutomaticMatchReviewDecision.Undo,
            ReviewedAt,
            CancellationToken.None);

        var mappings = await fixture.AllMappingsAsync();

        var settled = Assert.Single(mappings.Where(mapping => mapping.Id == ours.Id));
        Assert.Equal(ProductPriceTrackingStatus.Matched, settled.Status);
        Assert.Equal(masterProductId, settled.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.BusinessSelection, settled.MatchSource);
        Assert.Equal(ours.UpdatedAtUtc, settled.UpdatedAtUtc);

        var returned = Assert.Single(mappings.Where(mapping => mapping.Id == theirs.Id));
        Assert.Equal(ProductPriceTrackingStatus.Requested, returned.Status);
        Assert.Null(returned.MasterProductId);
        Assert.Null(returned.MatchSource);
    }

    #endregion

    private static void AssertConflict(string code, ProductWriteAttempt attempt)
    {
        Assert.Equal(ProductWriteOutcome.MasterProductConflict, attempt.Outcome);
        Assert.Equal(code, attempt.ConflictCode);
        Assert.False(string.IsNullOrWhiteSpace(attempt.Refusal));
        Assert.Null(attempt.ProductId);
    }

    /// <summary>
    /// Every master product, suggestion and supplier listing as they stand, so a test can state that
    /// a link wrote none of them.
    /// </summary>
    private static async Task<string> Describe(PriceCatalogueFixture fixture)
    {
        var masters = await fixture.AllMasterProductsAsync();
        var suggestions = await fixture.AllSuggestionsAsync();
        var listings = await fixture.AllListingsAsync();

        return string.Join(
            "|",
            masters.Select(master =>
                $"M{master.Id}/{master.Brand}/{master.Name}/{master.NormalisedKey}/{master.UpdatedAtUtc:O}")
                .Concat(suggestions.Select(suggestion =>
                    $"S{suggestion.Id}/{suggestion.WaitingKey}/{suggestion.MasterProductId}"
                        + $"/{suggestion.Status}/{suggestion.UpdatedAtUtc:O}"))
                .Concat(listings.Select(listing =>
                    $"L{listing.Id}/{listing.MasterProductId}/{listing.WaitingKey}"
                        + $"/{listing.MatchStatus}/{listing.UpdatedAtUtc:O}")));
    }
}
