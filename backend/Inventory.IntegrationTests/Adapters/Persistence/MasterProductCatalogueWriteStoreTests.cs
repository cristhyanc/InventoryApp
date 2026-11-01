using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The Super Admin's create and edit of a master product (issue #1024, parent #817) over the real
/// adapter, the real migration history and <strong>two</strong> businesses, on the
/// <strong>denied</strong> business scope a platform-admin request carries.
///
/// <para>Relational SQLite over the real migrations rather than InMemory, because every property
/// under test is a property of the database: the unique index over the normalised key, the state read
/// inside one <c>BEGIN IMMEDIATE</c> transaction, the foreign keys an edit must leave pointing where
/// they pointed, and the ownership enforcement a platform write passes without a lease.</para>
///
/// <para>Two businesses throughout, as AGENTS.md § Tenant ownership requires of any change that
/// touches a cross-business surface: both track the product being edited here, so "a catalogue write
/// changes the catalogue and nothing a business owns" is asserted against a database where there
/// <em>is</em> tenant data to change.</para>
/// </summary>
public class MasterProductCatalogueWriteStoreTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

    #region Creating one

    /// <summary>
    /// The first criterion: a create stores the description and the key
    /// <c>MasterProductPolicy</c> computed, both instants from the server's clock, and no supplier
    /// listing at all.
    /// </summary>
    [Fact]
    public async Task A_create_stores_the_description_its_key_and_both_instants()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var created = await fixture.DeniedMasterProductWriteStore().CreateAsync(
            Description("Coca-Cola", "Classic", 375m),
            Now,
            CancellationToken.None);

        Assert.Equal("Coca-Cola", created.Brand);
        Assert.Equal("Classic", created.Name);
        Assert.Equal(375m, created.SizeValue);
        Assert.Equal(MasterProductSizeUnit.Millilitre, created.SizeUnit);
        Assert.Equal(Now, created.CreatedAtUtc);
        Assert.Equal(Now, created.UpdatedAtUtc);
        Assert.Equal(0, created.ListingCounts.Total);

        var stored = await fixture.MasterProductAsync(created.Id);
        Assert.Equal(
            MasterProductPolicy.NormalisedKey("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre),
            stored!.NormalisedKey);
    }

    /// <summary>
    /// The duplicate rule, which is the shared catalogue's reason for existing: one description is
    /// one master product, so a second create of it is the review's own documented conflict and
    /// leaves one row.
    /// </summary>
    [Fact]
    public async Task A_create_of_a_description_another_product_already_carries_is_a_conflict()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.DeniedMasterProductWriteStore().CreateAsync(
                Description("Coca-Cola", "Classic", 375m),
                Now,
                CancellationToken.None));

        Assert.Equal(ProductMatchReviewPolicy.DuplicateMasterProductMessage, refusal.Message);
        Assert.Single(await fixture.AllMasterProductsAsync());
    }

    /// <summary>
    /// The key is what decides a duplicate, not the spelling: the same product typed in another case
    /// or with extra spacing is the same row, which is the whole of why the key is a stored column
    /// under a unique index rather than a comparison a query makes.
    /// </summary>
    [Theory]
    [InlineData("coca-cola", "classic")]
    [InlineData("COCA-COLA", "CLASSIC")]
    [InlineData("Coca-Cola", "  Classic  ")]
    public async Task A_create_folding_to_an_existing_key_is_a_conflict(string brand, string name)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.DeniedMasterProductWriteStore().CreateAsync(
                Description(brand, name, 375m),
                Now,
                CancellationToken.None));

        Assert.Single(await fixture.AllMasterProductsAsync());
    }

    /// <summary>
    /// A different size is a different product, so the same brand and name at another size is a
    /// second row rather than a conflict - which is what keeps 375 ml and 600 ml comparable
    /// separately.
    /// </summary>
    [Fact]
    public async Task A_create_at_another_size_is_another_product()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic", 375m);

        await fixture.DeniedMasterProductWriteStore().CreateAsync(
            Description("Coca-Cola", "Classic", 600m),
            Now,
            CancellationToken.None);

        Assert.Equal(2, (await fixture.AllMasterProductsAsync()).Count);
    }

    /// <summary>
    /// The index's own answer is reported as the pre-check's: a row inserted behind the adapter's
    /// back - a writer its in-lock read could not have seen - makes the save violate the unique
    /// index, and the caller is told the documented conflict rather than a database failure.
    ///
    /// <para>Arranged by creating the colliding row through this same store, which is the one thing a
    /// test can do that proves the translation rather than the pre-check: the second create's own
    /// pre-check sees it too, so the assertion here is that <em>both</em> guards answer with one
    /// sentence and that no SQLite text reaches it.</para>
    /// </summary>
    [Fact]
    public async Task A_duplicate_is_reported_as_the_documented_conflict_and_not_as_a_database_error()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        await fixture.DeniedMasterProductWriteStore().CreateAsync(
            Description("Coca-Cola", "Classic", 375m),
            Now,
            CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.DeniedMasterProductWriteStore().CreateAsync(
                Description("Coca-Cola", "Classic", 375m),
                Now,
                CancellationToken.None));

        Assert.Equal(ProductMatchReviewPolicy.DuplicateMasterProductMessage, refusal.Message);
        Assert.DoesNotContain("SQLite", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UNIQUE", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NormalisedKey", refusal.Message, StringComparison.Ordinal);
    }

    #endregion

    #region Editing one

    /// <summary>
    /// The second criterion: an edit stores the corrected description, recomputes the key, and stamps
    /// <c>UpdatedAtUtc</c> while leaving <c>CreatedAtUtc</c> alone - the row was corrected, not
    /// created again.
    /// </summary>
    [Fact]
    public async Task An_edit_stores_the_correction_and_recomputes_the_key_and_instant()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var id = await fixture.SeedMasterProductAsync("Coke", "Clasic");
        var before = await fixture.MasterProductAsync(id);

        var updated = await fixture.DeniedMasterProductWriteStore().UpdateAsync(
            id,
            Edit("Coca-Cola", "Classic", 375m, before!.UpdatedAtUtc),
            Now,
            CancellationToken.None);

        Assert.Equal(id, updated!.Id);
        Assert.Equal("Coca-Cola", updated.Brand);
        Assert.Equal("Classic", updated.Name);
        Assert.Equal(Now, updated.UpdatedAtUtc);
        Assert.Equal(before.CreatedAtUtc, updated.CreatedAtUtc);

        var stored = await fixture.MasterProductAsync(id);
        Assert.Equal(
            MasterProductPolicy.NormalisedKey("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre),
            stored!.NormalisedKey);
    }

    /// <summary>An id no master product has is nothing, which the endpoint reports as <c>404</c>.</summary>
    [Fact]
    public async Task An_edit_of_an_unknown_product_is_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        Assert.Null(await fixture.DeniedMasterProductWriteStore().UpdateAsync(
            424242,
            Edit("Coca-Cola", "Classic", 375m, Now),
            Now,
            CancellationToken.None));

        Assert.Empty(await fixture.AllMasterProductsAsync());
    }

    /// <summary>
    /// An edit to a description another master product already carries is the same conflict a create
    /// is: the catalogue cannot hold two rows for one product, whichever way the second one arrives.
    /// </summary>
    [Fact]
    public async Task An_edit_onto_another_products_key_is_a_conflict()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var zero = await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar");
        var before = await fixture.MasterProductAsync(zero);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.DeniedMasterProductWriteStore().UpdateAsync(
                zero,
                Edit("Coca-Cola", "Classic", 375m, before!.UpdatedAtUtc),
                Now,
                CancellationToken.None));

        Assert.Equal(ProductMatchReviewPolicy.DuplicateMasterProductMessage, refusal.Message);

        var stored = await fixture.MasterProductAsync(zero);
        Assert.Equal("Zero Sugar", stored!.Name);
        Assert.Equal(before!.UpdatedAtUtc, stored.UpdatedAtUtc);
    }

    /// <summary>
    /// The issue's own exception to that rule: a row is not a duplicate of itself, so saving an
    /// unchanged description - or correcting only its capitalisation, which folds to the same key -
    /// is allowed and stamps the instant.
    /// </summary>
    [Theory]
    [InlineData("Coca-Cola", "Classic")]
    [InlineData("coca-cola", "classic")]
    public async Task An_edit_saving_its_own_key_is_allowed(string brand, string name)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var id = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var before = await fixture.MasterProductAsync(id);

        var updated = await fixture.DeniedMasterProductWriteStore().UpdateAsync(
            id,
            Edit(brand, name, 375m, before!.UpdatedAtUtc),
            Now,
            CancellationToken.None);

        Assert.Equal(brand, updated!.Brand);
        Assert.Equal(Now, updated.UpdatedAtUtc);
        Assert.Equal(before.NormalisedKey, (await fixture.MasterProductAsync(id))!.NormalisedKey);
    }

    /// <summary>
    /// The stale-edit refusal: an edit presenting an instant the row no longer carries is refused
    /// rather than applied, so two Super Admin tabs cannot overwrite each other silently. The row is
    /// exactly as the first edit left it.
    /// </summary>
    [Fact]
    public async Task An_edit_presenting_a_superseded_instant_is_refused_and_changes_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var id = await fixture.SeedMasterProductAsync("Coke", "Classic");
        var read = (await fixture.MasterProductAsync(id))!.UpdatedAtUtc;

        // The first tab's correction goes through and moves the instant.
        await fixture.DeniedMasterProductWriteStore().UpdateAsync(
            id,
            Edit("Coca-Cola", "Classic", 375m, read),
            Now,
            CancellationToken.None);

        // The second tab still holds the instant it read before that.
        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.DeniedMasterProductWriteStore().UpdateAsync(
                id,
                Edit("Pepsi", "Max", 375m, read),
                Now.AddMinutes(1),
                CancellationToken.None));

        Assert.Equal(MasterProductEditPolicy.StaleEditMessage, refusal.Message);

        var stored = await fixture.MasterProductAsync(id);
        Assert.Equal("Coca-Cola", stored!.Brand);
        Assert.Equal(Now, stored.UpdatedAtUtc);
    }

    #endregion

    #region The size safeguard

    /// <summary>
    /// <strong>The financial safeguard of this issue.</strong> A confirmed supplier listing's pack
    /// size was confirmed against the size this row holds, and a per-unit comparison divides by it, so
    /// a change to the size value or the unit is refused while any listing of the product is
    /// confirmed - and nothing at all is written, the listing included.
    /// </summary>
    [Theory]
    [InlineData(600, MasterProductSizeUnit.Millilitre)]
    [InlineData(375, MasterProductSizeUnit.Gram)]
    public async Task A_size_change_is_refused_while_a_listing_is_confirmed(
        decimal size,
        MasterProductSizeUnit unit)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var id = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic", 375m);
        var listing = await fixture.SeedListingAsync(id, "SKU-COLA-30", unitsPerPack: 30);
        await fixture.ConfirmListingAsync(listing);
        var before = await fixture.MasterProductAsync(id);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.DeniedMasterProductWriteStore().UpdateAsync(
                id,
                new MasterProductEdit(
                    MasterProductPolicy.Normalise("Coca-Cola", "Classic", size, unit),
                    before!.UpdatedAtUtc),
                Now,
                CancellationToken.None));

        Assert.Equal(MasterProductEditPolicy.SizeChangeRefusedMessage, refusal.Message);

        var stored = await fixture.MasterProductAsync(id);
        Assert.Equal(375m, stored!.SizeValue);
        Assert.Equal(MasterProductSizeUnit.Millilitre, stored.SizeUnit);
        Assert.Equal(before!.NormalisedKey, stored.NormalisedKey);
        Assert.Equal(before.UpdatedAtUtc, stored.UpdatedAtUtc);

        // And the listing is exactly as it was: a refused edit decides nothing about a listing.
        var unchanged = await fixture.ListingAsync(listing);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, unchanged.MatchStatus);
        Assert.Equal(30, unchanged.UnitsPerPack);
    }

    /// <summary>
    /// The other half of the criterion: a brand or a name edit is always allowed, confirmed listings
    /// or not. Those are the shared catalogue's own label for the product, and no pack price was ever
    /// divided by either.
    /// </summary>
    [Fact]
    public async Task A_brand_or_name_edit_is_allowed_while_a_listing_is_confirmed()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var id = await fixture.SeedMasterProductAsync("Coke", "Clasic", 375m);
        var listing = await fixture.SeedListingAsync(id, "SKU-COLA-30", unitsPerPack: 30);
        await fixture.ConfirmListingAsync(listing);
        var before = await fixture.MasterProductAsync(id);

        var updated = await fixture.DeniedMasterProductWriteStore().UpdateAsync(
            id,
            Edit("Coca-Cola", "Classic", 375m, before!.UpdatedAtUtc),
            Now,
            CancellationToken.None);

        Assert.Equal("Coca-Cola", updated!.Brand);
        Assert.Equal("Classic", updated.Name);
        Assert.Equal(1, updated.ListingCounts.Confirmed);

        // The listing still hangs from the same product, with the pack it was confirmed with.
        var unchanged = await fixture.ListingAsync(listing);
        Assert.Equal(id, unchanged.MasterProductId);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, unchanged.MatchStatus);
        Assert.Equal(30, unchanged.UnitsPerPack);
    }

    /// <summary>
    /// A size change with no confirmed listing is allowed: a proposed, flagged or refused listing's
    /// pack size is nobody's decision yet, so there is no confirmed pack to invalidate.
    /// </summary>
    [Theory]
    [InlineData(SupplierListingMatchStatus.Proposed)]
    [InlineData(SupplierListingMatchStatus.NeedsReview)]
    [InlineData(SupplierListingMatchStatus.Rejected)]
    public async Task A_size_change_is_allowed_with_no_confirmed_listing(
        SupplierListingMatchStatus status)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var id = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic", 375m);
        var listing = await fixture.SeedListingAsync(id, "SKU-COLA-30", unitsPerPack: 30);
        await fixture.SetListingStatusAsync(listing, status);
        var before = await fixture.MasterProductAsync(id);

        var updated = await fixture.DeniedMasterProductWriteStore().UpdateAsync(
            id,
            Edit("Coca-Cola", "Classic", 600m, before!.UpdatedAtUtc),
            Now,
            CancellationToken.None);

        Assert.Equal(600m, updated!.SizeValue);
        Assert.Equal(
            MasterProductPolicy.NormalisedKey("Coca-Cola", "Classic", 600m, MasterProductSizeUnit.Millilitre),
            (await fixture.MasterProductAsync(id))!.NormalisedKey);
    }

    /// <summary>
    /// Re-submitting the same size in another spelling is not a size change, so it is not refused as
    /// one: <c>375</c> and <c>375.000</c> are one size, and an edit of the brand that happens to
    /// re-send the size must not be blocked by the safeguard.
    /// </summary>
    [Fact]
    public async Task Resubmitting_the_same_size_differently_spelled_is_not_a_size_change()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var id = await fixture.SeedMasterProductAsync("Coke", "Classic", 375m);
        var listing = await fixture.SeedListingAsync(id, "SKU-COLA-30", unitsPerPack: 30);
        await fixture.ConfirmListingAsync(listing);
        var before = await fixture.MasterProductAsync(id);

        var updated = await fixture.DeniedMasterProductWriteStore().UpdateAsync(
            id,
            Edit("Coca-Cola", "Classic", 375.000m, before!.UpdatedAtUtc),
            Now,
            CancellationToken.None);

        Assert.Equal("Coca-Cola", updated!.Brand);
        Assert.Equal(375m, updated.SizeValue);
    }

    /// <summary>
    /// <strong>The concurrency case the review correction asks for</strong>: a listing confirmed
    /// <em>after</em> the editing page was read, and before the size edit was submitted, still
    /// refuses that edit - so a listing confirmed against the old size cannot be silently invalidated
    /// by one.
    ///
    /// <para>What makes that true is where the count is read: the safeguard is decided inside the
    /// edit's own <c>BEGIN IMMEDIATE</c> transaction, over the committed state at that moment, rather
    /// than from anything the page carried. The confirmation here is the real Supplier Price Review
    /// store's, taking the same write lock, which is what serialises the two.</para>
    /// </summary>
    [Fact]
    public async Task A_listing_confirmed_after_the_page_was_read_still_refuses_the_size_edit()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var id = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic", 375m);
        var listing = await fixture.SeedListingAsync(id, "SKU-COLA-30", unitsPerPack: 30);

        // What the editing page read: nothing is confirmed, so a size edit would have been allowed.
        var read = await fixture.MasterProductAsync(id);
        Assert.Equal(0, (await fixture.DeniedMasterProductStore().FindAsync(id, CancellationToken.None))!
            .Product.ListingCounts.Confirmed);

        // Meanwhile, in the Supplier Price Review, somebody confirms that listing.
        var confirmed = await fixture.DeniedReviewStore().DecideListingAsync(
            new SupplierListingReviewSubmission(
                listing,
                SupplierListingReviewOutcome.Confirmed,
                UnitsPerPack: 30,
                Barcode: null,
                BarcodeScope: null,
                Variant: null,
                SizeValue: null,
                SizeUnit: null,
                ApprovePriceException: false),
            Now,
            CancellationToken.None);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, confirmed!.MatchStatus);

        // The edit was prepared against the earlier read and is still presenting a current instant -
        // a listing confirmation does not touch the master product - so only the in-transaction
        // count can refuse it, and it does.
        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.DeniedMasterProductWriteStore().UpdateAsync(
                id,
                Edit("Coca-Cola", "Classic", 600m, read!.UpdatedAtUtc),
                Now.AddMinutes(1),
                CancellationToken.None));

        Assert.Equal(MasterProductEditPolicy.SizeChangeRefusedMessage, refusal.Message);
        Assert.Equal(375m, (await fixture.MasterProductAsync(id))!.SizeValue);
    }

    #endregion

    #region What a write may not reach

    /// <summary>
    /// <strong>The decisive assertion of this issue.</strong> An edit keeps the row's key, so every
    /// mapping, supplier listing, match suggestion and collector work item that pointed at this master
    /// product still points at it - and <strong>no other row changed at all</strong>, in either
    /// business, while both of them are tracking the very product being corrected.
    ///
    /// <para>That is what "manage master products without changing businesses" means in the database:
    /// the mappings keep their status, their match source and their own instants, and so do the
    /// products, purchases, suppliers and stock behind them.</para>
    /// </summary>
    [Fact]
    public async Task An_edit_keeps_every_link_and_changes_no_other_row()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var id = await fixture.SeedMasterProductAsync("Coke", "Clasic", 375m);
        var listing = await fixture.SeedListingAsync(id, "SKU-COLA-30", unitsPerPack: 30);
        await fixture.SeedMatchSuggestionAsync(
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"),
            id,
            ProductMatchSuggestionStatus.Accepted);
        var run = await fixture.SeedCollectorRunAsync();
        var workItem = await fixture.SeedWorkItemAsync(run, id, waitingKey: null);

        // Both businesses track that product, matched to this very master product.
        var first = await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        var second = await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);
        await fixture.MatchAsync(first.Id, id);
        await fixture.MatchAsync(second.Id, id);

        var tenantOwnedBefore = await fixture.TenantOwnedFingerprintAsync();
        var read = await fixture.MasterProductAsync(id);

        var updated = await fixture.DeniedMasterProductWriteStore().UpdateAsync(
            id,
            Edit("Coca-Cola", "Classic", 375m, read!.UpdatedAtUtc),
            Now,
            CancellationToken.None);

        // The same row: the id a correction is made on is the id everything already names.
        Assert.Equal(id, updated!.Id);
        Assert.Single(await fixture.AllMasterProductsAsync());

        Assert.Equal(id, (await fixture.ListingAsync(listing)).MasterProductId);
        Assert.Equal(id, Assert.Single(await fixture.AllSuggestionsAsync()).MasterProductId);
        Assert.Equal(id, Assert.Single(await fixture.AllWorkItemsAsync()).MasterProductId);

        foreach (var mapping in await fixture.AllTrackingsAsync())
        {
            Assert.Equal(id, mapping.MasterProductId);
            Assert.Equal(ProductPriceTrackingStatus.Matched, mapping.Status);
            Assert.Equal(ProductPriceTrackingMatchSource.SuperAdmin, mapping.MatchSource);
        }

        // Nothing either business owns moved - not a product, a purchase, a supplier, a stock
        // adjustment or a mapping, including their own instants.
        Assert.Equal(tenantOwnedBefore, await fixture.TenantOwnedFingerprintAsync());
    }

    /// <summary>
    /// A create reaches nothing a business owns either, and decides no listing: it adds one row to the
    /// catalogue and that is all it does.
    /// </summary>
    [Fact]
    public async Task A_create_changes_nothing_a_business_owns_and_decides_no_listing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var existing = await fixture.SeedMasterProductAsync("Pepsi", "Max");
        var listing = await fixture.SeedListingAsync(existing, "SKU-PEPSI");
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var tenantOwnedBefore = await fixture.TenantOwnedFingerprintAsync();

        await fixture.DeniedMasterProductWriteStore().CreateAsync(
            Description("Coca-Cola", "Classic", 375m),
            Now,
            CancellationToken.None);

        Assert.Equal(tenantOwnedBefore, await fixture.TenantOwnedFingerprintAsync());

        // The listing that existed is untouched - still proposed, still on the product it was on.
        var unchanged = await fixture.ListingAsync(listing);
        Assert.Equal(SupplierListingMatchStatus.Proposed, unchanged.MatchStatus);
        Assert.Equal(existing, unchanged.MasterProductId);
        Assert.Empty(await fixture.AllObservationsAsync());
    }

    /// <summary>
    /// The write is a <em>platform</em> write, and what bounds it is authorization rather than a
    /// scope: it behaves identically on a context scoped to one business, and still reaches nothing
    /// that business owns.
    ///
    /// <para>Asserted because it is the inverse of the review's own lease test. This adapter needs no
    /// lease at all - a master product is not <c>IBusinessOwned</c> - so the thing worth pinning is
    /// that the absence of a lease has not quietly become a way to write a tenant-owned row.</para>
    /// </summary>
    [Fact]
    public async Task The_write_behaves_the_same_on_a_business_scope_and_reaches_nothing_tenant_owned()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var tenantOwnedBefore = await fixture.TenantOwnedFingerprintAsync();

        var created = await fixture.MasterProductWriteStoreFor(fixture.FirstBusinessId).CreateAsync(
            Description("Coca-Cola", "Classic", 375m),
            Now,
            CancellationToken.None);

        Assert.Equal("Coca-Cola", (await fixture.MasterProductAsync(created.Id))!.Brand);
        Assert.Equal(tenantOwnedBefore, await fixture.TenantOwnedFingerprintAsync());
    }

    #endregion

    private static NormalisedMasterProduct Description(string brand, string name, decimal size) =>
        MasterProductPolicy.Normalise(brand, name, size, MasterProductSizeUnit.Millilitre);

    private static MasterProductEdit Edit(
        string brand,
        string name,
        decimal size,
        DateTime expectedUpdatedAtUtc) =>
        new(Description(brand, name, size), expectedUpdatedAtUtc);
}
