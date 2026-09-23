using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: bookkeeping's card/cash split, COGS completeness, and date-range
/// filtering depend on SQL translation and ordering, not just in-memory LINQ semantics.
/// </summary>
public class EfBookkeepingReportFactsProviderTests
{
    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = new AppDbContext(options);
        await setup.Database.EnsureCreatedAsync();
        return (connection, options);
    }

    private static ISiteCommissionService EmptyCommissions()
    {
        var mock = new Mock<ISiteCommissionService>();
        mock.Setup(x => x.GetReportAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, long? _, CancellationToken _) => new SiteCommissionReportDto(from, to, []));
        return mock.Object;
    }

    [Fact]
    public async Task Splits_card_and_cash_sales_and_reports_cogs_completeness()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 1,
                MachineID = 10,
                MachineName = "Machine A",
                SettlementValue = 10m,
                PaymentMethod = "Credit Card",
                MachineAuthorizationTime = new DateTime(2025, 8, 1),
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                CostOfGoodsSold = 4m
            },
            new NayaxSales
            {
                TransactionID = 2,
                MachineID = 10,
                MachineName = "Machine A",
                SettlementValue = 5m,
                PaymentMethod = "Cash",
                MachineAuthorizationTime = new DateTime(2025, 8, 1),
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                CostOfGoodsSold = null
            });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, new NayaxProcessingFeeService(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(15m, facts.GrossSales);
        Assert.Equal(10m, facts.CardSales);
        Assert.Equal(1, facts.CardTransactions);
        Assert.Equal(5m, facts.CashSales);
        Assert.Equal(1, facts.CashTransactions);
        Assert.False(facts.IsCogsComplete);
        Assert.Equal(1, facts.UncostedTransactionCount);
        Assert.Equal(5m, facts.UncostedSalesAmount);
        Assert.Equal(4m, facts.PartialCostOfGoods);
    }

    [Fact]
    public async Task Date_range_is_inclusive_of_both_boundary_days_and_excludes_the_day_after()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 1m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 7, 31, 23, 59, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 2m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 2, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 8m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 3, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, new NayaxProcessingFeeService(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 2), null, CancellationToken.None);

        Assert.Equal(6m, facts.GrossSales);
    }

    [Fact]
    public async Task Machine_filter_scopes_sales_to_the_selected_machine_only()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 20, SettlementValue = 30m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, new NayaxProcessingFeeService(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        Assert.Equal(10m, facts.GrossSales);
    }

    [Fact]
    public async Task Non_completed_sales_are_excluded_from_totals_and_cogs_completeness()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 99m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.PendingSettlementNotFinal, CostOfGoodsSold = null });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, new NayaxProcessingFeeService(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(10m, facts.GrossSales);
        Assert.True(facts.IsCogsComplete);
    }

    [Fact]
    public async Task Receipt_delivery_and_package_costs_are_zero_when_no_receipts_are_in_range()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var provider = new EfBookkeepingReportFactsProvider(db, new NayaxProcessingFeeService(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(0m, facts.ReceiptDeliveryCost);
        Assert.Equal(0m, facts.ReceiptPackageCost);
        Assert.Equal(0m, facts.GrossSales);
        Assert.True(facts.IsCogsComplete);
    }
}
