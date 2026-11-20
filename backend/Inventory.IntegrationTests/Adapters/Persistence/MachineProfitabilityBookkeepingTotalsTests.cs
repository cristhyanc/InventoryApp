using Inventory.Application.Commissions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Reporting.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Machine profitability's per-machine rows must add up to the same money bookkeeping reports for the
/// same range (issue #1263). The two adapters measure the same facts differently - bookkeeping sums
/// the scope once, machine profitability sums it per machine - so a per-machine grouping bug shows up
/// here as a reconciliation failure and nowhere else. Grouping sales by
/// <c>{ MachineID, MachineName }</c> made a renamed machine two rows and charged each of them the
/// machine's whole fees, commission and expenses.
///
/// <para>Two figures are deliberately outside the comparison, because bookkeeping holds money this
/// report has no row for: an expense attributed to a machine with no sales in the period (machine
/// profitability has no row for a machine that did not sell, which issue #1263 left unchanged), and a
/// whole-business expense (which is not attributable to any machine at all). The fixture therefore
/// seeds neither.</para>
/// </summary>
public class MachineProfitabilityBookkeepingTotalsTests
{
    private static readonly DateTime Date = new(2025, 8, 1);

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    /// <summary>
    /// One site whose two machines each owe commission, as the live commission report would answer
    /// for them. Both machines that sell appear, so commission is complete and neither report is
    /// provisional for a reason other than the one under test.
    /// </summary>
    private static IGetSiteCommissionReport Commissions()
    {
        var mock = new Mock<IGetSiteCommissionReport>();
        mock.Setup(x => x.HandleBusinessPeriod(It.IsAny<SiteCommissionBusinessPeriod>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteCommissionBusinessPeriod period, long? _, CancellationToken _) =>
                new SiteCommissionReport(period.FirstBusinessDate, period.LastBusinessDate,
                [
                    new SiteCommissionReportRow(91, "Site 91", period.FirstBusinessDate, period.LastBusinessDate,
                        CommissionFrequency.Monthly, CommissionBasis.GrossSales, 50m, 50m, 0m, 50m, 0.10m, 5m,
                        [
                            new SiteCommissionMachineReportRow(10, "Lobby", 2, 20m, 20m, 0m, 20m, 2m),
                            new SiteCommissionMachineReportRow(11, "Gym", 1, 30m, 30m, 0m, 30m, 3m)
                        ],
                        [])
                ]));
        return mock.Object;
    }

    [Fact]
    public async Task Machine_profitability_totals_equal_bookkeepings_for_the_same_machines_and_range()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.Unrestricted(options);
        db.Businesses.Add(new Business
        {
            Id = 1,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
            SalesProvider = ExternalProvider.Nayax,
        });
        var lobby = new LocalMachine { BusinessId = 1, Name = "Lobby", ExternalMachineId = "10" };
        var gym = new LocalMachine { BusinessId = 1, Name = "Gym", ExternalMachineId = "11" };
        db.Machines.AddRange(lobby, gym);
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { BusinessId = 1, EffectiveFrom = new DateTime(2020, 1, 1), FeeExGst = 0.20m });
        db.NayaxSales.AddRange(
            // Machine 10 sold twice, under two different stored names.
            Sale(1, 10, "Lobby", 10m),
            Sale(2, 10, "Lobby (Level 1)", 10m),
            Sale(3, 11, "Gym", 30m));
        await db.SaveChangesAsync();
        db.OperatingExpenses.AddRange(
            new OperatingExpense { BusinessId = 1, ExpenseDate = Date, MachineId = lobby.Id, TotalAmount = 6m },
            new OperatingExpense { BusinessId = 1, ExpenseDate = Date, MachineId = gym.Id, TotalAmount = 4m });
        await db.SaveChangesAsync();

        var commissions = Commissions();
        var fees = TestFinancialUseCases.ProcessingFees(db);
        var bookkeeping = await new EfBookkeepingReportFactsProvider(db, fees, commissions, TestFinancialUseCases.TrivialCalendar)
            .GetFactsAsync(Date, Date, null, CancellationToken.None);
        var machines = await new EfMachineProfitabilityReportFactsProvider(db, fees, commissions, TestFinancialUseCases.TrivialCalendar)
            .GetFactsAsync(Date, Date, null, CancellationToken.None);

        Assert.Equal(2, machines.Machines.Count);
        Assert.Equal(bookkeeping.GrossSales, machines.Machines.Sum(x => x.Sales));
        Assert.Equal(bookkeeping.ProcessingFees.TotalFeeIncGst, machines.Machines.Sum(x => x.ProcessingFees.TotalFeeIncGst));
        Assert.Equal(bookkeeping.SiteCommission, machines.Machines.Sum(x => x.CommissionDue));
        Assert.Equal(bookkeeping.OperatingExpensesTotal, machines.Machines.Sum(x => x.OperatingExpenses));
        // The figures themselves, so a parity failure cannot be read as both reports drifting together.
        Assert.Equal(50m, machines.Machines.Sum(x => x.Sales));
        Assert.Equal(0.66m, machines.Machines.Sum(x => x.ProcessingFees.TotalFeeIncGst));
        Assert.Equal(5m, machines.Machines.Sum(x => x.CommissionDue));
        Assert.Equal(10m, machines.Machines.Sum(x => x.OperatingExpenses));
    }

    private static NayaxSales Sale(long transactionId, long machineId, string machineName, decimal settlementValue) =>
        new()
        {
            BusinessId = 1,
            TransactionID = transactionId,
            MachineID = machineId,
            MachineName = machineName,
            SettlementValue = settlementValue,
            PaymentMethod = "Credit Card",
            MachineAuthorizationTime = Date,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            CostOfGoodsSold = 4m,
        };
}
