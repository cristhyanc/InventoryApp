using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Application.Commissions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
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

    private static IGetSiteCommissionReport EmptyCommissions()
    {
        var mock = new Mock<IGetSiteCommissionReport>();
        mock.Setup(x => x.Handle(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, long? _, CancellationToken _) => new SiteCommissionReport(from, to, []));
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
        var provider = new EfMachineProfitabilityReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

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
        var provider = new EfMachineProfitabilityReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        var machine = Assert.Single(facts.Machines);
        Assert.Equal(10, machine.MachineId);
        Assert.Equal(10m, machine.Sales);
    }

    /// <summary>
    /// Since issue #780 an expense records the local machine row, not the provider's machine
    /// number, and this report's machine keys are deliberately still the provider's numbers. So the
    /// grouping has to go through <c>LocalMachine.ExternalMachineId</c> - and the total has to be
    /// the same as it was before the repoint, which is what the <c>6m</c> here is: the machine's
    /// own expense, and not the whole-business one beside it.
    /// </summary>
    [Fact]
    public async Task Operating_expenses_are_grouped_by_machine_and_unassigned_expenses_are_excluded()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        db.Businesses.Add(new Business
        {
            Id = 1,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
            SalesProvider = ExternalProvider.Nayax,
        });
        // The local machine the AddMachines backfill would have created for provider machine 10,
        // with a local key that is deliberately not that number.
        var machine10 = new LocalMachine { BusinessId = 1, Name = "Machine 10", ExternalMachineId = "10" };
        db.Machines.Add(machine10);
        db.NayaxSales.Add(new NayaxSales { BusinessId = 1, TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = date, TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m });
        await db.SaveChangesAsync();
        db.OperatingExpenses.AddRange(
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, MachineId = machine10.Id, TotalAmount = 6m },
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, TotalAmount = 20m });
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        var machine = Assert.Single(facts.Machines);
        Assert.Equal(10, machine.MachineId);
        Assert.Equal(6m, machine.OperatingExpenses);
    }

    /// <summary>
    /// A machine the provider reports that this business has recorded no machine for contributes no
    /// operating expenses - and must not pick up another machine's, which is what grouping by a raw
    /// number would risk once the column holds local keys.
    /// </summary>
    [Fact]
    public async Task A_machine_with_no_local_record_reports_no_operating_expenses()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        db.Businesses.Add(new Business
        {
            Id = 1,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
            SalesProvider = ExternalProvider.Nayax,
        });
        var machine10 = new LocalMachine { BusinessId = 1, Name = "Machine 10", ExternalMachineId = "10" };
        db.Machines.Add(machine10);
        db.NayaxSales.AddRange(
            new NayaxSales { BusinessId = 1, TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = date, TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m },
            new NayaxSales { BusinessId = 1, TransactionID = 2, MachineID = 11, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = date, TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m });
        await db.SaveChangesAsync();
        db.OperatingExpenses.Add(
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, MachineId = machine10.Id, TotalAmount = 6m });
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        Assert.Equal(6m, Assert.Single(facts.Machines, x => x.MachineId == 10).OperatingExpenses);
        Assert.Equal(0m, Assert.Single(facts.Machines, x => x.MachineId == 11).OperatingExpenses);
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
        var provider = new EfMachineProfitabilityReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        Assert.Equal(2, facts.MissingFeeRateTransactionCount);
        Assert.All(facts.Machines, machine => Assert.True(machine.ProcessingFees.HasMissingRates));
    }
}
