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
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return (connection, options);
    }

    private static IGetSiteCommissionReport EmptyCommissions()
    {
        var mock = new Mock<IGetSiteCommissionReport>();
        mock.Setup(x => x.Handle(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, long? _, CancellationToken _) => new SiteCommissionReport(from, to, []));
        return mock.Object;
    }

    [Fact]
    public async Task Splits_card_and_cash_sales_and_reports_cogs_completeness()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
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
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

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
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 1m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 7, 31, 23, 59, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 2m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 2, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 8m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 3, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 2), null, CancellationToken.None);

        Assert.Equal(6m, facts.GrossSales);
    }

    [Fact]
    public async Task Machine_filter_scopes_sales_to_the_selected_machine_only()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 20, SettlementValue = 30m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        Assert.Equal(10m, facts.GrossSales);
    }

    [Fact]
    public async Task Non_completed_sales_are_excluded_from_totals_and_cogs_completeness()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 99m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.PendingSettlementNotFinal, CostOfGoodsSold = null });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(10m, facts.GrossSales);
        Assert.True(facts.IsCogsComplete);
    }

    [Fact]
    public async Task Status_diagnostics_count_every_row_in_scope_before_the_completed_sale_filter()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            Sale(1, NayaxTransactionStatusIds.Completed, 10m, costOfGoodsSold: 4m),
            Sale(2, NayaxTransactionStatusIds.PendingSettlementNotFinal, 20m),
            Sale(3, NayaxTransactionStatusIds.PendingBatch, 21m),
            Sale(4, NayaxTransactionStatusIds.Refunded, 30m),
            Sale(5, NayaxTransactionStatusIds.CancelledOrDeclined26, 40m),
            Sale(6, NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed, 41m),
            Sale(7, 9999, 50m),
            Sale(8, null, 60m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(2, facts.PendingTransactionCount);
        Assert.Equal(1, facts.RefundedTransactionCount);
        Assert.Equal(2, facts.DeclinedOrCancelledTransactionCount);
        Assert.Equal(1, facts.UnknownStatusTransactionCount);
        Assert.Equal(1, facts.MissingStatusTransactionCount);
        // The diagnostics never widen what the totals include: only the completed sale counts.
        Assert.Equal(10m, facts.GrossSales);
        Assert.True(facts.IsCogsComplete);
    }

    [Fact]
    public async Task Status_diagnostics_are_scoped_to_the_selected_machine_and_date_range()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            Sale(1, null, 10m, machineId: 10),
            Sale(2, null, 10m, machineId: 20),
            Sale(3, null, 10m, machineId: 10, authorizationTime: new DateTime(2025, 8, 2)),
            Sale(4, 9999, 10m, machineId: 20));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        Assert.Equal(1, facts.MissingStatusTransactionCount);
        Assert.Equal(0, facts.UnknownStatusTransactionCount);
    }

    [Fact]
    public async Task A_period_whose_rows_are_all_completed_and_costed_reports_no_status_or_cost_problem()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            Sale(1, NayaxTransactionStatusIds.Completed, 10m, costOfGoodsSold: 4m),
            Sale(2, NayaxTransactionStatusIds.Completed, 5m, costOfGoodsSold: 2m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(0, facts.MissingStatusTransactionCount);
        Assert.Equal(0, facts.UnknownStatusTransactionCount);
        Assert.Equal(0, facts.PendingTransactionCount);
        Assert.Equal(0, facts.RefundedTransactionCount);
        Assert.Equal(0, facts.DeclinedOrCancelledTransactionCount);
        Assert.True(facts.IsCogsComplete);
    }

    /// <summary>
    /// Issue #804: the completed-transaction count this report now publishes is the count of the
    /// very rows its own totals are summed from - approved status 12 only, with every other
    /// outcome excluded exactly as it is from gross sales. A completed sale whose payment method
    /// classifies as neither card nor cash is still one of them, so the count is not the card and
    /// cash counts added together.
    /// </summary>
    [Fact]
    public async Task Transaction_count_counts_completed_sales_only_including_an_unknown_payment_method()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 5m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 2m },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 6m, PaymentMethod = "Mystery Tender", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 3m },
            Sale(4, NayaxTransactionStatusIds.PendingSettlementNotFinal, 99m),
            Sale(5, NayaxTransactionStatusIds.Refunded, 99m),
            Sale(6, NayaxTransactionStatusIds.CancelledOrDeclined26, 99m),
            Sale(7, 9999, 99m),
            Sale(8, null, 99m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(3, facts.TransactionCount);
        Assert.Equal(1, facts.CardTransactions);
        Assert.Equal(1, facts.CashTransactions);
        Assert.Equal(1, facts.UnknownTransactions);
        Assert.Equal(21m, facts.GrossSales);
    }

    [Fact]
    public async Task Transaction_count_is_scoped_to_the_selected_machine_and_date_range()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            Sale(1, NayaxTransactionStatusIds.Completed, 10m, machineId: 10),
            Sale(2, NayaxTransactionStatusIds.Completed, 10m, machineId: 10),
            Sale(3, NayaxTransactionStatusIds.Completed, 10m, machineId: 20),
            Sale(4, NayaxTransactionStatusIds.Completed, 10m, machineId: 10, authorizationTime: new DateTime(2025, 8, 2)));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        Assert.Equal(2, facts.TransactionCount);
        Assert.Equal(20m, facts.GrossSales);
    }

    [Fact]
    public async Task A_period_with_no_completed_sale_reports_a_zero_transaction_count()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.Add(Sale(1, NayaxTransactionStatusIds.PendingSettlementNotFinal, 99m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(0, facts.TransactionCount);
        Assert.Equal(0m, facts.GrossSales);
    }

    private static NayaxSales Sale(
        long transactionId, int? transactionStatusId, decimal settlementValue,
        long machineId = 10, DateTime? authorizationTime = null, decimal? costOfGoodsSold = null) =>
        new()
        {
            TransactionID = transactionId,
            MachineID = machineId,
            PaymentMethod = "Credit Card",
            SettlementValue = settlementValue,
            MachineAuthorizationTime = authorizationTime ?? new DateTime(2025, 8, 1),
            TransactionStatusId = transactionStatusId,
            CostOfGoodsSold = costOfGoodsSold
        };

    [Fact]
    public async Task Receipt_delivery_and_package_costs_are_zero_when_no_receipts_are_in_range()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(0m, facts.ReceiptDeliveryCost);
        Assert.Equal(0m, facts.ReceiptPackageCost);
        Assert.Equal(0m, facts.GrossSales);
        Assert.True(facts.IsCogsComplete);
    }

    /// <summary>
    /// The machine filter is still the <em>provider's</em> machine number (issue #780 deliberately
    /// left the report filters alone), but an expense now records the local machine row. So the
    /// number has to be resolved through <c>LocalMachine.ExternalMachineId</c> before the expenses
    /// are narrowed, and the whole-business and machine-filtered totals have to be exactly what
    /// they were before the repoint: 36 and 11.
    /// </summary>
    [Fact]
    public async Task Operating_expenses_are_filtered_by_the_providers_machine_number_through_the_local_machine()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
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
        var machine11 = new LocalMachine { BusinessId = 1, Name = "Machine 11", ExternalMachineId = "11" };
        db.Machines.AddRange(machine10, machine11);
        db.NayaxSales.AddRange(
            Sale(1, NayaxTransactionStatusIds.Completed, 10m, machineId: 10, authorizationTime: date, costOfGoodsSold: 4m),
            Sale(2, NayaxTransactionStatusIds.Completed, 20m, machineId: 11, authorizationTime: date, costOfGoodsSold: 8m));
        await db.SaveChangesAsync();
        db.OperatingExpenses.AddRange(
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, MachineId = machine10.Id, TotalAmount = 11m, GstAmount = 1m },
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, MachineId = machine11.Id, TotalAmount = 22m, GstAmount = 2m },
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, TotalAmount = 3m, GstAmount = 0m });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var wholeBusiness = await provider.GetFactsAsync(date, date, null, CancellationToken.None);
        var machineTen = await provider.GetFactsAsync(date, date, 10, CancellationToken.None);

        Assert.Equal(36m, wholeBusiness.OperatingExpensesTotal);
        Assert.Equal(11m, machineTen.OperatingExpensesTotal);
        Assert.Equal(1m, machineTen.OperatingExpensesGst);
    }

    /// <summary>
    /// A provider machine this business has recorded no machine for narrows the report to no
    /// expenses at all. Falling back to the whole business's would report another machine's costs,
    /// and a shared sales row set, against the one machine that was asked about.
    /// </summary>
    [Fact]
    public async Task A_machine_filter_with_no_local_machine_reports_no_operating_expenses()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
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
        db.NayaxSales.Add(
            Sale(1, NayaxTransactionStatusIds.Completed, 10m, machineId: 99, authorizationTime: date, costOfGoodsSold: 4m));
        await db.SaveChangesAsync();
        db.OperatingExpenses.Add(
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, MachineId = machine10.Id, TotalAmount = 11m, GstAmount = 1m });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions());

        var facts = await provider.GetFactsAsync(date, date, 99, CancellationToken.None);

        Assert.Equal(0m, facts.OperatingExpensesTotal);
        Assert.Equal(0m, facts.OperatingExpensesGst);
        Assert.Empty(facts.OperatingExpensesByCategory);
    }
}
