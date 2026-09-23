using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: the raw Nayax product identifier/name grouping, card/cash split, and
/// catalogue projection depend on SQL translation, not just in-memory LINQ semantics. Product
/// matching itself is Domain policy and is exercised at the use-case level, not here.
/// </summary>
public class EfProductProfitabilityReportFactsProviderTests
{
    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = new AppDbContext(options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    [Fact]
    public async Task Groups_raw_sales_by_nayax_product_identity_and_reports_card_cash_split()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Categories.Add(new Category { Id = 1, Name = "Drinks" });
        db.Products.Add(new Product { Id = 1, Name = "Water", CategoryId = 1 });
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, NayaxProductId = 1, ProductName = "Water", PaymentMethod = "Credit Card", SettlementValue = 10m, CostOfGoodsSold = 2m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, NayaxProductId = 1, ProductName = "Water", PaymentMethod = "Cash", SettlementValue = 5m, CostOfGoodsSold = 1m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfProductProfitabilityReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        var group = Assert.Single(facts.SaleGroups);
        Assert.Equal(1, group.NayaxProductId);
        Assert.Equal(15m, group.Sales);
        Assert.Equal(3m, group.PartialCostOfGoods);
        Assert.True(group.IsCogsComplete);
        Assert.Equal(10m, group.CardRevenue);
        Assert.Equal(5m, group.CashRevenue);
        var candidate = Assert.Single(facts.Catalogue);
        Assert.Equal(1, candidate.Id);
        Assert.Equal("Water", candidate.Name);
        Assert.Equal("Drinks", candidate.CategoryName);
    }

    [Fact]
    public async Task Different_raw_nayax_product_identities_are_not_merged_by_this_adapter()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Products.Add(new Product { Id = 1, Name = "Water" });
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, NayaxProductId = 1, ProductName = "Water", SettlementValue = 3m, CostOfGoodsSold = 1m, MachineAuthorizationTime = new DateTime(2026, 9, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, NayaxProductId = 999, ProductName = "Water (999)", SettlementValue = 3m, CostOfGoodsSold = 1m, MachineAuthorizationTime = new DateTime(2026, 9, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfProductProfitabilityReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 1), null, CancellationToken.None);

        Assert.Equal(2, facts.SaleGroups.Count);
    }

    [Fact]
    public async Task Machine_filter_scopes_sale_groups_to_the_selected_machine_only()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, NayaxProductId = 1, SettlementValue = 10m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 11, NayaxProductId = 2, SettlementValue = 20m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfProductProfitabilityReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        var group = Assert.Single(facts.SaleGroups);
        Assert.Equal(1, group.NayaxProductId);
    }

    [Fact]
    public async Task Uncosted_transactions_are_reported_without_treating_missing_cogs_as_zero()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, NayaxProductId = 1, SettlementValue = 10m, CostOfGoodsSold = 4m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, NayaxProductId = 1, SettlementValue = 10m, CostOfGoodsSold = null, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfProductProfitabilityReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        var group = Assert.Single(facts.SaleGroups);
        Assert.False(group.IsCogsComplete);
        Assert.Equal(1, group.UncostedTransactionCount);
        Assert.Equal(10m, group.UncostedSalesAmount);
        Assert.Equal(4m, group.PartialCostOfGoods);
    }
}
