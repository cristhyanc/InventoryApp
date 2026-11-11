using System.Data.Common;
using Inventory.Domain.Purchases;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests for the receipt-scan pack-size lookup (issue #1165): that it names the
/// same purchase line the price-comparison path names - including for purchases on the same day,
/// where the tie-break decides - that it reads only the matched supplier's and the caller's own
/// business's purchases, and that it answers every requested product in one statement.
/// </summary>
public class EfReceiptScanPurchaseHistoryStoreTests
{
    private const int MatchedSupplierId = 1;
    private const int OtherSupplierId = 2;

    private static DbContextOptions<AppDbContext> OptionsFor(
        SqliteConnection connection, DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    private static Purchase Purchase(
        int id,
        int? supplierId,
        DateTime purchaseDate,
        int businessId,
        params PurchaseItem[] items)
    {
        var purchase = new Purchase
        {
            Id = id,
            Title = $"Order {id}",
            SupplierId = supplierId,
            PurchaseDate = purchaseDate,
            BusinessId = businessId
        };

        foreach (var item in items)
        {
            item.BusinessId = businessId;
            purchase.Items.Add(item);
        }

        return purchase;
    }

    private static PurchaseItem Line(int id, long productId, decimal unitCost) =>
        new() { Id = id, ProductId = productId, UnitCost = unitCost, Quantity = 1 };

    [Fact]
    public async Task Same_day_purchases_choose_the_line_the_price_comparison_path_also_calls_latest()
    {
        var sameDay = new DateTime(2026, 6, 5);
        await using var connection = await CreateSqliteAsync();
        await using var setup = TestAppDbContext.Unrestricted(OptionsFor(connection));
        setup.Products.Add(new Product { Id = 1, Name = "CC's Tasty Cheese 45g", BusinessId = 1 });
        setup.Suppliers.Add(new Supplier { Id = MatchedSupplierId, Name = "JB Metro", BusinessId = 1 });
        // Three lines for one product from one supplier on one day, added in an order that would give
        // a different answer if arrival order decided, plus an earlier day with a higher id still.
        setup.Receipts.AddRange(
            Purchase(10, MatchedSupplierId, sameDay, 1, Line(1, 1, 1.0000m)),
            Purchase(11, MatchedSupplierId, sameDay, 1, Line(3, 1, 1.2067m)),
            Purchase(12, MatchedSupplierId, sameDay, 1, Line(2, 1, 9.9900m)),
            Purchase(13, MatchedSupplierId, new DateTime(2026, 1, 5), 1, Line(4, 1, 7.7700m)));
        await setup.SaveChangesAsync();

        await using var scopedDb = TestAppDbContext.For(OptionsFor(connection), businessId: 1);
        var store = new EfReceiptScanPurchaseHistoryStore(scopedDb);
        var lastPurchase = Assert.Single(
            await store.ListLatestAsync(MatchedSupplierId, new long[] { 1 }, CancellationToken.None));

        // The same-day tie-break takes the highest purchase-item id, which is the $1.2067 line.
        Assert.Equal(1.2067m, lastPurchase.UnitCost);
        Assert.Equal(new DateOnly(2026, 6, 5), lastPurchase.PurchaseDate);

        // And that is the line the price-comparison path calls Latest over the same history.
        await using var comparisonDb = TestAppDbContext.For(OptionsFor(connection), businessId: 1);
        var facts = await new EfProductPurchaseCostFactsProvider(comparisonDb)
            .GetForProductsAsync(new long[] { 1 }, CancellationToken.None);
        var fromThatSupplier = facts[1].Where(entry => entry.SupplierId == MatchedSupplierId).ToList();
        var latest = SupplierPriceComparisonPolicy.Evaluate(fromThatSupplier).Latest;

        Assert.Equal(latest!.Value.UnitCost, lastPurchase.UnitCost);
        Assert.Equal(DateOnly.FromDateTime(latest.Value.PurchaseDate), lastPurchase.PurchaseDate);
    }

    [Fact]
    public async Task A_later_purchase_from_another_supplier_is_not_read()
    {
        await using var connection = await CreateSqliteAsync();
        await using var setup = TestAppDbContext.Unrestricted(OptionsFor(connection));
        setup.Products.Add(new Product { Id = 1, Name = "CC's Tasty Cheese 45g", BusinessId = 1 });
        setup.Suppliers.AddRange(
            new Supplier { Id = MatchedSupplierId, Name = "JB Metro", BusinessId = 1 },
            new Supplier { Id = OtherSupplierId, Name = "Costco", BusinessId = 1 });
        setup.Receipts.AddRange(
            Purchase(10, MatchedSupplierId, new DateTime(2026, 1, 5), 1, Line(1, 1, 1.2067m)),
            // Later, higher id, same product, and a purchase with no supplier at all: neither is this
            // supplier's price history.
            Purchase(11, OtherSupplierId, new DateTime(2026, 6, 5), 1, Line(2, 1, 9.9900m)),
            Purchase(12, null, new DateTime(2026, 7, 5), 1, Line(3, 1, 8.8800m)));
        await setup.SaveChangesAsync();

        await using var scopedDb = TestAppDbContext.For(OptionsFor(connection), businessId: 1);
        var store = new EfReceiptScanPurchaseHistoryStore(scopedDb);

        var lastPurchase = Assert.Single(
            await store.ListLatestAsync(MatchedSupplierId, new long[] { 1 }, CancellationToken.None));

        Assert.Equal(1.2067m, lastPurchase.UnitCost);
        Assert.Equal(new DateOnly(2026, 1, 5), lastPurchase.PurchaseDate);
    }

    [Fact]
    public async Task Only_the_business_the_context_resolved_has_its_purchases_read()
    {
        await using var connection = await CreateSqliteAsync();
        await using var setup = TestAppDbContext.Unrestricted(OptionsFor(connection));
        setup.Products.AddRange(
            new Product { Id = 1, Name = "Business A product", BusinessId = 1 },
            new Product { Id = 2, Name = "Business B product", BusinessId = 2 });
        setup.Suppliers.AddRange(
            new Supplier { Id = MatchedSupplierId, Name = "JB Metro", BusinessId = 1 },
            // The other business's own supplier of the same name; a supplier row belongs to one
            // business, which is why its purchase cannot name business 1's supplier id instead.
            new Supplier { Id = OtherSupplierId, Name = "JB Metro", BusinessId = 2 });
        setup.Receipts.AddRange(
            Purchase(10, MatchedSupplierId, new DateTime(2026, 1, 5), 1, Line(1, 1, 1.2067m)),
            Purchase(11, OtherSupplierId, new DateTime(2026, 6, 5), 2, Line(2, 2, 999m)));
        await setup.SaveChangesAsync();

        await using var scopedDb = TestAppDbContext.For(OptionsFor(connection), businessId: 1);
        var lastPurchases = await new EfReceiptScanPurchaseHistoryStore(scopedDb).ListLatestAsync(
            MatchedSupplierId, new long[] { 1, 2 }, CancellationToken.None);

        var own = Assert.Single(lastPurchases);
        Assert.Equal(1, own.ProductId);
        Assert.Equal(1.2067m, own.UnitCost);
        Assert.DoesNotContain(lastPurchases, row => row.ProductId == 2);

        // And the other way round: asking as the other business for business 1's own product and
        // supplier id reads nothing, because the business is never a parameter of this read.
        await using var otherDb = TestAppDbContext.For(OptionsFor(connection), businessId: 2);
        Assert.Empty(await new EfReceiptScanPurchaseHistoryStore(otherDb).ListLatestAsync(
            MatchedSupplierId, new long[] { 1, 2 }, CancellationToken.None));
    }

    [Fact]
    public async Task Every_requested_product_is_answered_by_one_statement()
    {
        await using var connection = await CreateSqliteAsync();
        await using var setup = TestAppDbContext.Unrestricted(OptionsFor(connection));
        setup.Products.AddRange(
            new Product { Id = 1, Name = "Cheese", BusinessId = 1 },
            new Product { Id = 2, Name = "Chips", BusinessId = 1 },
            new Product { Id = 3, Name = "Coke", BusinessId = 1 },
            new Product { Id = 4, Name = "Never bought from this supplier", BusinessId = 1 });
        setup.Suppliers.Add(new Supplier { Id = MatchedSupplierId, Name = "JB Metro", BusinessId = 1 });
        setup.Receipts.AddRange(
            Purchase(
                10, MatchedSupplierId, new DateTime(2026, 1, 5), 1,
                Line(1, 1, 1.00m), Line(2, 2, 2.00m), Line(3, 3, 3.00m)),
            Purchase(
                11, MatchedSupplierId, new DateTime(2026, 6, 5), 1,
                Line(4, 1, 1.2067m), Line(5, 2, 2.50m)));
        await setup.SaveChangesAsync();

        var reads = new List<string>();
        await using var scopedDb = TestAppDbContext.For(
            OptionsFor(connection, new ReadCountingInterceptor(reads)), businessId: 1);
        var store = new EfReceiptScanPurchaseHistoryStore(scopedDb);

        var lastPurchases = await store.ListLatestAsync(
            MatchedSupplierId, new long[] { 1, 2, 3, 4 }, CancellationToken.None);

        Assert.Single(reads);
        Assert.Equal(3, lastPurchases.Count);
        Assert.Equal(1.2067m, lastPurchases.Single(row => row.ProductId == 1).UnitCost);
        Assert.Equal(2.50m, lastPurchases.Single(row => row.ProductId == 2).UnitCost);
        Assert.Equal(3.00m, lastPurchases.Single(row => row.ProductId == 3).UnitCost);
        Assert.DoesNotContain(lastPurchases, row => row.ProductId == 4);
    }

    [Fact]
    public async Task No_requested_product_reads_nothing_at_all()
    {
        await using var connection = await CreateSqliteAsync();
        var reads = new List<string>();
        await using var scopedDb = TestAppDbContext.For(
            OptionsFor(connection, new ReadCountingInterceptor(reads)), businessId: 1);
        var store = new EfReceiptScanPurchaseHistoryStore(scopedDb);

        var lastPurchases = await store.ListLatestAsync(
            MatchedSupplierId, Array.Empty<long>(), CancellationToken.None);

        Assert.Empty(lastPurchases);
        Assert.Empty(reads);
    }

    private sealed class ReadCountingInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _reads;

        public ReadCountingInterceptor(List<string> reads) => _reads = reads;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            _reads.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _reads.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
