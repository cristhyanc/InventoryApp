using Inventory.Application.PriceCatalogue;
using Inventory.Application.Products;
using Inventory.Domain.PriceCatalogue;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the one tenant-owned table of the shared price catalogue (issue #923, parent
/// #817): a business's own price-tracking mappings, and since issue #938 the switch that creates and
/// removes them.
///
/// <para>Two businesses over real SQLite and the real migration history, because what is under test
/// is the boundary itself - the ownership stamp, the central query filters, the per-business unique
/// index and the foreign keys. None of those exists in an InMemory provider, and none of them can be
/// stated with one business.</para>
/// </summary>
public class EfProductPriceTrackingStoreTests
{
    #region The stamp and the search text

    /// <summary>
    /// The criterion itself: a mapping created under a business scope is stamped with that business.
    /// The port takes no owner at all, so there is nothing for a caller to choose.
    /// </summary>
    [Fact]
    public async Task A_mapping_is_stamped_with_the_callers_own_business()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var created = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var stored = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(fixture.FirstBusinessId, stored.BusinessId);
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(fixture.FirstBusinessColaId, stored.ProductId);
    }

    /// <summary>
    /// The search text is captured from the product's own name and unit label, and the waiting key is
    /// the fold of what was captured - so the text a collector searches with and the key its answer
    /// is grouped under cannot disagree.
    ///
    /// <para>Since issue #938 the adapter reads both from the product row itself, inside the write, so
    /// there is nothing a caller could have captured instead: the fixture passes no name and no
    /// label.</para>
    /// </summary>
    [Fact]
    public async Task A_new_mapping_records_the_products_own_search_text_and_its_folded_key()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var created = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        Assert.Equal("Coca-Cola Classic", created.SearchName);
        Assert.Equal("375mL", created.UnitLabel);
        Assert.Equal(
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"),
            created.WaitingKey);

        // And a new mapping is a request and nothing more: deciding what the product is belongs to
        // the matcher (#941) and the review (#928).
        Assert.Equal(ProductPriceTrackingStatus.Requested, created.Status);
        Assert.Null(created.MasterProductId);
        Assert.Null(created.MatchSource);
    }

    /// <summary>
    /// A product with no unit label cannot be tracked at all (issue #938's second criterion). Until
    /// this issue there was no writer and the empty label was reachable; now the only writer refuses
    /// it, because a product that does not say what size it comes in cannot be looked for on a
    /// supplier's shelf. The empty-string spelling the column still documents is
    /// <c>ProductPriceTrackingPolicy.NormaliseUnitLabel</c>'s, unit-tested there.
    /// </summary>
    [Fact]
    public async Task A_product_with_no_unit_label_cannot_be_tracked()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var unlabelled = await fixture.SeedProductAsync(fixture.FirstBusinessId, "Shapes Barbecue", null);

        var write = await fixture.TryTrackAsync(fixture.FirstBusinessId, unlabelled);

        Assert.Equal(ProductPriceTrackingWriteOutcome.Refused, write.Outcome);
        Assert.Equal(ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage, write.Refusal);
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// And neither can a product carrying the generic default label, which is what every product the
    /// provider catalogue import creates has.
    /// </summary>
    [Fact]
    public async Task A_product_with_the_generic_unit_label_cannot_be_tracked()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var imported = await fixture.SeedProductAsync(fixture.FirstBusinessId, "Imported Bar", "unit");

        var write = await fixture.TryTrackAsync(fixture.FirstBusinessId, imported);

        Assert.Equal(ProductPriceTrackingWriteOutcome.Refused, write.Outcome);
        Assert.Equal(ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage, write.Refusal);
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// A caller with no usable membership writes nothing: the enforcer has no business to stamp, so
    /// the insert is refused rather than stored unowned. "No current business" is never "no filter".
    ///
    /// <para>Under a denied scope the product is not readable either, so the refusal arrives as
    /// "no such product" before an insert is attempted at all - which is the fail-closed direction,
    /// and is what the empty table below states.</para>
    /// </summary>
    [Fact]
    public async Task A_denied_scope_cannot_create_a_mapping()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var write = await fixture.DeniedTrackingStore().TrackAsync(
            fixture.FirstBusinessColaId,
            PriceCatalogueFixture.CreatedAt,
            CancellationToken.None);

        Assert.Equal(ProductPriceTrackingWriteOutcome.ProductNotFound, write.Outcome);
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    #endregion

    #region Isolation between two businesses

    /// <summary>
    /// The criterion: another business's mapping is unreachable, by listing and by id alike. The id
    /// is a real, existing row key - the point is that knowing it yields nothing.
    /// </summary>
    [Fact]
    public async Task Another_business_s_mapping_is_unreachable_by_listing_and_by_id()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var store = fixture.TrackingStoreFor(fixture.FirstBusinessId);

        var listed = await store.ListAsync(CancellationToken.None);
        Assert.Equal([mine.Id], listed.Select(mapping => mapping.Id));

        Assert.Null(await store.FindAsync(theirs.Id, CancellationToken.None));
        Assert.NotNull(await store.FindAsync(mine.Id, CancellationToken.None));
    }

    /// <summary>
    /// And a business cannot track another business's product: the product is read through the central
    /// query filters inside the write, so another business's product is simply not there - the same
    /// answer a product that does not exist gives, which is deliberate. The foreign key on
    /// <c>(BusinessId, ProductId)</c> remains the database's own backstop under that, declared by the
    /// migration and asserted by <c>AddSupplierPriceCatalogueMigrationTests</c>.
    /// </summary>
    [Fact]
    public async Task A_business_cannot_track_another_business_s_product()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var write = await fixture.TryTrackAsync(fixture.FirstBusinessId, fixture.SecondBusinessColaId);

        Assert.Equal(ProductPriceTrackingWriteOutcome.ProductNotFound, write.Outcome);
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// Nor read another business's product's tracking state by id, which is the read half of the same
    /// boundary: the answer is "no such product", not "that product is not tracked" and certainly not
    /// its status.
    /// </summary>
    [Fact]
    public async Task A_business_cannot_read_another_business_s_products_tracking_state()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var view = await fixture.TrackingStoreFor(fixture.FirstBusinessId)
            .FindForProductAsync(fixture.SecondBusinessColaId, CancellationToken.None);

        Assert.Null(view);
    }

    /// <summary>
    /// Two businesses tracking what folds to the same product is two mappings and no conflict: the
    /// uniqueness rule is per business, because a global one would let whichever business tracked a
    /// product first deny it to every other.
    /// </summary>
    [Fact]
    public async Task Two_businesses_may_each_track_their_own_copy_of_one_product()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var first = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var second = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        Assert.Equal(2, (await fixture.AllMappingsAsync()).Count);

        // And the two spellings fold to one key, which is what the collector work list deduplicates.
        Assert.Equal(first.WaitingKey, second.WaitingKey);
    }

    /// <summary>
    /// One mapping per product. The in-lock read is what turns the ordinary second attempt into a
    /// clear answer, and the per-business unique index is the backstop under it - which the test below
    /// states directly.
    /// </summary>
    [Fact]
    public async Task One_product_can_be_tracked_only_once()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var second = await fixture.TryTrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        Assert.Equal(ProductPriceTrackingWriteOutcome.AlreadyTracked, second.Outcome);
        Assert.Single(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// The index's own refusal, for a mapping written by a writer this process did not serialise with:
    /// the row is inserted directly, behind the adapter's back, and the adapter both fails and
    /// <em>recognises</em> the violation - so the use case answers "already tracked" rather than a
    /// server error, and there is still exactly one mapping.
    /// </summary>
    [Fact]
    public async Task The_unique_index_refuses_a_second_mapping_and_the_adapter_recognises_it()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.InsertMappingDirectlyAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var store = fixture.TrackingStoreFor(fixture.FirstBusinessId);

        // The in-lock read sees the row this time, so the adapter answers rather than inserting. The
        // recognition below is what covers the case it cannot see - another process's row.
        var write = await store.TrackAsync(
            fixture.FirstBusinessColaId, PriceCatalogueFixture.CreatedAt, CancellationToken.None);
        Assert.Equal(ProductPriceTrackingWriteOutcome.AlreadyTracked, write.Outcome);

        var violation = await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.InsertMappingDirectlyAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId));
        Assert.True(store.IsDuplicateMapping(violation));

        // And an unrelated failure is not translated into "already tracked".
        Assert.False(store.IsDuplicateMapping(new InvalidOperationException("something else")));

        Assert.Single(await fixture.AllMappingsAsync());
    }

    #endregion

    #region Turning tracking off

    /// <summary>
    /// The criterion: turning tracking off removes <em>only</em> this business's mapping. The shared
    /// master product it was matched to, the supplier listing under that master product and the other
    /// business's mapping of its own copy of the product are all still there afterwards.
    /// </summary>
    [Fact]
    public async Task Turning_tracking_off_removes_only_this_business_s_mapping()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var listing = await fixture.SeedListingAsync(master, "SKU-375");

        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mine.Id, master);
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        await fixture.MatchAsync(theirs.Id, master);

        var write = await fixture.UntrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        Assert.Equal(ProductPriceTrackingWriteOutcome.Removed, write.Outcome);
        Assert.Equal([theirs.Id], (await fixture.AllMappingsAsync()).Select(mapping => mapping.Id));
        Assert.True(await fixture.MasterProductExistsAsync(master));
        Assert.True(await fixture.ListingExistsAsync(listing));
    }

    /// <summary>
    /// A business cannot turn off another business's tracking by naming its product: the product is
    /// not there, so the answer is "no such product" and that business's mapping is untouched.
    /// </summary>
    [Fact]
    public async Task A_business_cannot_turn_off_another_business_s_tracking()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var write = await fixture.UntrackAsync(fixture.FirstBusinessId, fixture.SecondBusinessColaId);

        Assert.Equal(ProductPriceTrackingWriteOutcome.ProductNotFound, write.Outcome);
        Assert.Equal([theirs.Id], (await fixture.AllMappingsAsync()).Select(mapping => mapping.Id));
    }

    /// <summary>
    /// Turning off a product that is not tracked is a success with nothing to do, so a second click
    /// or a retried request needs no different answer. "No such product" stays a separate answer.
    /// </summary>
    [Fact]
    public async Task Turning_tracking_off_twice_is_the_same_answer_as_it_already_being_off()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        await fixture.UntrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var again = await fixture.UntrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        Assert.Equal(ProductPriceTrackingWriteOutcome.NotTracked, again.Outcome);
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// And turning it on again afterwards works, recapturing the product's search text: turning off
    /// removes the mapping rather than recording a refusal, so there is nothing left to block it.
    /// </summary>
    [Fact]
    public async Task Tracking_can_be_turned_on_again_after_it_was_turned_off()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.UntrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var again = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        Assert.Equal("Coca-Cola Classic", again.SearchName);
        Assert.Equal(ProductPriceTrackingStatus.Requested, again.Status);
        Assert.Single(await fixture.AllMappingsAsync());
    }

    #endregion

    #region The status read

    /// <summary>
    /// An untracked product of this business is a real answer rather than a missing one, which is what
    /// lets the switch render "off" instead of an error.
    /// </summary>
    [Fact]
    public async Task An_untracked_product_reads_as_having_no_mapping()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var view = await fixture.TrackingStoreFor(fixture.FirstBusinessId)
            .FindForProductAsync(fixture.FirstBusinessColaId, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal("Coca-Cola Classic", view.ProductName);
        Assert.Equal("375mL", view.ProductUnit);
        Assert.Null(view.Tracking);
        Assert.Null(view.MasterProduct);
    }

    /// <summary>
    /// The criterion: a matched mapping reads with the master product's name and size. Both of the
    /// matched states carry it, which is what the auto-matched status is shown with.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched, ProductPriceTrackingMatchSource.CollectorAutomaticMatch)]
    [InlineData(ProductPriceTrackingStatus.Matched, ProductPriceTrackingMatchSource.SuperAdmin)]
    public async Task A_matched_mapping_reads_with_the_master_products_name_and_size(
        ProductPriceTrackingStatus status,
        ProductPriceTrackingMatchSource source)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master, status, source);

        var view = await fixture.TrackingStoreFor(fixture.FirstBusinessId)
            .FindForProductAsync(fixture.FirstBusinessColaId, CancellationToken.None);

        Assert.Equal(status, view!.Tracking!.Status);
        Assert.Equal("Classic", view.MasterProduct!.Name);
        Assert.Equal(375m, view.MasterProduct.SizeValue);
        Assert.Equal(MasterProductSizeUnit.Millilitre, view.MasterProduct.SizeUnit);
    }

    /// <summary>
    /// And a mapping that is still waiting, or was looked for and not found, carries no master product
    /// at all - there is nothing it was matched to, so there is nothing to describe.
    /// </summary>
    [Fact]
    public async Task A_waiting_or_not_found_mapping_reads_with_no_master_product()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var waiting = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var notFound = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessPepsiId);
        await fixture.MarkNotFoundAsync(notFound.Id);

        var store = fixture.TrackingStoreFor(fixture.FirstBusinessId);

        var waitingView = await store.FindForProductAsync(waiting.ProductId, CancellationToken.None);
        Assert.Equal(ProductPriceTrackingStatus.Requested, waitingView!.Tracking!.Status);
        Assert.Null(waitingView.MasterProduct);

        var notFoundView = await store.FindForProductAsync(notFound.ProductId, CancellationToken.None);
        Assert.Equal(ProductPriceTrackingStatus.NotFound, notFoundView!.Tracking!.Status);
        Assert.Null(notFoundView.MasterProduct);
    }

    #endregion

    #region What a product edit does to the mapping

    /// <summary>
    /// The criterion: editing the name of a product whose mapping is still awaiting a match updates
    /// its search text and its waiting key, inside the product update's own transaction.
    /// </summary>
    [Fact]
    public async Task Renaming_a_waiting_product_refreshes_its_search_text_and_waiting_key()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var attempt = await fixture.UpdateProductAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coke Classic", "375mL");
        Assert.Equal(ProductWriteOutcome.Succeeded, attempt.Outcome);

        var mapping = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal("Coke Classic", mapping.SearchName);
        Assert.Equal("375mL", mapping.UnitLabel);
        Assert.Equal(PriceCatalogueWaitingKey.For("Coke Classic", "375mL"), mapping.WaitingKey);
        Assert.Equal(ProductPriceTrackingStatus.Requested, mapping.Status);
    }

    /// <summary>
    /// The criterion: changing the unit label of a matched product returns its mapping to awaiting a
    /// match with the new text, and the master product and the match source it named are cleared -
    /// because the size the match rests on may no longer agree. The master product itself is
    /// untouched, which the restricted reference is what guarantees.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched, ProductPriceTrackingMatchSource.CollectorAutomaticMatch)]
    [InlineData(ProductPriceTrackingStatus.Matched, ProductPriceTrackingMatchSource.SuperAdmin)]
    public async Task Relabelling_a_matched_product_returns_its_mapping_to_awaiting_a_match(
        ProductPriceTrackingStatus status,
        ProductPriceTrackingMatchSource source)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master, status, source);

        await fixture.UpdateProductAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "1.25L");

        var refreshed = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(ProductPriceTrackingStatus.Requested, refreshed.Status);
        Assert.Null(refreshed.MasterProductId);
        Assert.Null(refreshed.MatchSource);
        Assert.Equal("1.25L", refreshed.UnitLabel);
        Assert.Equal(PriceCatalogueWaitingKey.For("Coca-Cola Classic", "1.25L"), refreshed.WaitingKey);
        Assert.True(await fixture.MasterProductExistsAsync(master));
    }

    /// <summary>
    /// And the other half: a name-only edit of a matched product keeps the match, the master product
    /// and the text the match was decided from.
    /// </summary>
    [Fact]
    public async Task Renaming_a_matched_product_keeps_the_match()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master);

        await fixture.UpdateProductAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coke Classic", "375mL");

        var kept = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(ProductPriceTrackingStatus.Matched, kept.Status);
        Assert.Equal(master, kept.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.SuperAdmin, kept.MatchSource);
        Assert.Equal("Coca-Cola Classic", kept.SearchName);
    }

    /// <summary>
    /// The repair of issue #938's review finding, over the real migration history and the real
    /// <c>EfProductStore</c>: relabelling a <strong>matched</strong> product to the generic
    /// <c>"unit"</c> suspends its mapping and clears the match, so the master product it was matched
    /// to can no longer supply prices for a size the product no longer has. The product edit itself
    /// still succeeds, and the shared catalogue rows are all exactly as they were.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched, ProductPriceTrackingMatchSource.CollectorAutomaticMatch)]
    [InlineData(ProductPriceTrackingStatus.Matched, ProductPriceTrackingMatchSource.SuperAdmin)]
    public async Task Relabelling_a_matched_product_to_the_generic_label_suspends_it_and_clears_the_match(
        ProductPriceTrackingStatus status,
        ProductPriceTrackingMatchSource source)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var listing = await fixture.SeedListingAsync(master, "ALPHA-COLA-375");
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master, status, source);

        var attempt = await fixture.UpdateProductAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "unit");

        // The edit is accepted: what a product may be saved with is #895's rule, and an existing
        // mapping is not a reason to refuse an otherwise valid product edit.
        Assert.Equal(ProductWriteOutcome.Succeeded, attempt.Outcome);

        var suspended = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(ProductPriceTrackingStatus.AwaitingProductDetails, suspended.Status);
        Assert.Null(suspended.MasterProductId);
        Assert.Null(suspended.MatchSource);

        // The master product and its listing are untouched - the reference restricts, and nothing
        // here writes a platform table.
        Assert.True(await fixture.MasterProductExistsAsync(master));
        Assert.True(await fixture.ListingExistsAsync(listing));

        // And that master product is no longer eligible for this business at all: the tracked-master
        // read is what a price comparison is built from, and nothing points at it any more.
        Assert.Empty(
            await fixture.WorkListStoreFor(fixture.FirstBusinessId)
                .ListTrackedMasterProductsAsync(CancellationToken.None));

        // Nor is its previous size searched for: the work list is the Requested rows.
        Assert.Empty(
            await fixture.WorkListStoreFor(fixture.FirstBusinessId)
                .ListProductsAwaitingMatchAsync(CancellationToken.None));
    }

    /// <summary>
    /// And the other half: correcting the unit label to a real, <em>different</em> size captures the
    /// product's current name and label afresh and returns the mapping to awaiting a match - with no
    /// master product, because the one it had was invalidated rather than remembered.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched, ProductPriceTrackingMatchSource.CollectorAutomaticMatch)]
    [InlineData(ProductPriceTrackingStatus.Matched, ProductPriceTrackingMatchSource.SuperAdmin)]
    public async Task Correcting_the_unit_label_of_a_suspended_mapping_makes_it_wait_for_a_match_again(
        ProductPriceTrackingStatus status,
        ProductPriceTrackingMatchSource source)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master, status, source);
        await fixture.UpdateProductAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "unit");

        await fixture.UpdateProductAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "1.25L");

        var resumed = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(ProductPriceTrackingStatus.Requested, resumed.Status);
        Assert.Null(resumed.MasterProductId);
        Assert.Null(resumed.MatchSource);
        Assert.Equal("Coca-Cola Classic", resumed.SearchName);
        Assert.Equal("1.25L", resumed.UnitLabel);
        Assert.Equal(PriceCatalogueWaitingKey.For("Coca-Cola Classic", "1.25L"), resumed.WaitingKey);

        // The corrected text is what is searched for now, and the old master product is still not
        // tracked through by anybody.
        var awaiting = Assert.Single(
            await fixture.WorkListStoreFor(fixture.FirstBusinessId)
                .ListProductsAwaitingMatchAsync(CancellationToken.None));
        Assert.Equal("1.25L", awaiting.UnitLabel);
        Assert.Empty(
            await fixture.WorkListStoreFor(fixture.FirstBusinessId)
                .ListTrackedMasterProductsAsync(CancellationToken.None));
        Assert.True(await fixture.MasterProductExistsAsync(master));
    }

    /// <summary>
    /// A suspension reaches one business's own mapping and nothing else: the other business's mapping
    /// of the <em>same</em> master product stays matched and stays eligible. The refresh reads through
    /// the central query filters, so there is exactly one row it can see.
    /// </summary>
    [Fact]
    public async Task Suspending_one_business_s_mapping_leaves_the_other_s_match_eligible()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mine = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        await fixture.MatchAsync(mine.Id, master);
        await fixture.MatchAsync(theirs.Id, master);

        await fixture.UpdateProductAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coca-Cola Classic", "unit");

        var stored = await fixture.AllMappingsAsync();
        var suspended = Assert.Single(stored, mapping => mapping.Id == mine.Id);
        Assert.Equal(ProductPriceTrackingStatus.AwaitingProductDetails, suspended.Status);
        Assert.Null(suspended.MasterProductId);

        var untouched = Assert.Single(stored, mapping => mapping.Id == theirs.Id);
        Assert.Equal(ProductPriceTrackingStatus.Matched, untouched.Status);
        Assert.Equal(master, untouched.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.SuperAdmin, untouched.MatchSource);
        Assert.Equal("coca-cola  classic", untouched.SearchName);
        Assert.Equal("375 ML", untouched.UnitLabel);

        // Their match still supplies that master product to the collector's work list, which since
        // issue #924 reads across businesses under the denied scope a collector request carries; mine
        // no longer does, which the suspended mapping above already shows.
        Assert.Equal(
            [master],
            (await fixture.DeniedWorkListStore()
                .ListTrackedMasterProductsAsync(CancellationToken.None))
                .Select(tracked => tracked.Id));
    }

    /// <summary>
    /// Editing an <em>untracked</em> product writes no mapping at all: a product edit is not a place a
    /// business acquires a tracked product. This is the regression that keeps issue #938 from changing
    /// what <c>PUT api/products/{id}</c> does for every product that is not tracked.
    /// </summary>
    [Fact]
    public async Task Editing_an_untracked_product_creates_no_mapping()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var attempt = await fixture.UpdateProductAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coke Classic", "600mL");

        Assert.Equal(ProductWriteOutcome.Succeeded, attempt.Outcome);
        Assert.Empty(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// And one business's product edit never reaches another business's mapping, even for the product
    /// whose text folds to the same waiting key: the refresh reads through the central query filters,
    /// so there is exactly one mapping it can see.
    /// </summary>
    [Fact]
    public async Task A_product_edit_leaves_another_business_s_mapping_alone()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        await fixture.UpdateProductAsync(
            fixture.FirstBusinessId, fixture.FirstBusinessColaId, "Coke Classic", "600mL");

        var stored = await fixture.AllMappingsAsync();
        var untouched = Assert.Single(stored, mapping => mapping.Id == theirs.Id);
        Assert.Equal("coca-cola  classic", untouched.SearchName);
        Assert.Equal("375 ML", untouched.UnitLabel);
        Assert.Equal(theirs.WaitingKey, untouched.WaitingKey);
    }

    #endregion

    #region What the references do

    /// <summary>
    /// A master product businesses are tracking through cannot be deleted: the reference restricts,
    /// so a settled match cannot be turned back into a search by removing the thing it was matched to.
    /// </summary>
    [Fact]
    public async Task A_master_product_a_business_is_tracking_cannot_be_deleted()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var mapping = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.MatchAsync(mapping.Id, master);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.DeleteMasterProductAsync(master));

        Assert.Single(await fixture.AllMappingsAsync());
    }

    /// <summary>
    /// Deleting a product still works exactly as it did, and takes its mapping with it - which is the
    /// reason the mapping's product reference cascades rather than restricting. Issue #923's last
    /// criterion is that no existing product behaviour changes, and "a product can no longer be
    /// deleted once somebody asked for its price" would have been a change.
    /// </summary>
    [Fact]
    public async Task Deleting_a_product_takes_its_mapping_with_it_and_leaves_every_other_alone()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var kept = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessPepsiId);
        var theirs = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        await fixture.DeleteProductAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        Assert.Equal(
            [kept.Id, theirs.Id],
            (await fixture.AllMappingsAsync()).Select(mapping => mapping.Id).Order());
    }

    #endregion

    #region Ordering

    /// <summary>
    /// The list is ordered by the search text and then by the row key, so it does not depend on the
    /// database's collation for a product name and two mappings whose text matches cannot swap places
    /// between reads.
    /// </summary>
    [Fact]
    public async Task The_list_is_ordered_by_the_search_text()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessPepsiId);
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var listed = await fixture.TrackingStoreFor(fixture.FirstBusinessId)
            .ListAsync(CancellationToken.None);

        Assert.Equal(
            ["Coca-Cola Classic", "Pepsi Max"],
            listed.Select(mapping => mapping.SearchName));
    }

    #endregion
}
