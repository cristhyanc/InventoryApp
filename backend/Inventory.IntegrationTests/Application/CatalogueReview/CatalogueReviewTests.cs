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
            new Product { Id = 50, Name = "Chips", UnitPrice = 3.00m, RestockTo = 0 });

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
                        Price(
                            product.ItemId,
                            ProviderFieldDecision.TakeProvider,
                            CatalogueReviewAction.LinkExisting,
                            50),
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
                (50L, NayaxExternalId.From(100), "Nayax Chips", (long?)7, 3.50m),
                (product.Id, product.ExternalProductId, product.Name, product.CategoryId, product.UnitPrice));
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
    /// A created product has no local value to keep, so it takes the provider's price.
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
            // local name or category it replaced, and the link already reports the name. The price it
            // took is still reported as a decision, because it is one and it is recorded.
            Assert.Empty(applied.AutomaticChanges);
            Assert.Equal(
                [(NayaxExternalId.From(100), nameof(NegotiableCatalogueField.UnitPrice), (string?)null, "3.50")],
                applied.AcceptedChanges.Select(change =>
                    (change.ExternalId, change.Field, change.PreviousValue, change.ProviderValue)));
        }

        await using var verify = Scoped();
        var category = await verify.Categories.AsNoTracking().SingleAsync();
        var product = await verify.Products.AsNoTracking().SingleAsync();

        Assert.Equal((NayaxExternalId.From(10), "Snacks"), (category.ExternalCategoryId, category.Name));
        Assert.Equal(
            (NayaxExternalId.From(100), "Nayax Chips", (long?)category.Id, 3.50m),
            (product.ExternalProductId, product.Name, product.CategoryId, product.UnitPrice));

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
                UnitPrice = 3.50m,
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
    /// The negotiable half. Two linked products whose provider price has moved: one keeps its local
    /// price, the other takes the provider's, and both answers are recorded so neither is asked again.
    /// </summary>
    [Fact]
    public async Task A_changed_provider_price_is_taken_or_kept_per_product_and_both_answers_are_recorded()
    {
        await SeedAsync(
            Linked(50, 100, "Chips", 3.00m),
            Linked(51, 101, "Bar", 2.00m));

        var nayax = Nayax(
            [Product(100, "Chips", 3.50m), Product(101, "Bar", 2.50m)],
            []);

        await using (var db = Scoped())
        {
            var review = await Start(db, nayax).Handle(CancellationToken.None);
            var chips = ProductEntry(review, 100);
            var bar = ProductEntry(review, 101);

            Assert.Equal(
                [new CatalogueReviewFieldDto(nameof(NegotiableCatalogueField.UnitPrice), "3.00", "3.50")],
                chips.Choices);

            var applied = await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(
                    review.ReviewId,
                    [
                        Price(chips.ItemId, ProviderFieldDecision.TakeProvider),
                        Price(bar.ItemId, ProviderFieldDecision.KeepLocal),
                    ]),
                CancellationToken.None);

            Assert.Equal(
                [(NayaxExternalId.From(100), "3.00", "3.50")],
                applied.AcceptedChanges.Select(change =>
                    (change.ExternalId, change.PreviousValue, change.ProviderValue)));
            Assert.Equal(
                [(NayaxExternalId.From(101), "2.00", "2.50")],
                applied.SkippedChanges.Select(change =>
                    (change.ExternalId, change.PreviousValue, change.ProviderValue)));
        }

        await using var verify = Scoped();
        Assert.Equal(
            [(50L, 3.50m), (51L, 2.00m)],
            await verify.Products.AsNoTracking()
                .OrderBy(product => product.Id)
                .Select(product => ValueTuple.Create(product.Id, product.UnitPrice))
                .ToListAsync());

        Assert.Equal(
            [(50L, "3.50", ProviderFieldDecision.TakeProvider), (51L, "2.50", ProviderFieldDecision.KeepLocal)],
            await verify.ProviderCatalogueFieldDecisions.AsNoTracking()
                .OrderBy(decision => decision.ProductId)
                .Select(decision => ValueTuple.Create(
                    decision.ProductId, decision.ProviderValue, decision.Decision))
                .ToListAsync());
    }

    /// <summary>
    /// The skip rule, which is the whole reason the last-decided value is stored: a price the operator
    /// kept their own over is not proposed again, and is proposed again the moment the provider moves
    /// it somewhere new.
    /// </summary>
    [Fact]
    public async Task A_skipped_price_is_not_proposed_again_until_the_provider_changes_it_again()
    {
        await SeedAsync(Linked(50, 100, "Chips", 3.00m));

        await using (var db = Scoped())
        {
            var first = await Start(db, Nayax([Product(100, "Chips", 3.50m)], [])).Handle(CancellationToken.None);
            await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(
                    first.ReviewId,
                    [Price(ProductEntry(first, 100).ItemId, ProviderFieldDecision.KeepLocal)]),
                CancellationToken.None);
        }

        await using (var again = Scoped())
        {
            var second = await Start(again, Nayax([Product(100, "Chips", 3.50m)], []), StartedAt.AddDays(1))
                .Handle(CancellationToken.None);

            Assert.Empty(ProductEntry(second, 100).Choices);
        }

        await using var moved = Scoped();
        var third = await Start(moved, Nayax([Product(100, "Chips", 3.95m)], []), StartedAt.AddDays(2))
            .Handle(CancellationToken.None);

        Assert.Equal(
            [new CatalogueReviewFieldDto(nameof(NegotiableCatalogueField.UnitPrice), "3.00", "3.95")],
            ProductEntry(third, 100).Choices);
    }

    /// <summary>
    /// Nothing is linked by name. A local product called exactly what the provider calls its product
    /// stays unlinked, is reported as a decision to take, and an apply that takes no decision about it
    /// changes nothing at all - it neither links that row nor creates a second one.
    /// </summary>
    [Fact]
    public async Task A_matching_name_links_nothing_and_an_undecided_entry_changes_nothing()
    {
        await SeedAsync(new Product { Id = 50, Name = "Nayax Chips", UnitPrice = 3.00m, RestockTo = 0 });

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
        Assert.Equal((50L, "Nayax Chips", (string?)null, 3.00m),
            (product.Id, product.Name, product.ExternalProductId, product.UnitPrice));
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
            Linked(50, 77, "Chips", 3.00m),
            new Product { Id = 51, Name = "Bar", UnitPrice = 2.00m, RestockTo = 0 });

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
            new Product { Id = 50, Name = "Chips", UnitPrice = 3.00m, RestockTo = 0 },
            new Product { Id = 51, Name = "Other", UnitPrice = 1.00m, RestockTo = 0 });

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
        await SeedAsync(Linked(50, 100, "Chips", 3.00m));

        long reviewId;
        await using (var db = Scoped())
        {
            var review = await Start(db, Nayax([Product(100, "Nayax Chips", 3.50m)], []))
                .Handle(CancellationToken.None);
            reviewId = review.ReviewId;
            await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(
                    reviewId, [Price(ProductEntry(review, 100).ItemId, ProviderFieldDecision.TakeProvider)]),
                CancellationToken.None);
        }

        var before = await SnapshotAsync();

        await using (var again = Scoped())
        {
            var applied = await Apply(again, AppliedAt.AddHours(1)).Handle(
                new ApplyCatalogueReviewRequest(
                    reviewId, [Price(1, ProviderFieldDecision.KeepLocal)]),
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
            UnitPrice = 3.00m,
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
        await SeedAsync(Linked(50, 100, "Chips", 3.00m, unit: "bag"));

        await using var db = Scoped();
        var review = await Start(db, Nayax([Product(100, "Chips", 3.00m)], [])).Handle(CancellationToken.None);

        Assert.Equal(CatalogueReviewPolicy.UnitVolumeUnavailableNote, review.UnitVolumeNote);
        Assert.Empty(ProductEntry(review, 100).Choices);
        Assert.Null(await db.ProviderCatalogueReviewItems.AsNoTracking()
            .Select(item => item.ProviderUnitVolume)
            .SingleAsync());
    }

    private static Product Linked(
        long localRowId, long nayaxProductId, string name, decimal price, string? unit = null) =>
        new()
        {
            Id = localRowId,
            Name = name,
            ExternalProductId = NayaxExternalId.From(nayaxProductId),
            UnitPrice = price,
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
                $"P{product.Id}:{product.ExternalProductId}:{product.Name}:{product.UnitPrice}:{product.CategoryId}:{product.Unit}")
            .Concat(categories.Select(category =>
                $"C{category.Id}:{category.ExternalCategoryId}:{category.Name}"))
            .Concat(decisions.Select(decision =>
                $"D{decision.ProductId}:{decision.Field}:{decision.ProviderValue}:{decision.Decision}:{decision.DecidedAtUtc:O}"))
            .Concat(sales.Select(sale =>
                $"S{sale.TransactionID}:{sale.ProductId}:{sale.UnitCostAtSale}:{sale.CostOfGoodsSold}:{sale.CostingStatus}")));
    }
}
