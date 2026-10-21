using Inventory.Application.CatalogueReview;
using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.CatalogueReview;

/// <summary>
/// The provider catalogue review as a whole (issue #738, parent #329 decision C1): what a review
/// offers, what an apply writes, what it refuses, and what it leaves alone.
///
/// <para>Every test runs the real use cases over the real
/// <c>Inventory.Infrastructure.Persistence.EfCatalogueReviewStore</c> against a relational SQLite
/// database, for the reason the base class records: atomicity, the filtered unique index over
/// <c>(BusinessId, ExternalProductId)</c> and "nothing was committed" are relational facts.</para>
/// </summary>
public class CatalogueReviewTests : CatalogueReviewTestBase
{
    /// <summary>
    /// Linking a provider group to a local category whose own key is <em>not</em> the
    /// <c>ProductGroupID</c> - the case issue #735 made ordinary. The category keeps its key, records
    /// the group's id, takes the provider's name, and the group's products are assigned to it; a later
    /// review then resolves the same group through that recorded id rather than offering it again.
    /// </summary>
    [Fact]
    public async Task Linking_a_group_to_a_local_category_records_its_id_renames_it_and_takes_its_products()
    {
        await SeedAsync(
            new Category { Id = 7, Name = "Local snacks" },
            new Product { Id = 50, Name = "Chips", RestockTo = 0 });

        var nayax = Nayax(
            [Product(100, "Nayax Chips", 3.50m, groupId: 10)],
            [Group(10, "Snacks")]);

        await using (var db = Scoped())
        {
            var review = await Start(db, nayax).Handle(CancellationToken.None);
            var category = CategoryEntry(review, 10);
            var product = ProductEntry(review, 100);

            Assert.False(category.IsLinked);
            Assert.False(product.IsLinked);

            var applied = await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(
                    review.ReviewId,
                    [
                        LinkTo(category.ItemId, 7),
                        LinkTo(product.ItemId, 50),
                    ]),
                CancellationToken.None);

            Assert.False(applied.AlreadyApplied);
            Assert.Equal(2, applied.LinkedCount);
            Assert.Equal(0, applied.CreatedCount);
        }

        await using (var verify = Scoped())
        {
            var category = await verify.Categories.AsNoTracking().SingleAsync();
            Assert.Equal((7L, NayaxExternalId.From(10), "Snacks"),
                (category.Id, category.ExternalCategoryId, category.Name));

            var product = await verify.Products.AsNoTracking().SingleAsync();
            Assert.Equal(
                (50L, NayaxExternalId.From(100), "Nayax Chips", (long?)7),
                (product.Id, product.ExternalProductId, product.Name, product.CategoryId));
        }

        // The second review resolves both entries through the recorded ids, not by name and not by
        // key: it offers no link at all, which is what makes the first apply durable.
        await using var later = Scoped();
        var second = await Start(later, nayax, StartedAt.AddDays(1)).Handle(CancellationToken.None);
        Assert.Equal((long?)7, CategoryEntry(second, 10).LinkedCategoryId);
        Assert.Equal((long?)50, ProductEntry(second, 100).LinkedProductId);
        Assert.Equal(0, second.UnlinkedProductCount + second.UnlinkedCategoryCount);
    }

    /// <summary>
    /// Creating, rather than linking: the new rows are keyed by the database and record the provider's
    /// ids beside those keys (issue #735), so a provider id that happens to equal no local key - and a
    /// local key that happens to equal no provider id - is the normal case rather than a collision.
    /// Since issue #792 a created product is initialised with no selling price at all, because a
    /// product has none: the provider's price is neither read nor written.
    /// </summary>
    [Fact]
    public async Task Creating_a_local_row_keys_it_by_the_database_and_records_the_provider_id_beside_it()
    {
        await SeedAsync();

        var nayax = Nayax(
            [Product(100, "Nayax Chips", 3.50m, groupId: 10)],
            [Group(10, "Snacks")]);

        await using (var db = Scoped())
        {
            var review = await Start(db, nayax).Handle(CancellationToken.None);
            var applied = await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(
                    review.ReviewId,
                    [
                        Create(CategoryEntry(review, 10).ItemId),
                        Create(ProductEntry(review, 100).ItemId),
                    ]),
                CancellationToken.None);

            Assert.Equal(2, applied.CreatedCount);
            Assert.Equal(0, applied.LinkedCount);

            // A created row takes its provider values rather than having them changed: there is no
            // local name or category it replaced, and the link already reports the name. Nothing is
            // accepted as a negotiated field, because the one the provider priced is gone.
            Assert.Empty(applied.AutomaticChanges);
            Assert.Empty(applied.AcceptedChanges);
            Assert.Empty(applied.SkippedChanges);
        }

        await using var verify = Scoped();
        var category = await verify.Categories.AsNoTracking().SingleAsync();
        var product = await verify.Products.AsNoTracking().SingleAsync();

        Assert.Equal((NayaxExternalId.From(10), "Snacks"), (category.ExternalCategoryId, category.Name));
        Assert.Equal(
            (NayaxExternalId.From(100), "Nayax Chips", (long?)category.Id),
            (product.ExternalProductId, product.Name, product.CategoryId));

        // The provider's price reached nothing: no field decision was recorded about it, and the
        // stored review entry carries none either.
        Assert.Empty(await verify.ProviderCatalogueFieldDecisions.AsNoTracking().ToListAsync());
        Assert.All(
            await verify.ProviderCatalogueReviewItems.AsNoTracking().ToListAsync(),
            item => Assert.Null(item.ProviderUnitPrice));

        // The keys are the database's own: neither row is keyed by the provider's number.
        Assert.NotEqual(100L, product.Id);
        Assert.NotEqual(10L, category.Id);
    }

    /// <summary>
    /// The provider owns a linked row's name and category outright: a later rename, and a move to
    /// another group, are applied without being offered and are listed in the apply's result.
    /// </summary>
    [Fact]
    public async Task A_later_provider_rename_or_category_change_is_applied_automatically_and_listed()
    {
        await SeedAsync(
            new Category { Id = 7, Name = "Snacks", ExternalCategoryId = NayaxExternalId.From(10) },
            new Product
            {
                Id = 50,
                Name = "Nayax Chips",
                ExternalProductId = NayaxExternalId.From(100),
                CategoryId = 7,
                RestockTo = 0,
            });

        var nayax = Nayax(
            [Product(100, "Nayax Chips Large", 3.50m, groupId: 20)],
            [Group(10, "Snacks"), Group(20, "Bars")]);

        CatalogueReviewApplied applied;
        await using (var db = Scoped())
        {
            var review = await Start(db, nayax).Handle(CancellationToken.None);
            var product = ProductEntry(review, 100);

            // Offered as nothing: both fields are the provider's, so the review lists them as changes
            // it will make rather than as decisions to take.
            Assert.True(product.IsLinked);
            Assert.Empty(product.Choices);
            Assert.Equal(
                [ProviderManagedCatalogueFields.Name, ProviderManagedCatalogueFields.CategoryId],
                product.AutomaticChanges.Select(change => change.Field));

            applied = await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(
                    review.ReviewId, [Create(CategoryEntry(review, 20).ItemId)]),
                CancellationToken.None);
        }

        Assert.Equal(
            [ProviderManagedCatalogueFields.Name, ProviderManagedCatalogueFields.CategoryId],
            applied.AutomaticChanges
                .Where(change => change.Kind == nameof(ProviderCatalogueEntryKind.Product))
                .Select(change => change.Field));

        await using var verify = Scoped();
        var bars = await verify.Categories.AsNoTracking()
            .SingleAsync(category => category.ExternalCategoryId == NayaxExternalId.From(20));
        var moved = await verify.Products.AsNoTracking().SingleAsync();
        Assert.Equal(("Nayax Chips Large", (long?)bars.Id), (moved.Name, moved.CategoryId));
    }

    /// <summary>
    /// The negotiable half is empty since issue #792 (A2 of #786). A product carries no selling
    /// price, so a provider price - even one that has moved since the last reading - is neither read
    /// into the stored review nor offered as a choice, for a linked product or any other.
    /// </summary>
    [Fact]
    public async Task A_provider_price_is_neither_recorded_on_the_review_nor_offered_as_a_choice()
    {
        await SeedAsync(Linked(50, 100, "Chips"), Linked(51, 101, "Bar"));

        await using var db = Scoped();
        var review = await Start(db, Nayax([Product(100, "Chips", 3.50m), Product(101, "Bar", 2.50m)], []))
            .Handle(CancellationToken.None);

        Assert.Empty(ProductEntry(review, 100).Choices);
        Assert.Empty(ProductEntry(review, 101).Choices);
        Assert.All(
            await db.ProviderCatalogueReviewItems.AsNoTracking().ToListAsync(),
            item => Assert.Null(item.ProviderUnitPrice));
    }

    /// <summary>
    /// An older client that still submits a keep-or-take answer about the retired selling price is
    /// refused, and the refusal is atomic: the link beside it, which would have succeeded on its
    /// own, is not written either. A decision the server cannot carry out must never be reported as
    /// applied (issue #792).
    /// </summary>
    [Fact]
    public async Task A_submitted_selling_price_decision_is_refused_and_the_whole_apply_writes_nothing()
    {
        await SeedAsync(
            new Product { Id = 50, Name = "Chips", RestockTo = 0 },
            new Product { Id = 51, Name = "Bar", RestockTo = 0 });

        await using var db = Scoped();
        var review = await Start(db, Nayax([Product(100, "Nayax Chips"), Product(101, "Nayax Bar")], []))
            .Handle(CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => Apply(db).Handle(
            new ApplyCatalogueReviewRequest(
                review.ReviewId,
                [
                    LinkTo(ProductEntry(review, 101).ItemId, 51),
                    RetiredPriceDecision(
                        ProductEntry(review, 100).ItemId,
                        ProviderFieldDecision.TakeProvider,
                        CatalogueReviewAction.LinkExisting,
                        50),
                ]),
            CancellationToken.None));

        Assert.Contains(
            ApplyCatalogueReview.RetiredSellingPriceDecisionMessage,
            refusal.Message,
            StringComparison.Ordinal);

        await using var verify = Scoped();
        Assert.Equal(
            [(50L, (string?)null), (51L, (string?)null)],
            await verify.Products.AsNoTracking()
                .OrderBy(product => product.Id)
                .Select(product => ValueTuple.Create(product.Id, product.ExternalProductId))
                .ToListAsync());
        Assert.Empty(await verify.ProviderCatalogueFieldDecisions.AsNoTracking().ToListAsync());
        Assert.Null(await verify.ProviderCatalogueReviews.AsNoTracking()
            .Select(stored => stored.AppliedAtUtc)
            .SingleAsync());
    }

    /// <summary>
    /// A review stored before issue #792 still carries the provider prices it meant to negotiate,
    /// against a product field that no longer exists. Applying it would carry out only part of the
    /// plan the operator approved, so it is refused whole - before any link, rename or category
    /// change is written - and the operator is told to start again. A review started afterwards
    /// applies normally against the same catalogue.
    /// </summary>
    [Fact]
    public async Task A_pre_upgrade_review_carrying_provider_prices_is_refused_as_stale_and_a_fresh_one_applies()
    {
        await SeedAsync(new Product { Id = 50, Name = "Chips", RestockTo = 0 });

        var nayax = Nayax([Product(100, "Nayax Chips", 3.50m)], []);

        long staleItemId;
        long staleReviewId;
        await using (var db = Scoped())
        {
            var review = await Start(db, nayax).Handle(CancellationToken.None);
            staleReviewId = review.ReviewId;
            staleItemId = ProductEntry(review, 100).ItemId;
        }

        // Exactly what the previous release wrote: the provider's price on the stored entry.
        await using (var legacy = Scoped())
        {
            var item = await legacy.ProviderCatalogueReviewItems.SingleAsync(row => row.Id == staleItemId);
            item.ProviderUnitPrice = "3.50";
            await legacy.SaveChangesAsync();
        }

        await using (var db = Scoped())
        {
            var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => Apply(db).Handle(
                new ApplyCatalogueReviewRequest(staleReviewId, [LinkTo(staleItemId, 50)]),
                CancellationToken.None));

            Assert.Equal(ApplyCatalogueReview.StaleSellingPriceReviewMessage, refusal.Message);
        }

        await using (var verify = Scoped())
        {
            // Nothing was linked, nothing was renamed, and the review is still unapplied - so the
            // operator can start a fresh one rather than being left with half a catalogue.
            var untouched = await verify.Products.AsNoTracking().SingleAsync();
            Assert.Equal((50L, "Chips", (string?)null), (untouched.Id, untouched.Name, untouched.ExternalProductId));
            Assert.Null(await verify.ProviderCatalogueReviews.AsNoTracking()
                .Select(stored => stored.AppliedAtUtc)
                .SingleAsync());
            Assert.Equal("3.50", await verify.ProviderCatalogueReviewItems.AsNoTracking()
                .Where(item => item.Id == staleItemId)
                .Select(item => item.ProviderUnitPrice)
                .SingleAsync());
        }

        // A fresh reading of the same catalogue records no provider price and applies normally.
        await using (var fresh = Scoped())
        {
            var review = await Start(fresh, nayax, StartedAt.AddDays(1)).Handle(CancellationToken.None);
            var applied = await Apply(fresh, AppliedAt.AddDays(1)).Handle(
                new ApplyCatalogueReviewRequest(
                    review.ReviewId, [LinkTo(ProductEntry(review, 100).ItemId, 50)]),
                CancellationToken.None);

            Assert.Equal(1, applied.LinkedCount);
        }

        await using var after = Scoped();
        var linked = await after.Products.AsNoTracking().SingleAsync();
        Assert.Equal(
            (50L, "Nayax Chips", NayaxExternalId.From(100)),
            (linked.Id, linked.Name, linked.ExternalProductId));
    }

    /// <summary>
    /// Nothing is linked by name. A local product called exactly what the provider calls its product
    /// stays unlinked, is reported as a decision to take, and an apply that takes no decision about it
    /// changes nothing at all - it neither links that row nor creates a second one.
    /// </summary>
    [Fact]
    public async Task A_matching_name_links_nothing_and_an_undecided_entry_changes_nothing()
    {
        await SeedAsync(new Product { Id = 50, Name = "Nayax Chips", RestockTo = 0 });

        await using (var db = Scoped())
        {
            var review = await Start(db, Nayax([Product(100, "Nayax Chips", 3.50m)], []))
                .Handle(CancellationToken.None);

            Assert.False(ProductEntry(review, 100).IsLinked);
            Assert.Null(ProductEntry(review, 100).LinkedProductId);

            var applied = await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(review.ReviewId), CancellationToken.None);

            Assert.Equal(0, applied.CreatedCount + applied.LinkedCount);
            Assert.Empty(applied.AcceptedChanges);
        }

        await using var verify = Scoped();
        var product = await verify.Products.AsNoTracking().SingleAsync();
        Assert.Equal((50L, "Nayax Chips", (string?)null),
            (product.Id, product.Name, product.ExternalProductId));
    }

    /// <summary>
    /// A local row that already carries a different provider id cannot also carry this one, and the
    /// refusal leaves the whole apply unwritten - including the link beside it that would have
    /// succeeded on its own. That is what "atomic" means here.
    /// </summary>
    [Fact]
    public async Task Linking_a_row_that_already_carries_a_different_provider_id_refuses_the_whole_apply()
    {
        await SeedAsync(
            Linked(50, 77, "Chips"),
            new Product { Id = 51, Name = "Bar", RestockTo = 0 });

        await using var db = Scoped();
        var review = await Start(db, Nayax([Product(100, "Nayax Chips"), Product(101, "Nayax Bar")], []))
            .Handle(CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => Apply(db).Handle(
            new ApplyCatalogueReviewRequest(
                review.ReviewId,
                [
                    LinkTo(ProductEntry(review, 101).ItemId, 51),
                    LinkTo(ProductEntry(review, 100).ItemId, 50),
                ]),
            CancellationToken.None));

        Assert.Contains(NayaxExternalId.From(77), refusal.Message, StringComparison.Ordinal);

        await using var verify = Scoped();
        Assert.Equal(
            [(50L, NayaxExternalId.From(77)), (51L, (string?)null)],
            await verify.Products.AsNoTracking()
                .OrderBy(product => product.Id)
                .Select(product => ValueTuple.Create(product.Id, product.ExternalProductId))
                .ToListAsync());
        Assert.Null(await verify.ProviderCatalogueReviews.AsNoTracking()
            .Select(stored => stored.AppliedAtUtc)
            .SingleAsync());
    }

    /// <summary>
    /// The stale-state guard. The review said nothing carried provider product 100; by the time the
    /// apply runs, another writer has recorded it on a different row. The apply is decided on the rows
    /// as they are, so it refuses rather than recording the same provider id twice.
    /// </summary>
    [Fact]
    public async Task A_provider_id_another_row_took_since_the_review_refuses_the_apply()
    {
        await SeedAsync(
            new Product { Id = 50, Name = "Chips", RestockTo = 0 },
            new Product { Id = 51, Name = "Other", RestockTo = 0 });

        await using var db = Scoped();
        var review = await Start(db, Nayax([Product(100, "Nayax Chips")], [])).Handle(CancellationToken.None);

        await using (var other = Scoped())
        {
            var claimed = await other.Products.SingleAsync(product => product.Id == 51);
            claimed.ExternalProductId = NayaxExternalId.From(100);
            await other.SaveChangesAsync();
        }

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => Apply(db).Handle(
            new ApplyCatalogueReviewRequest(review.ReviewId, [LinkTo(ProductEntry(review, 100).ItemId, 50)]),
            CancellationToken.None));

        Assert.Contains("already carries this provider id", refusal.Message, StringComparison.Ordinal);

        await using var verify = Scoped();
        Assert.Null(await verify.Products.AsNoTracking()
            .Where(product => product.Id == 50)
            .Select(product => product.ExternalProductId)
            .SingleAsync());
    }

    /// <summary>
    /// Applying the same review twice changes nothing. The second apply writes no row, records no
    /// field decision and reports that it changed nothing, rather than failing or repeating itself.
    /// </summary>
    [Fact]
    public async Task Applying_the_same_review_a_second_time_changes_nothing()
    {
        await SeedAsync(new Product { Id = 50, Name = "Chips", RestockTo = 0 });

        long reviewId;
        long itemId;
        await using (var db = Scoped())
        {
            var review = await Start(db, Nayax([Product(100, "Nayax Chips", 3.50m)], []))
                .Handle(CancellationToken.None);
            reviewId = review.ReviewId;
            itemId = ProductEntry(review, 100).ItemId;
            await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(reviewId, [LinkTo(itemId, 50)]),
                CancellationToken.None);
        }

        var before = await SnapshotAsync();

        await using (var again = Scoped())
        {
            var applied = await Apply(again, AppliedAt.AddHours(1)).Handle(
                new ApplyCatalogueReviewRequest(reviewId, [LinkTo(itemId, 50)]),
                CancellationToken.None);

            Assert.True(applied.AlreadyApplied);
            Assert.Equal(AppliedAt, applied.AppliedAtUtc);
            Assert.Empty(applied.Links);
            Assert.Empty(applied.AcceptedChanges);
            Assert.Empty(applied.SkippedChanges);
            Assert.Empty(applied.AutomaticChanges);
        }

        Assert.Equal(before, await SnapshotAsync());
    }

    /// <summary>
    /// Linking changes no sale. A sale already attributed to the product keeps that product, its
    /// recorded historical unit cost and its COGS exactly, the product's costing position is
    /// untouched, and the sales the link makes <em>resolvable</em> stay unresolved - counted by the
    /// review, resolved by nothing here.
    /// </summary>
    [Fact]
    public async Task Linking_leaves_associated_sales_their_costs_and_the_unresolved_ones_alone()
    {
        await SeedAsync(new Product
        {
            Id = 50,
            Name = "Chips",
            RestockTo = 0,
            CostingQuantity = 4,
            InventoryValue = 8m,
            AverageUnitCost = 2m,
        });

        await using (var seed = Scoped())
        {
            seed.NayaxSales.Add(Costed(1, productId: 50, providerProductId: 999));
            seed.NayaxSales.AddRange(Unresolved(2, providerProductId: 100), Unresolved(3, providerProductId: 100));
            await seed.SaveChangesAsync();
        }

        await using (var db = Scoped())
        {
            var review = await Start(db, Nayax([Product(100, "Nayax Chips", 3.50m)], []))
                .Handle(CancellationToken.None);

            Assert.Equal(2, ProductEntry(review, 100).UnresolvedSaleCount);
            Assert.Equal(2, review.UnresolvedSaleCount);

            var applied = await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(
                    review.ReviewId, [LinkTo(ProductEntry(review, 100).ItemId, 50)]),
                CancellationToken.None);

            // Reported, so the operator can see what the separate resolution would settle - and not
            // settled.
            Assert.Equal(2, applied.UnresolvedSaleCount);
        }

        await using var verify = Scoped();
        var costed = await verify.NayaxSales.AsNoTracking().SingleAsync(sale => sale.TransactionID == 1);
        Assert.Equal(
            ((long?)50, (decimal?)2m, (decimal?)2m, SaleCostingStatus.Costed, SaleCostSource.InventoryLedger),
            (costed.ProductId, costed.UnitCostAtSale, costed.CostOfGoodsSold, costed.CostingStatus, costed.CostSource));

        Assert.Equal(
            [(2L, (long?)null), (3L, (long?)null)],
            await verify.NayaxSales.AsNoTracking()
                .Where(sale => sale.TransactionID != 1)
                .OrderBy(sale => sale.TransactionID)
                .Select(sale => ValueTuple.Create(sale.TransactionID, sale.ProductId))
                .ToListAsync());

        var product = await verify.Products.AsNoTracking().SingleAsync();
        Assert.Equal(((int?)4, (decimal?)8m, 2m), (product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
        Assert.Empty(await verify.NayaxSaleProductResolutions.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Starting a review replaces the business's unapplied one: a review is a reading of the
    /// provider's catalogue at an instant, and an older reading is not a second opinion. The database
    /// says so too - the filtered unique index permits exactly one pending review per business.
    /// </summary>
    [Fact]
    public async Task Starting_a_review_replaces_the_business_pending_one()
    {
        await SeedAsync();

        long first;
        await using (var db = Scoped())
            first = (await Start(db, Nayax([Product(100, "Chips")], [])).Handle(CancellationToken.None)).ReviewId;

        await using (var db = Scoped())
            await Start(db, Nayax([Product(101, "Bar")], []), StartedAt.AddHours(1))
                .Handle(CancellationToken.None);

        await using var verify = Scoped();
        var pending = Assert.Single(await verify.ProviderCatalogueReviews.AsNoTracking().ToListAsync());
        Assert.NotEqual(first, pending.Id);
        Assert.Equal(StartedAt.AddHours(1), pending.StartedAtUtc);
        Assert.Equal(
            [NayaxExternalId.From(101)],
            await verify.ProviderCatalogueReviewItems.AsNoTracking()
                .Select(item => item.ExternalId)
                .ToListAsync());
    }

    /// <summary>
    /// The provider publishes no unit/volume label, so none is ever proposed. The field stays declared
    /// and the review says why in one sentence, rather than the operator being offered a volume type
    /// id as if it were a unit label - see <c>CatalogueReviewPolicy.UnitVolumeUnavailableNote</c>.
    /// </summary>
    [Fact]
    public async Task No_unit_or_volume_value_is_ever_proposed_because_the_provider_publishes_none()
    {
        await SeedAsync(Linked(50, 100, "Chips", unit: "bag"));

        await using var db = Scoped();
        var review = await Start(db, Nayax([Product(100, "Chips", 3.00m)], [])).Handle(CancellationToken.None);

        Assert.Equal(CatalogueReviewPolicy.UnitVolumeUnavailableNote, review.UnitVolumeNote);
        Assert.Empty(ProductEntry(review, 100).Choices);
        Assert.Null(await db.ProviderCatalogueReviewItems.AsNoTracking()
            .Select(item => item.ProviderUnitVolume)
            .SingleAsync());
    }

    private static Product Linked(
        long localRowId, long nayaxProductId, string name, string? unit = null) =>
        new()
        {
            Id = localRowId,
            Name = name,
            ExternalProductId = NayaxExternalId.From(nayaxProductId),
            Unit = unit,
            RestockTo = 0,
        };

    private static NayaxSales Costed(long transactionId, long productId, long providerProductId) =>
        new()
        {
            TransactionID = transactionId,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            ProductId = productId,
            NayaxProductId = providerProductId,
            ProviderProductIdIsRaw = true,
            MachineAuthorizationTime = StartedAt.AddDays(-5),
            UnitCostAtSale = 2m,
            CostOfGoodsSold = 2m,
            CostingStatus = SaleCostingStatus.Costed,
            CostSource = SaleCostSource.InventoryLedger,
        };

    private static NayaxSales Unresolved(long transactionId, long providerProductId) =>
        new()
        {
            TransactionID = transactionId,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            ProductId = null,
            NayaxProductId = providerProductId,
            ProviderProductIdIsRaw = true,
            MachineAuthorizationTime = StartedAt.AddDays(-4),
        };

    private async Task SeedAsync(params object[] rows)
    {
        await using var setup = Scoped();
        foreach (var row in rows)
            setup.Add(row);
        await setup.SaveChangesAsync();
    }

    /// <summary>
    /// Everything a second apply could change, as one comparable value: the catalogue rows, the
    /// recorded field decisions and the sales' attribution and cost.
    /// </summary>
    private async Task<string> SnapshotAsync()
    {
        await using var db = Scoped();
        var products = await db.Products.AsNoTracking().OrderBy(product => product.Id).ToListAsync();
        var categories = await db.Categories.AsNoTracking().OrderBy(category => category.Id).ToListAsync();
        var decisions = await db.ProviderCatalogueFieldDecisions.AsNoTracking()
            .OrderBy(decision => decision.Id).ToListAsync();
        var sales = await db.NayaxSales.AsNoTracking().OrderBy(sale => sale.Id).ToListAsync();

        return string.Join("|",
            products.Select(product =>
                $"P{product.Id}:{product.ExternalProductId}:{product.Name}:{product.CategoryId}:{product.Unit}")
            .Concat(categories.Select(category =>
                $"C{category.Id}:{category.ExternalCategoryId}:{category.Name}"))
            .Concat(decisions.Select(decision =>
                $"D{decision.ProductId}:{decision.Field}:{decision.ProviderValue}:{decision.Decision}:{decision.DecidedAtUtc:O}"))
            .Concat(sales.Select(sale =>
                $"S{sale.TransactionID}:{sale.ProductId}:{sale.UnitCostAtSale}:{sale.CostOfGoodsSold}:{sale.CostingStatus}")));
    }
}
