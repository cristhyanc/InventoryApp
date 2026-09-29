using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: proves the bulk projection (issue #207) reads UnitCost/PurchaseDate/
/// Title/Supplier through the actual EF-mapped Purchase/PurchaseItem/Supplier relationships for many
/// products in one query, the same shape <see cref="EfProductPurchasePriceHistoryProvider"/> reads
/// for one product at a time.
/// </summary>
public class EfProductPurchaseCostFactsProviderTests
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
    public async Task Returns_every_requested_products_history_keyed_by_product_id_in_one_call()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Products.AddRange(new Product { Id = 1, Name = "Coke" }, new Product { Id = 2, Name = "Chips" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Acme Supplies" });
        db.Receipts.AddRange(
            new Purchase
            {
                Id = 10,
                Title = "January order",
                SupplierId = 1,
                PurchaseDate = new DateTime(2026, 1, 5),
                Items = { new PurchaseItem { Id = 1, ProductId = 1, UnitCost = 2.00m, Quantity = 10 } }
            },
            new Purchase
            {
                Id = 11,
                Title = "February order",
                PurchaseDate = new DateTime(2026, 2, 5),
                Items = { new PurchaseItem { Id = 2, ProductId = 2, UnitCost = 5.00m, Quantity = 3 } }
            });
        await db.SaveChangesAsync();
        var provider = new EfProductPurchaseCostFactsProvider(db);

        var facts = await provider.GetForProductsAsync(new long[] { 1, 2 }, CancellationToken.None);

        Assert.Equal(2, facts.Count);
        var product1 = Assert.Single(facts[1]);
        Assert.Equal(1, product1.PurchaseItemId);
        Assert.Equal(10, product1.PurchaseId);
        Assert.Equal("January order", product1.PurchaseTitle);
        Assert.Equal(new DateTime(2026, 1, 5), product1.PurchaseDate);
        Assert.Equal(1, product1.SupplierId);
        Assert.Equal("Acme Supplies", product1.SupplierName);
        Assert.Equal(2.00m, product1.UnitCost);

        var product2 = Assert.Single(facts[2]);
        Assert.Null(product2.SupplierId);
        Assert.Null(product2.SupplierName);
        Assert.Equal(5.00m, product2.UnitCost);
    }

    [Fact]
    public async Task A_product_with_no_recorded_purchase_history_is_absent_from_the_result()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        await db.SaveChangesAsync();
        var provider = new EfProductPurchaseCostFactsProvider(db);

        var facts = await provider.GetForProductsAsync(new long[] { 1 }, CancellationToken.None);

        Assert.False(facts.ContainsKey(1));
    }

    [Fact]
    public async Task An_empty_product_id_list_returns_an_empty_result_without_querying()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var provider = new EfProductPurchaseCostFactsProvider(db);

        var facts = await provider.GetForProductsAsync(Array.Empty<long>(), CancellationToken.None);

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
        var provider = new EfProductPurchaseCostFactsProvider(scopedDb);

        var facts = await provider.GetForProductsAsync(new long[] { 1, 2 }, CancellationToken.None);

        var product1 = Assert.Single(facts[1]);
        Assert.Equal(2.00m, product1.UnitCost);
        Assert.False(facts.ContainsKey(2));
    }
}
