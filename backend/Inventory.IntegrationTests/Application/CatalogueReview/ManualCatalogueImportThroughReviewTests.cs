using Inventory.Application.CatalogueReview;
using Inventory.Application.Nayax;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.CatalogueReview;

/// <summary>
/// The acceptance criterion of issue #739 (parent #329 decision C2) on the server side: the product
/// catalogue import now runs for a business that maintains its own catalogue, end to end -
/// read the provider's catalogue, review it, apply one decision per entry.
///
/// <para><strong>This is the case the direct import could not serve.</strong> It identified a
/// product only by the provider's own id, so for a business holding products with none it would have
/// created a duplicate of every one of them - which is why issue #736 refused it outright
/// (<c>409</c>) rather than duplicating them. Issue #739 replaced that import, and its refusal, with
/// this flow for every business, so what has to be proven here is the whole of it: the manual row
/// the operator links keeps its identity and its history, the entry they create becomes a new row,
/// the row they say nothing about is untouched, and nothing is duplicated.</para>
///
/// <para>Relational SQLite and the real use cases and adapter, because the identity this is about is
/// the database's: the keys the apply's created rows get, and the per-business filtered unique index
/// over the external id columns, are things only a real database decides.</para>
/// </summary>
public class ManualCatalogueImportThroughReviewTests : CatalogueReviewTestBase
{
    private const long NayaxColaId = 888;
    private const long NayaxMarsId = 889;
    private const int NayaxBeveragesGroup = 20;

    /// <summary>
    /// One operator catalogue: one group, and two products in it. One of them is the provider's
    /// version of a product the business already keeps itself; the other it has never heard of.
    /// </summary>
    private static readonly NayaxProductGroup[] Groups = [Group(NayaxBeveragesGroup, "Beverages")];

    private static readonly NayaxProduct[] Products =
    [
        Product(NayaxColaId, "Nayax Coke Zero 600ml", 3.50m, NayaxBeveragesGroup),
        Product(NayaxMarsId, "Nayax Mars Bar 53g", 2.80m, NayaxBeveragesGroup),
    ];

    [Fact]
    public async Task The_catalogue_import_runs_for_a_business_that_maintains_its_own_catalogue()
    {
        await SeedManualCatalogueAsync();

        await using (var db = Scoped())
        {
            var review = await Start(db, Nayax(Products, Groups)).Handle(CancellationToken.None);

            // Nothing of the business's own catalogue carries a provider id, so every provider entry
            // is reported unlinked and the review picks none of them. That is the state the direct
            // import refused to run in at all.
            Assert.Equal((1, 2), (review.UnlinkedCategoryCount, review.UnlinkedProductCount));
            Assert.Null(CategoryEntry(review, NayaxBeveragesGroup).LinkedCategoryId);
            Assert.Null(ProductEntry(review, NayaxColaId).LinkedProductId);

            var applied = await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(
                    review.ReviewId,
                    [
                        // The group is the operator's own "Soft drinks" category.
                        LinkTo(CategoryEntry(review, NayaxBeveragesGroup).ItemId, await CategoryIdAsync("Soft drinks")),
                        // Their own cola is the provider's cola.
                        LinkTo(
                            ProductEntry(review, NayaxColaId).ItemId,
                            await ProductIdAsync("Coca-Cola 600ml")),
                        // The provider's Mars Bar is a product they do not keep: a new row.
                        Create(ProductEntry(review, NayaxMarsId).ItemId),
                    ]),
                CancellationToken.None);

            Assert.Equal((2, 1), (applied.LinkedCount, applied.CreatedCount));
        }

        await using var verify = Scoped();

        // The linked manual product is the same row: same key, same stock and costing history, now
        // carrying the provider's id and the two fields the provider owns.
        var cola = await verify.Products.AsNoTracking()
            .SingleAsync(product => product.ExternalProductId == NayaxExternalId.From(NayaxColaId));
        var beverages = await verify.Categories.AsNoTracking()
            .SingleAsync(category => category.ExternalCategoryId == NayaxExternalId.From(NayaxBeveragesGroup));
        Assert.Equal(
            ("Nayax Coke Zero 600ml", (long?)beverages.Id, 12, 1.80m),
            (cola.Name, cola.CategoryId, cola.QuantityInStock, cola.AverageUnitCost));

        // The category is the operator's own row, renamed by the provider that now owns its name.
        Assert.Equal("Beverages", beverages.Name);

        // The created row is a new product, keyed by the database rather than by the provider.
        var mars = await verify.Products.AsNoTracking()
            .SingleAsync(product => product.ExternalProductId == NayaxExternalId.From(NayaxMarsId));
        Assert.NotEqual(NayaxMarsId, mars.Id);
        Assert.Equal(("Nayax Mars Bar 53g", (long?)beverages.Id), (mars.Name, mars.CategoryId));

        // And the manual product no provider entry was about is exactly as it was: no provider id,
        // its own name, its own cost. Duplicating or claiming it is what the refusal existed to
        // prevent, and the review does neither.
        var chocolate = await verify.Products.AsNoTracking().SingleAsync(product => product.Name == "Choc Block");
        Assert.Null(chocolate.ExternalProductId);
        Assert.Equal(2.40m, chocolate.AverageUnitCost);

        // Three products and one category: nothing was duplicated by the import.
        Assert.Equal(3, await verify.Products.CountAsync());
        Assert.Equal(1, await verify.Categories.CountAsync());
    }

    /// <summary>
    /// The honest half of the same criterion: an entry the operator did not decide about is left
    /// alone, and the next review offers it again. A review the operator half finished must not link
    /// the other half by omission.
    /// </summary>
    [Fact]
    public async Task A_provider_entry_nobody_decided_about_is_untouched_and_offered_again()
    {
        await SeedManualCatalogueAsync();

        await using (var db = Scoped())
        {
            var review = await Start(db, Nayax(Products, Groups)).Handle(CancellationToken.None);
            var applied = await Apply(db).Handle(
                new ApplyCatalogueReviewRequest(review.ReviewId, []), CancellationToken.None);

            Assert.Equal((0, 0), (applied.LinkedCount, applied.CreatedCount));
        }

        await using (var verify = Scoped())
        {
            Assert.Equal(2, await verify.Products.CountAsync());
            Assert.Equal(0, await verify.Products.CountAsync(product => product.ExternalProductId != null));
            Assert.Equal(0, await verify.Categories.CountAsync(category => category.ExternalCategoryId != null));
        }

        await using var second = Scoped();
        var next = await Start(second, Nayax(Products, Groups), StartedAt.AddHours(1))
            .Handle(CancellationToken.None);

        Assert.Equal((1, 2), (next.UnlinkedCategoryCount, next.UnlinkedProductCount));
    }

    /// <summary>
    /// A catalogue the business built itself: two products and one category, none of them carrying a
    /// provider id, with stock and a recorded cost of their own.
    /// </summary>
    private async Task SeedManualCatalogueAsync()
    {
        await using var db = Scoped();
        var softDrinks = new Category { Name = "Soft drinks" };
        db.Categories.Add(softDrinks);
        db.Products.Add(new Product
        {
            Name = "Coca-Cola 600ml",
            AverageUnitCost = 1.80m,
            QuantityInStock = 12,
            RestockTo = 0,
            Category = softDrinks,
            CreatedAt = StartedAt,
        });
        db.Products.Add(new Product
        {
            Name = "Choc Block",
            AverageUnitCost = 2.40m,
            QuantityInStock = 5,
            RestockTo = 0,
            CreatedAt = StartedAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task<long> ProductIdAsync(string name)
    {
        await using var db = Scoped();
        return (await db.Products.AsNoTracking().SingleAsync(product => product.Name == name)).Id;
    }

    private async Task<long> CategoryIdAsync(string name)
    {
        await using var db = Scoped();
        return (await db.Categories.AsNoTracking().SingleAsync(category => category.Name == name)).Id;
    }
}
