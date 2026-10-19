using Inventory.Application.Categories;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

public class EfCategoryStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    [Fact]
    public async Task ListOrderedByNameAsync_returns_categories_ordered_by_name()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        db.Categories.AddRange(
            new Category { Name = "Zebra" },
            new Category { Name = "Apple", Description = "Fruit" });
        await db.SaveChangesAsync();
        var store = new EfCategoryStore(db);

        var categories = await store.ListOrderedByNameAsync(CancellationToken.None);

        Assert.Equal(new[] { "Apple", "Zebra" }, categories.Select(c => c.Name));
        Assert.Equal("Fruit", categories.Single(c => c.Name == "Apple").Description);
    }

    [Fact]
    public async Task FindByIdAsync_returns_existing_category()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long id;
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var category = new Category { Name = "Snacks" };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
            id = category.Id;
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var store = new EfCategoryStore(verify);

        var found = await store.FindByIdAsync(id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("Snacks", found!.Name);
    }

    [Fact]
    public async Task FindByIdAsync_returns_null_for_missing_category()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfCategoryStore(db);

        var found = await store.FindByIdAsync(999, CancellationToken.None);

        Assert.Null(found);
    }

    /// <summary>
    /// Category name has no uniqueness constraint today (AppDbContext indexes but does not
    /// enforce uniqueness on Category.Name) - this locks in that existing, deliberately
    /// permissive behavior against the real relational schema.
    /// </summary>
    [Fact]
    public async Task Two_categories_with_the_same_name_are_both_persisted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        db.Categories.AddRange(
            new Category { Name = "Same Name" },
            new Category { Name = "Same Name" });
        await db.SaveChangesAsync();
        var store = new EfCategoryStore(db);

        var categories = await store.ListOrderedByNameAsync(CancellationToken.None);

        Assert.Equal(2, categories.Count);
    }

    /// <summary>
    /// The write surface issue #736 added: a category is created, renamed, given a description and
    /// deleted again while no product uses it.
    /// </summary>
    [Fact]
    public async Task A_category_is_created_renamed_and_deleted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long categoryId;

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var created = await new EfCategoryStore(db).CreateAsync(
                new CategoryWriteFields("  Drinks  ", "Cold"), CancellationToken.None);

            Assert.Equal(CategoryWriteOutcome.Succeeded, created.Outcome);
            categoryId = created.CategoryId!.Value;
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            // The name is stored trimmed: trailing space is not a second category.
            var stored = await new EfCategoryStore(db).FindByIdAsync(categoryId, CancellationToken.None);
            Assert.Equal(("Drinks", "Cold", false), (stored!.Name, stored.Description, stored.IsProviderManaged));
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var renamed = await new EfCategoryStore(db).UpdateAsync(
                categoryId, new CategoryWriteFields("Cold drinks", "Chilled"), CancellationToken.None);

            Assert.Equal(CategoryWriteOutcome.Succeeded, renamed.Outcome);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var deleted = await new EfCategoryStore(db).DeleteAsync(categoryId, CancellationToken.None);

            Assert.Equal(CategoryWriteOutcome.Succeeded, deleted.Outcome);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        Assert.Empty(await verify.Categories.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The name is unique within the business, compared without case or surrounding space - "Drinks"
    /// and "drinks" are the same category to the operator typing the second one. The refusal quotes
    /// the name and nothing is written.
    /// </summary>
    [Theory]
    [InlineData("drinks")]
    [InlineData("DRINKS")]
    [InlineData("  Drinks ")]
    public async Task A_duplicate_name_is_refused_on_create(string duplicate)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Categories.Add(new Category { Name = "Drinks" });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await new EfCategoryStore(db).CreateAsync(
            new CategoryWriteFields(duplicate, null), CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.Refused, attempt.Outcome);
        Assert.Contains("already has a category called", attempt.Refusal!, StringComparison.Ordinal);
        Assert.Equal(1, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task A_duplicate_name_is_refused_on_rename_but_a_category_may_keep_its_own_name()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long snacksId;
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            var snacks = new Category { Name = "Snacks" };
            seed.Categories.AddRange(new Category { Name = "Drinks" }, snacks);
            await seed.SaveChangesAsync();
            snacksId = snacks.Id;
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfCategoryStore(db);

        var refused = await store.UpdateAsync(
            snacksId, new CategoryWriteFields("Drinks", null), CancellationToken.None);
        Assert.Equal(CategoryWriteOutcome.Refused, refused.Outcome);

        // Submitting the stored name is not a rename, so the description is still editable.
        var accepted = await store.UpdateAsync(
            snacksId, new CategoryWriteFields("Snacks", "Salty"), CancellationToken.None);
        Assert.Equal(CategoryWriteOutcome.Succeeded, accepted.Outcome);

        var stored = await db.Categories.AsNoTracking().SingleAsync(category => category.Id == snacksId);
        Assert.Equal(("Snacks", "Salty"), (stored.Name, stored.Description));
    }

    /// <summary>
    /// Uniqueness is per business (AGENTS.md § Tenant ownership and data isolation): another
    /// business's "Drinks" is not a clash, and this business's create succeeds beside it.
    /// </summary>
    [Fact]
    public async Task Another_businesss_category_name_is_not_a_duplicate()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessB))
        {
            seed.Categories.Add(new Category { Name = "Drinks" });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await new EfCategoryStore(db).CreateAsync(
            new CategoryWriteFields("Drinks", null), CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.Succeeded, attempt.Outcome);
    }

    /// <summary>
    /// A category the business's sales provider named keeps its name - the next catalogue import
    /// would overwrite a local rename - and keeps its description editable, which is what the
    /// provider-managed field list on the response says.
    /// </summary>
    [Fact]
    public async Task A_provider_managed_category_may_not_be_renamed()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long categoryId;
        await using (var seed = TestAppDbContext.Unrestricted(options))
        {
            seed.Businesses.Add(new Business
            {
                Id = BusinessA,
                Name = "Vending A",
                Status = BusinessStatus.Active,
                SalesProvider = ExternalProvider.Nayax,
                CreatedAtUtc = DateTime.UtcNow,
            });
            var category = new Category
            {
                BusinessId = BusinessA,
                Name = "Nayax Drinks",
                ExternalCategoryId = "42",
            };
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            categoryId = category.Id;
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfCategoryStore(db);

        var refused = await store.UpdateAsync(
            categoryId, new CategoryWriteFields("My Drinks", null), CancellationToken.None);
        Assert.Equal(CategoryWriteOutcome.Refused, refused.Outcome);
        Assert.Equal(
            ProviderManagedCatalogueFields.CategoryNameRefusal(ExternalProvider.Nayax),
            refused.Refusal);

        var accepted = await store.UpdateAsync(
            categoryId, new CategoryWriteFields("Nayax Drinks", "Cold"), CancellationToken.None);
        Assert.Equal(CategoryWriteOutcome.Succeeded, accepted.Outcome);

        var stored = await db.Categories.AsNoTracking().SingleAsync(category => category.Id == categoryId);
        Assert.Equal(("Nayax Drinks", "Cold"), (stored.Name, stored.Description));
        Assert.True((await store.FindByIdAsync(categoryId, CancellationToken.None))!.IsProviderManaged);
    }

    /// <summary>
    /// A category products still use is not deleted: <c>Product.CategoryId</c> is set to null when
    /// its category disappears, so the products would be quietly uncategorised. The refusal says how
    /// many use it, and both the category and the products are untouched.
    /// </summary>
    [Fact]
    public async Task A_category_products_use_may_not_be_deleted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long categoryId;
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            var category = new Category { Name = "Drinks" };
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            categoryId = category.Id;
            seed.Products.AddRange(
                new Product { Name = "Coke", CategoryId = categoryId },
                new Product { Name = "Pepsi", CategoryId = categoryId });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await new EfCategoryStore(db).DeleteAsync(categoryId, CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.Refused, attempt.Outcome);
        Assert.Contains("2 products use", attempt.Refusal!, StringComparison.Ordinal);
        Assert.True(await db.Categories.AnyAsync(category => category.Id == categoryId));
        Assert.Equal(2, await db.Products.CountAsync(product => product.CategoryId == categoryId));
    }

    [Fact]
    public async Task Writing_an_unknown_category_reports_not_found()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfCategoryStore(db);

        Assert.Equal(
            CategoryWriteOutcome.NotFound,
            (await store.UpdateAsync(999, new CategoryWriteFields("Drinks", null), CancellationToken.None)).Outcome);
        Assert.Equal(
            CategoryWriteOutcome.NotFound,
            (await store.DeleteAsync(999, CancellationToken.None)).Outcome);
    }

    /// <summary>
    /// The write surface is scoped by the central tenant filter too: another business's category is
    /// not found, and nothing about it changes.
    /// </summary>
    [Fact]
    public async Task Another_businesss_category_cannot_be_renamed_or_deleted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long otherBusinessCategoryId;
        await using (var seed = TestAppDbContext.For(options, BusinessB))
        {
            var category = new Category { Name = "B Category" };
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            otherBusinessCategoryId = category.Id;
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var store = new EfCategoryStore(db);

            Assert.Equal(
                CategoryWriteOutcome.NotFound,
                (await store.UpdateAsync(
                    otherBusinessCategoryId,
                    new CategoryWriteFields("Renamed", null),
                    CancellationToken.None)).Outcome);
            Assert.Equal(
                CategoryWriteOutcome.NotFound,
                (await store.DeleteAsync(otherBusinessCategoryId, CancellationToken.None)).Outcome);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var untouched = await verify.Categories.AsNoTracking()
            .SingleAsync(category => category.Id == otherBusinessCategoryId);
        Assert.Equal("B Category", untouched.Name);
    }

    /// <summary>
    /// The central tenant filter (issue #64), exercised through this adapter: a category owned
    /// by another business is invisible to both the list and by-id queries.
    /// </summary>
    [Fact]
    public async Task Another_business_category_is_invisible_to_list_and_find()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long otherBusinessCategoryId;
        await using (var seed = TestAppDbContext.For(options, BusinessB))
        {
            var category = new Category { Name = "B Category" };
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            otherBusinessCategoryId = category.Id;
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfCategoryStore(db);

        Assert.Empty(await store.ListOrderedByNameAsync(CancellationToken.None));
        Assert.Null(await store.FindByIdAsync(otherBusinessCategoryId, CancellationToken.None));
    }
}
