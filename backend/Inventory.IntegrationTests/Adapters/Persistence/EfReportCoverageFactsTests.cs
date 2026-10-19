using Inventory.Application.Commissions;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Reporting.Availability;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Reporting.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests for the provider coverage every remaining sales-based report now reads
/// (issue #731, under issue #730's model).
///
/// <para>They exist because the coverage <em>query</em> is where this feature can fail silently: a
/// report whose adapter never populated its coverage would answer <c>Unverified</c> for every
/// period forever, which looks plausible and is wrong. Every report is driven through its real EF
/// adapter over the three cases the issue names - no sales data, historical sales after a
/// disconnect, and complete data - in one loop, so a report wired up differently from the others
/// cannot quietly keep its own answer. The half-open range, the per-source split and the
/// business-day window the daily report selects by all depend on SQL translation rather than
/// in-memory LINQ semantics.</para>
/// </summary>
public class EfReportCoverageFactsTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);
    private static readonly DateTime EndExclusive = new(2026, 8, 1);

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    private static IGetSiteCommissionReport EmptyCommissions()
    {
        var mock = new Mock<IGetSiteCommissionReport>();
        mock.Setup(x => x.Handle(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, long? _, CancellationToken _) => new SiteCommissionReport(from, to, []));
        return mock.Object;
    }

    private static ProviderDataCoverage Coverage(DateTime fromUtc, DateTime toUtc, ProviderDataSource source) => new()
    {
        Provider = ExternalProvider.Nayax,
        Source = source,
        FromUtc = fromUtc,
        ToUtc = toUtc,
        RecordedAtUtc = toUtc,
    };

    private static async Task SeedAsync(AppDbContext db, bool withSale, params ProviderDataCoverage[] coverage)
    {
        if (withSale)
            db.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 1,
                MachineID = 10,
                MachineName = "Machine A",
                NayaxProductId = 1,
                ProductName = "Water",
                SettlementValue = 10m,
                PaymentMethod = "Credit Card",
                MachineAuthorizationTime = new DateTime(2026, 7, 5, 3, 0, 0, DateTimeKind.Utc),
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                CostOfGoodsSold = 4m,
            });
        db.ProviderDataCoverages.AddRange(coverage);
        await db.SaveChangesAsync();
    }

    /// <summary>Each report's coverage facts, read through its real EF adapter.</summary>
    private static async Task<IReadOnlyList<(string Report, ReportCoverageFacts Coverage)>> CoverageByReportAsync(
        AppDbContext db)
    {
        var fees = TestFinancialUseCases.ProcessingFees(db);
        var calendar = new FakeBusinessCalendar(To);

        var daily = await new EfDailyReportFactsProvider(db, fees, calendar)
            .GetFactsAsync(From, To, null, CancellationToken.None);
        var transactions = await new EfTransactionSalesReportFactsProvider(db)
            .GetFactsAsync(From, To, null, CancellationToken.None);
        var reconciliation = await new EfReconciliationReportFactsProvider(db)
            .GetFactsAsync(From, To, null, CancellationToken.None);
        var products = await new EfProductProfitabilityReportFactsProvider(db)
            .GetFactsAsync(From, To, null, CancellationToken.None);
        var machines = await new EfMachineProfitabilityReportFactsProvider(db, fees, EmptyCommissions())
            .GetFactsAsync(From, To, null, CancellationToken.None);

        return
        [
            ("daily", daily.Coverage),
            ("transactions", transactions.Coverage),
            ("reconciliation", reconciliation.Coverage),
            ("product-profitability", products.Coverage),
            ("machine-profitability", machines.Coverage),
        ];
    }

    /// <summary>
    /// The decided sales state for each report, as a list of pairs, so a failure names the report
    /// that disagreed instead of only the state that was wrong.
    /// </summary>
    private static async Task<IReadOnlyList<(string Report, string State)>> SalesStatesAsync(AppDbContext db) =>
        [.. (await CoverageByReportAsync(db)).Select(x => (x.Report, x.Coverage.SalesAvailability().State))];

    private static (string Report, string State)[] EveryReport(ReportAvailabilityState state) =>
        [
            ("daily", state.ToString()),
            ("transactions", state.ToString()),
            ("reconciliation", state.ToString()),
            ("product-profitability", state.ToString()),
            ("machine-profitability", state.ToString()),
        ];

    [Fact]
    public async Task No_sales_data_and_no_coverage_is_missing_for_every_report()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.Unrestricted(options);
        await SeedAsync(db, withSale: false);

        Assert.Equal(EveryReport(ReportAvailabilityState.Missing), await SalesStatesAsync(db));
        Assert.All(await CoverageByReportAsync(db), x => Assert.False(x.Coverage.ContainsSalesRows));
    }

    [Fact]
    public async Task Complete_coverage_is_complete_for_every_report()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.Unrestricted(options);
        await SeedAsync(db, withSale: true,
            Coverage(From.AddDays(-1), EndExclusive.AddDays(1), ProviderDataSource.SalesSync),
            Coverage(From.AddDays(-1), EndExclusive.AddDays(1), ProviderDataSource.SettlementImport));

        Assert.Equal(EveryReport(ReportAvailabilityState.Complete), await SalesStatesAsync(db));
    }

    [Fact]
    public async Task Historical_sales_after_a_disconnect_are_stale_as_of_the_last_covered_instant_for_every_report()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.Unrestricted(options);
        await SeedAsync(db, withSale: true,
            Coverage(From.AddDays(-30), lastCovered, ProviderDataSource.SalesSync));

        Assert.Equal(EveryReport(ReportAvailabilityState.Stale), await SalesStatesAsync(db));
        Assert.All(await CoverageByReportAsync(db), x =>
        {
            Assert.Equal(lastCovered, x.Coverage.SalesAvailability().AsOfUtc);
            Assert.True(x.Coverage.ContainsSalesRows);
        });
    }

    /// <summary>
    /// A settlement import proves nothing about sales, and a sales synchronization proves nothing
    /// about settlement: each family reads only the sources that feed it.
    /// </summary>
    [Fact]
    public async Task The_two_families_read_only_the_sources_that_feed_them()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.Unrestricted(options);
        await SeedAsync(db, withSale: true,
            Coverage(From.AddDays(-1), EndExclusive.AddDays(1), ProviderDataSource.SettlementImport));

        var coverage = (await CoverageByReportAsync(db)).Single(x => x.Report == "reconciliation").Coverage;

        Assert.Empty(coverage.SalesCoverage);
        Assert.Single(coverage.SettlementCoverage);
        Assert.Equal(nameof(ReportAvailabilityState.Unverified), coverage.SalesAvailability().State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), coverage.Availability().Fees.State);
    }

    /// <summary>
    /// A coverage range that ends exactly at the period start covers none of it, which is what keeps
    /// the half-open <c>[FromUtc, ToUtc)</c> claim honest, and it is not even read.
    /// </summary>
    [Fact]
    public async Task Coverage_outside_the_period_is_not_read()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.Unrestricted(options);
        await SeedAsync(db, withSale: true,
            Coverage(From.AddDays(-30), From, ProviderDataSource.SalesSync));

        var coverage = (await CoverageByReportAsync(db)).Single(x => x.Report == "product-profitability").Coverage;

        Assert.Empty(coverage.SalesCoverage);
        Assert.Equal(nameof(ReportAvailabilityState.Unverified), coverage.SalesAvailability().State);
    }
}
