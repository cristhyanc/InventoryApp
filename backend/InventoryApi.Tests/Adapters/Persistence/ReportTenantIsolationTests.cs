using Inventory.Application.Reporting.Daily;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The tenant boundary around report aggregates (issue #64).
///
/// Row-level isolation is proved elsewhere. An aggregate is a distinct risk: a report does not
/// return rows a reviewer can eyeball, it returns sums and counts, so another business's data
/// leaking in shows up only as a number that is quietly too large. Financial reporting is also
/// where a future optimisation is most tempted to reach past the query filter.
///
/// These run on SQLite so the aggregation happens in real SQL, where the filter has to survive
/// translation into the grouping.
///
/// Business A is 1 and business B is 2, matching the other tenancy tests.
/// </summary>
public sealed class ReportTenantIsolationTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static readonly DateTime ReportDay = new(2026, 8, 1);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ReportTenantIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Both businesses trade on the same day through different machines. Each one's gross sales
    /// must be its own takings alone - never the combined figure, and never the other's.
    /// </summary>
    [Fact]
    public async Task Daily_gross_sales_cover_only_the_current_business()
    {
        await SeedCompletedSaleAsync(BusinessA, transactionId: 1, machineId: 10, settlementValue: 3m);
        await SeedCompletedSaleAsync(BusinessB, transactionId: 2, machineId: 20, settlementValue: 500m);

        var dayForA = Assert.Single((await GetDailyFactsAsync(BusinessA)).Days);
        Assert.Equal(3m, dayForA.GrossSales);
        Assert.Equal(1, dayForA.TransactionCount);

        var dayForB = Assert.Single((await GetDailyFactsAsync(BusinessB)).Days);
        Assert.Equal(500m, dayForB.GrossSales);
        Assert.Equal(1, dayForB.TransactionCount);
    }

    /// <summary>
    /// A caller whose membership did not resolve must aggregate nothing at all. The dangerous
    /// failure is the opposite one: treating "no current business" as "no filter" would turn a
    /// resolution bug into a report totalling every business in the database.
    /// </summary>
    [Fact]
    public async Task A_caller_without_a_resolved_business_aggregates_nothing()
    {
        await SeedCompletedSaleAsync(BusinessA, transactionId: 1, machineId: 10, settlementValue: 3m);
        await SeedCompletedSaleAsync(BusinessB, transactionId: 2, machineId: 20, settlementValue: 500m);

        await using var denied = TestAppDbContext.Denied(_options);
        var facts = await new EfDailyReportFactsProvider(denied, new NayaxProcessingFeeService(denied))
            .GetFactsAsync(ReportDay, ReportDay, null, CancellationToken.None);

        Assert.Empty(facts.Days);
        Assert.Equal(0, facts.Totals.CompletedTransactionCount);
    }

    private async Task<DailyReportFacts> GetDailyFactsAsync(int businessId)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        return await new EfDailyReportFactsProvider(db, new NayaxProcessingFeeService(db))
            .GetFactsAsync(ReportDay, ReportDay, null, CancellationToken.None);
    }

    private async Task SeedCompletedSaleAsync(int businessId, long transactionId, long machineId, decimal settlementValue)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = transactionId,
            MachineID = machineId,
            NayaxProductId = 1,
            ProductName = "Snack",
            SettlementValue = settlementValue,
            PaymentMethod = "Cash",
            MachineAuthorizationTime = ReportDay,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
        });
        await db.SaveChangesAsync();
    }
}
