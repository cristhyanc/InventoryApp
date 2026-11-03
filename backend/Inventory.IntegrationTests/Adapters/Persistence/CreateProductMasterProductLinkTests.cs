using Inventory.Application.PriceCatalogue;
using Inventory.Application.Products;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the link a product created from a master product suggestion gets
/// (issue #1051, parent #817): the create that writes one <c>Matched</c> mapping with the new
/// <c>BusinessSelection</c> source, the three conflicts that write nothing at all, and what the Super
/// Admin review of #928 then sees of such a match and undoes.
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
    /// The ninth criterion's see half, through the <strong>real review adapter</strong>: the pair a
    /// created product settled is listed in the queue's own business-selections section, described by
    /// the mapping's stored text and naming the master product it was matched to - and it is in that
    /// section alone, because it is neither awaiting a match nor an automatic match somebody applied.
    ///
    /// <para>And the decisive assertion of this surface, asked of the new section too: nothing about
    /// the business that selected it is in the answer. There is no field for a business id, a product
    /// id or even a count, and the adapter does not read the ownership column for this section at
    /// all.</para>
    /// </summary>
    [Fact]
    public async Task The_review_queue_lists_a_created_business_selection()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        var waitingKey = PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel);

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);
        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);
        var ours = Assert.Single(await fixture.AllMappingsAsync());

        var queue = await fixture.DeniedReviewStore()
            .ReadQueueAsync(ReviewedAt, CancellationToken.None);

        var listed = Assert.Single(queue.BusinessSelections);
        Assert.Equal(waitingKey, listed.WaitingKey);
        Assert.Equal(Prefill.ProductName, listed.SearchName);
        Assert.Equal(Prefill.UnitLabel, listed.UnitLabel);
        Assert.Equal(masterProductId, listed.MasterProduct.Id);
        Assert.Equal(Brand, listed.MasterProduct.Brand);
        Assert.Equal(Name, listed.MasterProduct.Name);
        Assert.Equal(ours.CreatedAtUtc, listed.FirstSelectedAtUtc);
        Assert.Equal(ours.UpdatedAtUtc, listed.LastSelectedAtUtc);

        // A settled selection is not waiting for a match and is not an automatic one, so it appears
        // in neither of those sections.
        Assert.Empty(queue.WaitingProducts);
        Assert.Empty(queue.AutomaticMatches);
    }

    /// <summary>
    /// The ninth criterion's undo half, through the real review adapter: a Super Admin naming the
    /// <c>(waiting key, master product)</c> pair returns the mapping to awaiting a match - the master
    /// product and the source cleared, the instant the decision's - and the pair stops being listed as
    /// one somebody has still to check.
    ///
    /// <para><strong>And nothing else moves.</strong> The product itself is untouched: its name, unit
    /// label, quantity, average unit cost, inventory value and machine price are what the create
    /// stored, because an undo decides what a product <em>is</em> in the shared catalogue and never
    /// anything a business owns. No master product, suggestion row or supplier listing is written
    /// either - a business's selection created none of them for an undo to put back.</para>
    /// </summary>
    [Fact]
    public async Task A_super_admin_undo_returns_a_created_selection_to_waiting()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        await fixture.SeedListingAsync(masterProductId, "zedco-375", fixture.SupplierPresetId);
        var waitingKey = PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel);

        var created = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);
        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);

        var ours = Assert.Single(await fixture.AllMappingsAsync());
        var sharedBefore = await Describe(fixture);
        var productsBefore = await DescribeProducts(fixture);

        var undone = await fixture.DeniedReviewStore().UndoBusinessSelectionAsync(
            waitingKey,
            masterProductId,
            ReviewedAt,
            CancellationToken.None);

        Assert.NotNull(undone);
        Assert.Equal(waitingKey, undone!.WaitingKey);
        Assert.Equal(masterProductId, undone.MasterProduct.Id);

        var returned = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(ours.Id, returned.Id);
        Assert.Equal(fixture.FirstBusinessId, returned.BusinessId);
        Assert.Equal(created.ProductId, returned.ProductId);
        Assert.Equal(ProductPriceTrackingStatus.Requested, returned.Status);
        Assert.Null(returned.MasterProductId);
        Assert.Null(returned.MatchSource);
        Assert.Equal(ReviewedAt, returned.UpdatedAtUtc);

        // The search text it is waiting under is untouched, so the collector searches for exactly
        // what the business recorded.
        Assert.Equal(ours.WaitingKey, returned.WaitingKey);
        Assert.Equal(ours.SearchName, returned.SearchName);
        Assert.Equal(ours.UnitLabel, returned.UnitLabel);

        Assert.Equal(sharedBefore, await Describe(fixture));
        Assert.Equal(productsBefore, await DescribeProducts(fixture));

        // It is a waiting product now and no longer a selection to check, which is the state a
        // reviewer can then match, refuse or record as not found on the same surface.
        var queue = await fixture.DeniedReviewStore()
            .ReadQueueAsync(ReviewedAt, CancellationToken.None);
        Assert.Empty(queue.BusinessSelections);
        Assert.Equal(waitingKey, Assert.Single(queue.WaitingProducts).WaitingKey);
    }

    /// <summary>
    /// An undo is decided for a pair and therefore reaches every business that carries it - which is
    /// the rule every decision on this surface follows - and reaches nothing else: another business's
    /// settled match of another key, decided by a Super Admin, is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task An_undo_returns_every_businesss_selection_of_the_pair_and_nothing_else()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var masterProductId = await fixture.SeedMasterProductAsync(Brand, Name);
        var otherMasterProductId = await fixture.SeedMasterProductAsync(Brand, "No Sugar");
        var waitingKey = PriceCatalogueWaitingKey.For(Prefill.ProductName, Prefill.UnitLabel);

        // Both businesses create their own product from the same catalogue description: two mappings,
        // one pair.
        var ourCreate = await fixture.CreateProductAsync(
            fixture.FirstBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel,
            masterProductId);
        var theirCreate = await fixture.CreateProductAsync(
            fixture.SecondBusinessId,
            Prefill.ProductName.ToUpperInvariant(),
            Prefill.UnitLabel,
            masterProductId);
        Assert.Equal(ProductWriteOutcome.Succeeded, ourCreate.Outcome);
        Assert.Equal(ProductWriteOutcome.Succeeded, theirCreate.Outcome);

        // And the second business's own cola, which folds to another key and was matched by a Super
        // Admin to another master product.
        var elsewhere = await fixture.TrackAsync(
            fixture.SecondBusinessId,
            fixture.SecondBusinessColaId);
        await fixture.MatchAsync(elsewhere.Id, otherMasterProductId);

        var undone = await fixture.DeniedReviewStore().UndoBusinessSelectionAsync(
            waitingKey,
            masterProductId,
            ReviewedAt,
            CancellationToken.None);

        Assert.NotNull(undone);

        var mappings = await fixture.AllMappingsAsync();

        foreach (var productId in new[] { ourCreate.ProductId, theirCreate.ProductId })
        {
            var returned = Assert.Single(mappings.Where(mapping => mapping.ProductId == productId));
            Assert.Equal(ProductPriceTrackingStatus.Requested, returned.Status);
            Assert.Null(returned.MasterProductId);
            Assert.Null(returned.MatchSource);
            Assert.Equal(ReviewedAt, returned.UpdatedAtUtc);
        }

        var untouched = Assert.Single(mappings.Where(mapping => mapping.Id == elsewhere.Id));
        Assert.Equal(ProductPriceTrackingStatus.Matched, untouched.Status);
        Assert.Equal(otherMasterProductId, untouched.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.SuperAdmin, untouched.MatchSource);
        Assert.Equal(elsewhere.UpdatedAtUtc, untouched.UpdatedAtUtc);
    }

    /// <summary>
    /// A pair that is not a settled business selection is no decision anybody can make: an unknown
    /// master product, a key nothing carries, a key submitted empty, the same key against another
    /// master product, a settled match another source decided, and the same pair a second time are
    /// each answered with nothing - which the endpoint reports as <c>404</c> - and each writes nothing
    /// at all.
    /// </summary>
    [Fact]
    public async Task An_undo_of_anything_but_a_settled_selection_writes_nothing()
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
        Assert.Equal(ProductWriteOutcome.Succeeded, created.Outcome);

        // Another business's mapping of the same key, settled by a Super Admin: not a selection, so
        // no undo of a selection may take it back.
        var theirProduct = await fixture.SeedProductAsync(
            fixture.SecondBusinessId,
            Prefill.ProductName,
            Prefill.UnitLabel);
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, theirProduct);
        await fixture.MatchAsync(theirs.Id, masterProductId);

        var before = await fixture.TenantOwnedFingerprintAsync();
        var sharedBefore = await Describe(fixture);

        Assert.Null(await Undo(fixture, waitingKey, masterProductId + 9_999));
        Assert.Null(await Undo(fixture, PriceCatalogueWaitingKey.For("Nothing", "1L"), masterProductId));
        Assert.Null(await Undo(fixture, "   ", masterProductId));
        Assert.Null(await Undo(fixture, waitingKey, otherMasterProductId));

        Assert.Equal(before, await fixture.TenantOwnedFingerprintAsync());
        Assert.Equal(sharedBefore, await Describe(fixture));

        // The real pair is undone once, and a second undo of it is nothing to do either - which is
        // what makes the decision safe to repeat from a stale page.
        Assert.NotNull(await Undo(fixture, waitingKey, masterProductId));
        Assert.Null(await Undo(fixture, waitingKey, masterProductId));

        // And the Super Admin's own settled match of the same pair stood through all of it.
        var untouched = Assert.Single(
            (await fixture.AllMappingsAsync()).Where(mapping => mapping.Id == theirs.Id));
        Assert.Equal(ProductPriceTrackingStatus.Matched, untouched.Status);
        Assert.Equal(masterProductId, untouched.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.SuperAdmin, untouched.MatchSource);
        Assert.Equal(theirs.UpdatedAtUtc, untouched.UpdatedAtUtc);
    }

    /// <summary>
    /// A Super Admin matching the same waiting key to another master product moves only the mapping
    /// still <em>awaiting</em> a match (<c>ProductMatchReviewPolicy.MayMatch</c>): a settled
    /// <c>BusinessSelection</c> mapping is left standing, untouched instant included, exactly like a
    /// <c>Matched</c> mapping any other source decided. Undoing one is the decision that does take it
    /// back, and it is asserted above.
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
    /// An undo of an <em>automatic</em> match under the same key returns the mapping that match
    /// applied to and leaves this one exactly as it is: the two undos reach disjoint sets
    /// (<c>ProductMatchReviewPolicy.ReturnsToWaiting</c> against
    /// <c>ReturnsToWaitingFromSelection</c>), so taking a matcher's guess back never takes a
    /// business's own selection with it.
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
    /// One undo of a business's own selection, through the real review adapter on the denied scope a
    /// platform-admin request carries - a new context each time, because every decision a reviewer
    /// makes is its own request.
    /// </summary>
    private static Task<BusinessSelectionUndoResult?> Undo(
        PriceCatalogueFixture fixture,
        string? waitingKey,
        long masterProductId) =>
        fixture.DeniedReviewStore().UndoBusinessSelectionAsync(
            waitingKey,
            masterProductId,
            ReviewedAt,
            CancellationToken.None);

    /// <summary>
    /// Every product either business owns, by the fields a review must never touch: the identity the
    /// operator typed and the quantity, cost and value the create recorded.
    /// </summary>
    private static async Task<string> DescribeProducts(PriceCatalogueFixture fixture) =>
        string.Join(
            "|",
            (await fixture.AllProductsAsync()).Select(product =>
                $"P{product.Id}/{product.BusinessId}/{product.Name}/{product.Unit}"
                    + $"/{product.QuantityInStock}/{product.AverageUnitCost}"
                    + $"/{product.InventoryValue}/{product.MachinePrice}/{product.UpdatedAt:O}"));

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
