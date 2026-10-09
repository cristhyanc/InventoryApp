using Inventory.Application.Imports;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Cross-tenant regression tests for the migrated Nayax product catalogue import (issue #300).
/// The import writes catalogue rows keyed by a remote identifier, so it is exactly the kind of
/// write that must stay inside the caller's business: it reads and writes through the same
/// <c>AppDbContext</c> global query filters and <c>BusinessOwnershipEnforcer</c> every other
/// tenant-owned store uses, never an ad hoc business predicate (AGENTS.md § Tenant ownership).
///
/// <c>Product.Id</c> and <c>Category.Id</c> *are* the Nayax identifiers and are the single-column
/// primary keys, so the Nayax catalogue is not partitioned per business today - the known limit
/// docs/architecture.md § Tenant ownership records for the single-operator Nayax client. These
/// tests pin the behaviour that matters while that limit stands: one business's import never
/// reads, rewrites or deletes another business's catalogue row.
/// </summary>
public class EfNayaxProductCatalogImportStoreTenancyTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private static readonly DateTime ImportedAt = new(2026, 10, 3, 2, 15, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfNayaxProductCatalogImportStoreTenancyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Imported_rows_are_stamped_with_the_importing_business_and_leave_the_other_untouched()
    {
        using (var seed = TestAppDbContext.For(_options, BusinessB))
        {
            seed.Categories.Add(new Category { Id = 20, Name = "B drinks", Description = "B drinks" });
            seed.Products.Add(new Product { Id = 200, Name = "B cola", UnitPrice = 2m, CategoryId = 20 });
            await seed.SaveChangesAsync();
        }

        await ImportAs(BusinessA, new NayaxProductCatalogImport(
            [new ImportedProductCategory(10, "A snacks", "A snacks")],
            [new ImportedProductCatalogEntry(100, "A chips", "Salted", 3.50m, 10)],
            ImportedAt));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var imported = await verify.Products.SingleAsync(product => product.Id == 100);
        Assert.Equal(BusinessA, imported.BusinessId);
        Assert.Equal(BusinessA, (await verify.Categories.SingleAsync(category => category.Id == 10)).BusinessId);

        var other = await verify.Products.SingleAsync(product => product.Id == 200);
        Assert.Equal(BusinessB, other.BusinessId);
        Assert.Equal("B cola", other.Name);
        Assert.Equal(2m, other.UnitPrice);
    }

    /// <summary>
    /// The import's read is tenant-scoped, so another business's product is not a row this import
    /// may update. Because the Nayax product identifier is the shared primary key, the write is
    /// refused by the database rather than silently rewriting the other business's catalogue -
    /// which is the outcome that matters: B's product is exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_product_id_another_business_owns_is_never_rewritten_by_this_business()
    {
        using (var seed = TestAppDbContext.For(_options, BusinessB))
        {
            seed.Products.Add(new Product { Id = 100, Name = "B cola", Description = "B description", UnitPrice = 2m });
            await seed.SaveChangesAsync();
        }

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => ImportAs(BusinessA, new NayaxProductCatalogImport(
            [],
            [new ImportedProductCatalogEntry(100, "A chips", "Salted", 3.50m, null)],
            ImportedAt)));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var other = await verify.Products.SingleAsync(product => product.Id == 100);
        Assert.Equal(BusinessB, other.BusinessId);
        Assert.Equal("B cola", other.Name);
        Assert.Equal("B description", other.Description);
        Assert.Equal(2m, other.UnitPrice);
    }

    private async Task ImportAs(int businessId, NayaxProductCatalogImport import)
    {
        using var db = TestAppDbContext.For(_options, businessId);
        await new EfNayaxProductCatalogImportStore(db).ApplyAsync(import, CancellationToken.None);
    }
}
