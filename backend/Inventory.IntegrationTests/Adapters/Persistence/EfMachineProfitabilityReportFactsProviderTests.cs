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
        mock.Setup(x => x.HandleBusinessPeriod(It.IsAny<SiteCommissionBusinessPeriod>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteCommissionBusinessPeriod period, long? _, CancellationToken _) =>
                new SiteCommissionReport(period.FirstBusinessDate, period.LastBusinessDate, []));
        return mock.Object;
    }

    /// <summary>
    /// A commission report with one site holding exactly <paramref name="machines"/>, so a test can
    /// prove what each machine row is charged rather than only that nothing is.
    /// </summary>
    private static IGetSiteCommissionReport CommissionsFor(decimal rate, params (long MachineId, decimal Due)[] machines)
    {
        var mock = new Mock<IGetSiteCommissionReport>();
        mock.Setup(x => x.HandleBusinessPeriod(It.IsAny<SiteCommissionBusinessPeriod>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteCommissionBusinessPeriod period, long? _, CancellationToken _) =>
                new SiteCommissionReport(period.FirstBusinessDate, period.LastBusinessDate,
                [
                    new SiteCommissionReportRow(91, "Site 91", period.FirstBusinessDate, period.LastBusinessDate,
                        CommissionFrequency.Monthly, CommissionBasis.GrossSales, 0m, 0m, 0m, 0m, rate,
                        machines.Sum(machine => machine.Due),
                        machines.Select(machine => new SiteCommissionMachineReportRow(
                            machine.MachineId, $"Machine {machine.MachineId}", 0, 0m, 0m, 0m, 0m, machine.Due)).ToList(),
                        [])
                ]));
        return mock.Object;
    }

    /// <summary>The business whose machines carry Nayax's own numbers, as the expense tests seed it.</summary>
    private static void SeedNayaxBusiness(AppDbContext db) =>
        db.Businesses.Add(new Business
        {
            Id = 1,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
            SalesProvider = ExternalProvider.Nayax,
        });

    private static NayaxSales Sale(
        long transactionId, long machineId, string? machineName, DateTime authorizedAt, decimal settlementValue,
        string paymentMethod = "Cash") =>
        new()
        {
            BusinessId = 1,
            TransactionID = transactionId,
            MachineID = machineId,
            MachineName = machineName,
            SettlementValue = settlementValue,
            PaymentMethod = paymentMethod,
            MachineAuthorizationTime = authorizedAt,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            CostOfGoodsSold = 4m,
        };

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
        var provider = new EfMachineProfitabilityReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

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
        var provider = new EfMachineProfitabilityReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

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
        var provider = new EfMachineProfitabilityReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

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
        var provider = new EfMachineProfitabilityReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

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
        var provider = new EfMachineProfitabilityReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        Assert.Equal(2, facts.MissingFeeRateTransactionCount);
        Assert.All(facts.Machines, machine => Assert.True(machine.ProcessingFees.HasMissingRates));
    }

    /// <summary>
    /// Issue #1263's fixture. A machine whose stored sale name changed between two sales is still one
    /// machine, so it is one row - and its own expense is charged to that row once. Grouping by
    /// <c>{ MachineID, MachineName }</c> produced a row per name and charged each one the machine's
    /// whole $6 expense, reporting $12 of cost the business never incurred.
    /// </summary>
    [Fact]
    public async Task One_row_per_machine_even_when_its_stored_sale_name_changed()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        SeedNayaxBusiness(db);
        var machine10 = new LocalMachine { BusinessId = 1, Name = "Lobby", ExternalMachineId = "10" };
        db.Machines.Add(machine10);
        db.NayaxSales.AddRange(
            Sale(1, 10, "Lobby", date, 10m),
            Sale(2, 10, "Lobby (Level 1)", date, 10m));
        await db.SaveChangesAsync();
        db.OperatingExpenses.Add(new OperatingExpense { BusinessId = 1, ExpenseDate = date, MachineId = machine10.Id, TotalAmount = 6m });
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        var machine = Assert.Single(facts.Machines);
        Assert.Equal(10, machine.MachineId);
        Assert.Equal(20m, machine.Sales);
        Assert.Equal(6m, machine.OperatingExpenses);
    }

    /// <summary>
    /// The other two per-machine charges the duplicate rows multiplied: the machine's fees were
    /// estimated once per row from all of its card sales, and its whole commission due was charged to
    /// every row (issue #1263).
    /// </summary>
    [Fact]
    public async Task Fees_and_commission_are_charged_once_per_machine()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        SeedNayaxBusiness(db);
        db.Machines.Add(new LocalMachine { BusinessId = 1, Name = "Lobby", ExternalMachineId = "10" });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { BusinessId = 1, EffectiveFrom = new DateTime(2020, 1, 1), FeeExGst = 0.20m });
        db.NayaxSales.AddRange(
            Sale(1, 10, "Lobby", date, 10m, "Credit Card"),
            Sale(2, 10, "Lobby (Level 1)", date, 10m, "Credit Card"));
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), CommissionsFor(0.10m, (10, 2m)),
            TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        var machine = Assert.Single(facts.Machines);
        // Two card transactions at the one effective rate, and the machine's commission once.
        Assert.Equal(0.40m, machine.ProcessingFees.EstimatedFeeExGst);
        Assert.Equal(2m, machine.CommissionDue);
        Assert.Equal(0.10m, machine.CommissionPercent);
        Assert.Equal(0.40m, facts.Machines.Sum(x => x.ProcessingFees.EstimatedFeeExGst));
        Assert.Equal(2m, facts.Machines.Sum(x => x.CommissionDue));
    }

    /// <summary>
    /// The row's name is this business's own machine record's name, whatever the provider called the
    /// machine when each sale was stored (issue #1263).
    /// </summary>
    [Fact]
    public async Task Row_name_is_the_local_machine_name()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        SeedNayaxBusiness(db);
        db.Machines.Add(new LocalMachine { BusinessId = 1, Name = "Lobby", ExternalMachineId = "10" });
        db.NayaxSales.AddRange(
            Sale(1, 10, "Lobby", date, 10m),
            Sale(2, 10, "Lobby (Level 1)", date.AddHours(1), 10m));
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        Assert.Equal("Lobby", Assert.Single(facts.Machines).MachineName);
    }

    /// <summary>
    /// With no local machine record there is no name of this business's own, so the row falls back to
    /// the latest sale's name - and when two names are equally recent, to the one on the sale with the
    /// higher transaction id, so the answer does not depend on the order rows came back in
    /// (issue #1263).
    /// </summary>
    [Fact]
    public async Task Row_name_falls_back_to_the_latest_sale_name_and_breaks_a_tie_on_transaction_id()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        SeedNayaxBusiness(db);
        // The winning row is neither the first nor the last stored, and the machine's highest
        // transaction id is on its oldest sale, so only "latest, then higher transaction id" picks it.
        db.NayaxSales.AddRange(
            Sale(12, 10, "Lobby (older)", date, 10m),
            Sale(9, 10, "Lobby (tie, higher id)", date.AddHours(2), 10m),
            Sale(7, 10, "Lobby (tie, lower id)", date.AddHours(2), 10m));
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        Assert.Equal("Lobby (tie, higher id)", Assert.Single(facts.Machines).MachineName);
    }

    /// <summary>
    /// A machine with no local record and no stored sale name is still one row, named from its
    /// provider number as it always was.
    /// </summary>
    [Fact]
    public async Task Row_name_falls_back_to_the_machine_number_when_nothing_names_the_machine()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        db.NayaxSales.AddRange(
            Sale(1, 10, null, date, 10m),
            Sale(2, 10, null, date, 10m));
        await db.SaveChangesAsync();
        var provider = new EfMachineProfitabilityReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(date, date, null, CancellationToken.None);

        Assert.Equal("Machine 10", Assert.Single(facts.Machines).MachineName);
    }
}
