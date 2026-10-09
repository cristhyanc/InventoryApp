using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite test: proves the projection reads UnitCost/PurchaseDate/Title/Supplier through
/// the actual EF-mapped Purchase/PurchaseItem/Supplier relationships (join-translated SQL), including
/// a Purchase with no supplier, rather than relying on in-memory LINQ semantics that could hide a
/// translation problem.
/// </summary>
public class EfProductPurchasePriceHistoryProviderTests
{
    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    [Fact]
    public async Task Returns_each_purchase_items_unit_cost_joined_to_its_purchase_and_supplier()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Acme Supplies" });
        db.Receipts.Add(new Purchase
        {
            Id = 10,
            Title = "January order",
            SupplierId = 1,
            PurchaseDate = new DateTime(2026, 1, 5),
            Items = { new PurchaseItem { Id = 1, ProductId = 1, UnitCost = 2.00m, Quantity = 10 } }
        });
        await db.SaveChangesAsync();
        var provider = new EfProductPurchasePriceHistoryProvider(db);

        var facts = await provider.GetForProductAsync(1, CancellationToken.None);

        var fact = Assert.Single(facts);
        Assert.Equal(1, fact.PurchaseItemId);
        Assert.Equal(10, fact.PurchaseId);
        Assert.Equal("January order", fact.PurchaseTitle);
        Assert.Equal(new DateTime(2026, 1, 5), fact.PurchaseDate);
        Assert.Equal(1, fact.SupplierId);
        Assert.Equal("Acme Supplies", fact.SupplierName);
        Assert.Equal(2.00m, fact.UnitCost);
    }

    [Fact]
    public async Task Purchase_with_no_supplier_reports_a_null_supplier_rather_than_being_omitted()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.Receipts.Add(new Purchase
        {
            Id = 10,
            Title = "No supplier recorded",
            SupplierId = null,
            PurchaseDate = new DateTime(2026, 1, 5),
            Items = { new PurchaseItem { Id = 1, ProductId = 1, UnitCost = 2.00m, Quantity = 10 } }
        });
        await db.SaveChangesAsync();
        var provider = new EfProductPurchasePriceHistoryProvider(db);

        var facts = await provider.GetForProductAsync(1, CancellationToken.None);

        var fact = Assert.Single(facts);
        Assert.Null(fact.SupplierId);
        Assert.Null(fact.SupplierName);
    }

    [Fact]
    public async Task Another_products_purchase_items_are_not_included()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Products.AddRange(
            new Product { Id = 1, Name = "Coke" },
            new Product { Id = 2, Name = "Chips" });
        db.Receipts.Add(new Purchase
        {
            Id = 10,
            Title = "Mixed order",
            PurchaseDate = new DateTime(2026, 1, 5),
            Items =
            {
                new PurchaseItem { Id = 1, ProductId = 1, UnitCost = 2.00m, Quantity = 10 },
                new PurchaseItem { Id = 2, ProductId = 2, UnitCost = 5.00m, Quantity = 3 }
            }
        });
        await db.SaveChangesAsync();
        var provider = new EfProductPurchasePriceHistoryProvider(db);

        var facts = await provider.GetForProductAsync(1, CancellationToken.None);

        var fact = Assert.Single(facts);
        Assert.Equal(2.00m, fact.UnitCost);
    }

    [Fact]
    public async Task No_purchase_history_returns_an_empty_list()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        await db.SaveChangesAsync();
        var provider = new EfProductPurchasePriceHistoryProvider(db);

        var facts = await provider.GetForProductAsync(1, CancellationToken.None);

        Assert.Empty(facts);
    }

    [Fact]
    public async Task Business_scoped_context_only_sees_its_own_business_purchase_history()
    {
        await using var connection = await CreateSqliteAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        setup.Products.AddRange(
            new Product { Id = 1, Name = "Business A product", BusinessId = 1 },
            new Product { Id = 2, Name = "Business B product", BusinessId = 2 });
        setup.Receipts.AddRange(
            new Purchase
            {
                Id = 10,
                Title = "Business A order",
                BusinessId = 1,
                PurchaseDate = new DateTime(2026, 1, 5),
                Items = { new PurchaseItem { Id = 1, ProductId = 1, UnitCost = 2.00m, Quantity = 10, BusinessId = 1 } }
            },
            new Purchase
            {
                Id = 11,
                Title = "Business B order",
                BusinessId = 2,
                PurchaseDate = new DateTime(2026, 1, 5),
                Items = { new PurchaseItem { Id = 2, ProductId = 2, UnitCost = 999m, Quantity = 1, BusinessId = 2 } }
            });
        await setup.SaveChangesAsync();

        await using var scopedDb = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        var provider = new EfProductPurchasePriceHistoryProvider(scopedDb);

        var businessAFacts = await provider.GetForProductAsync(1, CancellationToken.None);
        var businessBFacts = await provider.GetForProductAsync(2, CancellationToken.None);

        var fact = Assert.Single(businessAFacts);
        Assert.Equal(2.00m, fact.UnitCost);
        Assert.Empty(businessBFacts);
    }
}
