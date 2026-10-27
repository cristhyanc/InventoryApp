using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the one tenant-owned table of the shared price catalogue (issue #923, parent
/// #817): a business's own price-tracking mappings.
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

        var created = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");

        var stored = Assert.Single(await fixture.AllMappingsAsync());
        Assert.Equal(fixture.FirstBusinessId, stored.BusinessId);
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(fixture.FirstBusinessColaId, stored.ProductId);
    }

    /// <summary>
    /// The search text is captured from the product's own name and unit label, and the waiting key is
    /// the fold of what was captured - so the text a collector searches with and the key its answer
    /// is grouped under cannot disagree.
    /// </summary>
    [Fact]
    public async Task A_new_mapping_records_the_products_own_search_text_and_its_folded_key()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var created = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "  Coca-Cola Classic ",
            "375mL");

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
    /// A product with no unit label records the empty string, so "no unit label" has exactly one
    /// stored spelling and one product cannot become two searches.
    /// </summary>
    [Fact]
    public async Task A_product_with_no_unit_label_records_an_empty_one()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var unlabelled = await fixture.SeedProductAsync(fixture.FirstBusinessId, "Shapes Barbecue", null);

        var created = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            unlabelled,
            "Shapes Barbecue",
            null);

        Assert.Equal(string.Empty, created.UnitLabel);
        Assert.Equal(PriceCatalogueWaitingKey.For("Shapes Barbecue", null), created.WaitingKey);
    }

    /// <summary>
    /// A caller with no usable membership writes nothing: the enforcer has no business to stamp, so
    /// the insert is refused rather than stored unowned. "No current business" is never "no filter".
    /// </summary>
    [Fact]
    public async Task A_denied_scope_cannot_create_a_mapping()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.DeniedTrackingStore().AddAsync(
                NewProductPriceTracking.For(
                    fixture.FirstBusinessColaId,
                    "Coca-Cola Classic",
                    "375mL",
                    PriceCatalogueFixture.CreatedAt),
                CancellationToken.None));

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

        var theirs = await fixture.TrackAsync(
            fixture.SecondBusinessId,
            fixture.SecondBusinessColaId,
            "coca-cola  classic",
            "375 ML");
        var mine = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");

        var store = fixture.TrackingStoreFor(fixture.FirstBusinessId);

        var listed = await store.ListAsync(CancellationToken.None);
        Assert.Equal([mine.Id], listed.Select(mapping => mapping.Id));

        Assert.Null(await store.FindAsync(theirs.Id, CancellationToken.None));
        Assert.NotNull(await store.FindAsync(mine.Id, CancellationToken.None));
    }

    /// <summary>
    /// And a business cannot track another business's product: the mapping's foreign key is on
    /// <c>(BusinessId, ProductId)</c>, and the business half of it is the stamp rather than anything
    /// the caller passed - so the pair names a row that does not exist and the database refuses it.
    /// </summary>
    [Fact]
    public async Task A_business_cannot_track_another_business_s_product()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.TrackAsync(
                fixture.FirstBusinessId,
                fixture.SecondBusinessColaId,
                "coca-cola  classic",
                "375 ML"));

        Assert.Empty(await fixture.AllMappingsAsync());
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

        var first = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
        var second = await fixture.TrackAsync(
            fixture.SecondBusinessId,
            fixture.SecondBusinessColaId,
            "coca-cola  classic",
            "375 ML");

        Assert.Equal(2, (await fixture.AllMappingsAsync()).Count);

        // And the two spellings fold to one key, which is what the collector work list deduplicates.
        Assert.Equal(first.WaitingKey, second.WaitingKey);
    }

    /// <summary>One mapping per product, which the per-business unique index decides.</summary>
    [Fact]
    public async Task One_product_can_be_tracked_only_once()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.TrackAsync(
                fixture.FirstBusinessId,
                fixture.FirstBusinessColaId,
                "Coca-Cola Classic",
                "375mL"));

        Assert.Single(await fixture.AllMappingsAsync());
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
        var mapping = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
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
        await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");
        var kept = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessPepsiId,
            "Pepsi Max",
            "375mL");
        var theirs = await fixture.TrackAsync(
            fixture.SecondBusinessId,
            fixture.SecondBusinessColaId,
            "coca-cola  classic",
            "375 ML");

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
        await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessPepsiId,
            "Pepsi Max",
            "375mL");
        await fixture.TrackAsync(
            fixture.FirstBusinessId,
            fixture.FirstBusinessColaId,
            "Coca-Cola Classic",
            "375mL");

        var listed = await fixture.TrackingStoreFor(fixture.FirstBusinessId)
            .ListAsync(CancellationToken.None);

        Assert.Equal(
            ["Coca-Cola Classic", "Pepsi Max"],
            listed.Select(mapping => mapping.SearchName));
    }

    #endregion
}
