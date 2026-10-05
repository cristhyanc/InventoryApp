using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests for the machine dashboard facts adapter's financial periods (issue #310).
/// A dashboard period starts and ends at an <c>Australia/Sydney</c> business-day boundary, which falls
/// in the middle of a UTC day, so the period a sale's revenue is counted in and the period its Nayax
/// processing fee is charged to must be the same period. These tests pin an instant where the Sydney
/// business date and the UTC date differ, which is where the two used to disagree: the fee lookup is a
/// date-range contract, and truncating Sydney-derived boundaries to whole UTC dates widened the fee
/// window to every UTC day the period touched.
/// </summary>
public class EfMachineDashboardFactsStoreTests
{
    /// <summary>
    /// 14:30 UTC on Wednesday 11 March 2026 is 01:30 on Thursday 12 March in Sydney (AEDT, +11), so
    /// the dashboard's "today" runs from 11 March 13:00 UTC to that instant, entirely inside UTC
    /// 11 March.
    /// </summary>
    private static readonly FixedSydneyTime Time =
        new(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

    private const long MachineId = 10;
    private const long SiteId = 91;

    /// <summary>20:00 on Wednesday 11 March in Sydney: the business day before the dashboard's.</summary>
    private static readonly DateTime YesterdayInSydney =
        new(2026, 3, 11, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>01:00 on Thursday 12 March in Sydney: inside the dashboard's own business day.</summary>
    private static readonly DateTime TodayInSydney =
        new(2026, 3, 11, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Today_estimates_fees_only_for_the_sales_its_revenue_counts()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = Db(connection);
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate
        {
            EffectiveFrom = new DateTime(2020, 1, 1),
            FeeExGst = .20m
        });
        db.NayaxSales.AddRange(
            CardSale(1, YesterdayInSydney, settlementValue: 5m, costOfGoodsSold: 2m),
            CardSale(2, TodayInSydney, settlementValue: 6m, costOfGoodsSold: 3m));
        await db.SaveChangesAsync();

        var facts = await Store(db).GetFactsAsync(MachineId, SiteId, Time.Window, CancellationToken.None);

        // Only the sale inside the Sydney business day counts towards revenue, and only that sale is
        // charged a fee: one estimated transaction at 0.20 excluding GST is 0.22 including GST. The
        // previous evening's sale is in neither total.
        Assert.Equal(6m, facts.Today.GrossRevenue);
        Assert.Equal(.22m, facts.Today.ProfitInputs.FeesIncludingGst);
        Assert.Equal(3m, facts.Today.ProfitInputs.NetSalesBeforeFees);
        Assert.False(facts.Today.ProfitInputs.ResolutionUnavailable);
        Assert.False(facts.Today.ProfitInputs.HasMissingFeeRates);
    }

    [Fact]
    public async Task Today_charges_the_imported_fee_of_its_own_business_day_and_does_not_estimate_it_again()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = Db(connection);
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate
        {
            EffectiveFrom = new DateTime(2020, 1, 1),
            FeeExGst = .20m
        });
        db.NayaxSales.Add(CardSale(1, TodayInSydney, settlementValue: 6m, costOfGoodsSold: 3m));

        // Imported fee data is authoritative for the dates it covers, and it covers Thursday
        // 12 March - the Sydney business day the dashboard means, not the UTC day its instants fall on.
        var file = new ImportedFile { FileName = "fees.xml", FileHash = "fees", ImportedAt = Time.NowUtc };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2026, 3, 12),
            ReimbursementEndDate = new DateTime(2026, 3, 12),
            Devices = { new ImportedReimbursementDevice { MachineNumber = "10", ProcessingFee = 2m } }
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();

        var facts = await Store(db).GetFactsAsync(MachineId, SiteId, Time.Window, CancellationToken.None);

        // 2.00 actual excluding GST plus 0.20 GST, and no estimate on top of the covered day.
        Assert.Equal(2.20m, facts.Today.ProfitInputs.FeesIncludingGst);
        Assert.Equal(3m, facts.Today.ProfitInputs.NetSalesBeforeFees);
        Assert.False(facts.Today.ProfitInputs.HasMissingFeeRates);
    }

    private static EfMachineDashboardFactsStore Store(AppDbContext db) =>
        new(db, TestFinancialUseCases.ProcessingFees(db, Time.Calendar));

    private static NayaxSales CardSale(
        long transactionId, DateTime authorizationTimeUtc, decimal settlementValue, decimal costOfGoodsSold) =>
        new()
        {
            TransactionID = transactionId,
            MachineID = MachineId,
            SettlementValue = settlementValue,
            CostOfGoodsSold = costOfGoodsSold,
            PaymentMethod = "Credit Card",
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineAuthorizationTime = authorizationTimeUtc
        };

    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = Db(connection);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    private static AppDbContext Db(SqliteConnection connection) =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
}
