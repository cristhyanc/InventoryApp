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
/// Relational SQLite tests: per-machine card/cash split, COGS completeness, and per-machine
/// operating-expense grouping depend on SQL translation and ordering, not just in-memory LINQ
/// semantics.
/// </summary>
public class EfMachineProfitabilityReportFactsProviderTests
{
    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    private static ISiteCommissionService EmptyCommissions()
    {
        var mock = new Mock<ISiteCommissionService>();
        mock.Setup(x => x.GetReportAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, long? _, CancellationToken _) => new SiteCommissionReportDto(from, to, []));
        return mock.Object;
    }

    [Fact]
    public async Task Splits_card_and_cash_sales_per_machine_and_reports_cogs_completeness()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, MachineName = "Machine A", SettlementValue = 10m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m },
            new NayaxSales { TransactionID = 2, MachineID = 10, MachineName = "Machine A", SettlementValue = 5m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = null },
            new NayaxSales { TransactionID = 3, MachineID = 11, MachineName = "Machine B", SettlementValue = 7m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 3m });
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(db, new NayaxProcessingFeeService(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(2, facts.Machines.Count);
        var machineA = Assert.Single(facts.Machines, x => x.MachineId == 10);
        Assert.Equal(15m, machineA.Sales);
        Assert.Equal(10m, machineA.CardSales);
        Assert.Equal(5m, machineA.CashSales);
        Assert.False(machineA.IsCogsComplete);
        Assert.Equal(1, machineA.UncostedTransactionCount);
        Assert.Equal(5m, machineA.UncostedSalesAmount);
        var machineB = Assert.Single(facts.Machines, x => x.MachineId == 11);
        Assert.True(machineB.IsCogsComplete);
        Assert.Equal(3m, machineB.PartialCostOfGoods);
    }

    [Fact]
    public async Task Machine_filter_scopes_facts_to_the_selected_machine_only()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 11, SettlementValue = 20m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(db, new NayaxProcessingFeeService(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        var machine = Assert.Single(facts.Machines);
        Assert.Equal(10, machine.MachineId);
        Assert.Equal(10m, machine.Sales);
    }

    [Fact]
    public async Task Operating_expenses_are_grouped_by_machine_and_unassigned_expenses_are_excluded()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        db.NayaxSales.Add(new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = date, TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m });
        db.OperatingExpenses.AddRange(
            new OperatingExpense { ExpenseDate = date, MachineId = 10, TotalAmount = 6m },
            new OperatingExpense { ExpenseDate = date, TotalAmount = 20m });
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(db, new NayaxProcessingFeeService(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        var machine = Assert.Single(facts.Machines);
        Assert.Equal(6m, machine.OperatingExpenses);
    }

    [Fact]
    public async Task Missing_fee_rate_transactions_are_aggregated_across_machines()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Credit Card", MachineAuthorizationTime = date, TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m },
            new NayaxSales { TransactionID = 2, MachineID = 11, SettlementValue = 10m, PaymentMethod = "Credit Card", MachineAuthorizationTime = date, TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m });
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(db, new NayaxProcessingFeeService(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        Assert.Equal(2, facts.MissingFeeRateTransactionCount);
        Assert.All(facts.Machines, machine => Assert.True(machine.ProcessingFees.HasMissingRates));
    }
}
