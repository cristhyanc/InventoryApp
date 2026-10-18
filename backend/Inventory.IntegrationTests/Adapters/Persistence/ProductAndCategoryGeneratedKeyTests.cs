using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The schema half of issue #735 (A4): on a database built by the migrations - the one Production
/// applies at startup - <c>Products.Id</c> and <c>Categories.Id</c> are assigned by SQLite, and the
/// ids already stored are kept exactly as they are.
///
/// <para>This is why that slice needs <strong>no migration</strong>, which is a claim worth proving
/// rather than asserting in a pull request. Both tables have declared their key
/// <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> since the schema was created, and the EF model has
/// always had it <c>ValueGeneratedOnAdd</c> (<c>ProviderIdIsNeverALocalKeyTests</c> pins the model
/// side) - manual product creation has relied on that all along. What issue #732's and #759's table
/// rebuilds could have quietly changed is the declaration, because SQLite cannot add a constraint in
/// place and EF rebuilds the table to do it; so the generation is read back from the migrated schema
/// here rather than taken on trust.</para>
///
/// <para>And a generated key must not disturb the ids a real database already holds: every existing
/// product id, category id and the foreign keys pointing at them stay valid, because SQLite assigns
/// above the highest key the table ever held.</para>
/// </summary>
public class ProductAndCategoryGeneratedKeyTests : IDisposable
{
    private const int BusinessId = 1;

    /// <summary>
    /// Ids in the shape the live data has: a product and a category keyed by the Nayax ids the
    /// #732 backfill recorded, which are far above the keys an empty table would generate.
    /// </summary>
    private const long StoredProductId = 5001;
    private const long StoredCategoryId = 55;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ProductAndCategoryGeneratedKeyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_migrated_products_and_categories_tables_assign_their_own_keys()
    {
        await MigrateAsync();

        foreach (var table in new[] { "Products", "Categories" })
        {
            var createTable = await MigrationSchemaProbe.CreateTableSqlAsync(_connection, table);
            Assert.Contains("PRIMARY KEY AUTOINCREMENT", createTable, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A row inserted with no key gets one of its own, and the rows that were already stored keep
    /// theirs - including the category a stored product points at.
    /// </summary>
    [Fact]
    public async Task A_row_inserted_with_no_key_is_given_one_and_the_stored_ids_are_untouched()
    {
        await MigrateAsync();
        await SeedAsync();

        long generatedProductId;
        long generatedCategoryId;
        await using (var insert = TestAppDbContext.For(_options, BusinessId))
        {
            var category = new Category { Name = "Imported category" };
            insert.Categories.Add(category);
            await insert.SaveChangesAsync();

            var product = new Product { Name = "Imported product", CategoryId = category.Id };
            insert.Products.Add(product);
            await insert.SaveChangesAsync();

            generatedProductId = product.Id;
            generatedCategoryId = category.Id;
        }

        Assert.True(generatedProductId > StoredProductId, $"Generated product id {generatedProductId}");
        Assert.True(generatedCategoryId > StoredCategoryId, $"Generated category id {generatedCategoryId}");

        await using var verify = TestAppDbContext.For(_options, BusinessId);
        var stored = await verify.Products.AsNoTracking().SingleAsync(product => product.Id == StoredProductId);
        Assert.Equal(("Stored product", "100", StoredCategoryId), (stored.Name, stored.ExternalProductId, stored.CategoryId));
        var storedCategory = await verify.Categories.AsNoTracking().SingleAsync(category => category.Id == StoredCategoryId);
        Assert.Equal(("Stored category", "10"), (storedCategory.Name, storedCategory.ExternalCategoryId));

        var imported = await verify.Products.AsNoTracking().SingleAsync(product => product.Id == generatedProductId);
        Assert.Equal(generatedCategoryId, imported.CategoryId);
    }

    private async Task MigrateAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        await db.Database.MigrateAsync();
    }

    private async Task SeedAsync()
    {
        await using (var setup = TestAppDbContext.Unrestricted(_options))
        {
            setup.Businesses.Add(new Business
            {
                Id = BusinessId,
                Name = "Vending A",
                Status = BusinessStatus.Active,
                SalesProvider = ExternalProvider.Nayax,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        await using var seed = TestAppDbContext.For(_options, BusinessId);
        seed.Categories.Add(new Category
        {
            Id = StoredCategoryId,
            ExternalCategoryId = NayaxExternalId.From(10),
            Name = "Stored category",
        });
        seed.Products.Add(new Product
        {
            Id = StoredProductId,
            ExternalProductId = NayaxExternalId.From(100),
            Name = "Stored product",
            CategoryId = StoredCategoryId,
        });
        await seed.SaveChangesAsync();
    }
}
