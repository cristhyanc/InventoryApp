using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Domain.Reporting.Availability;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests for the provider data coverage record (issue #730, parent #329 decision
/// P9): the write adapter, its tenant ownership, and the reporting read that turns stored ranges
/// into the four availability states.
///
/// These are relational rather than in-memory because the whole point of the record is what the
/// database holds and which business can see it: the ownership stamp, the query filter, and the
/// overlap predicate the reporting read translates to SQL (AGENTS.md § Database and migrations:
/// prefer relational tests where constraints and SQL translation matter).
/// </summary>
public class ProviderDataCoverageStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static readonly DateTime RecordedAt = new(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodFrom = new(2026, 7, 1);
    private static readonly DateTime PeriodTo = new(2026, 7, 31);

    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = RecordedAt },
            new Business { Id = BusinessB, Name = "Vending B", Status = BusinessStatus.Active, CreatedAtUtc = RecordedAt });
        await setup.SaveChangesAsync();
        return (connection, options);
    }

    private static EfProviderDataCoverageStore StoreFor(AppDbContext db) =>
        new(db, new FakeClock(RecordedAt));

    private static Inventory.Application.Commissions.IGetSiteCommissionReport EmptyCommissions()
    {
        var mock = new Mock<Inventory.Application.Commissions.IGetSiteCommissionReport>();
        mock.Setup(x => x.Handle(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, long? _, CancellationToken _) =>
                new Inventory.Application.Commissions.SiteCommissionReport(from, to, []));
        return mock.Object;
    }

    [Fact]
    public async Task A_recorded_range_is_stamped_with_the_callers_business_and_the_clocks_instant()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var db = TestAppDbContext.For(options, BusinessA))
            await StoreFor(db).RecordAsync(
                ExternalProvider.Nayax, ProviderDataSource.SalesSync,
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc),
                CancellationToken.None);

        await using var verify = TestAppDbContext.Unrestricted(options);
        var stored = Assert.Single(await verify.ProviderDataCoverages.ToListAsync());
        Assert.Equal(BusinessA, stored.BusinessId);
        Assert.Equal(ExternalProvider.Nayax, stored.Provider);
        Assert.Equal(ProviderDataSource.SalesSync, stored.Source);
        Assert.Equal(new DateTime(2026, 7, 1), stored.FromUtc);
        Assert.Equal(new DateTime(2026, 7, 20), stored.ToUtc);
        Assert.Equal(RecordedAt, stored.RecordedAtUtc);
    }

    [Fact]
    public async Task An_empty_or_inverted_range_is_not_coverage_and_is_never_stored()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var instant = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var store = StoreFor(db);
            await store.RecordAsync(ExternalProvider.Nayax, ProviderDataSource.SalesSync, instant, instant, CancellationToken.None);
            await store.RecordAsync(ExternalProvider.Nayax, ProviderDataSource.SalesSync, instant, instant.AddDays(-1), CancellationToken.None);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(await verify.ProviderDataCoverages.ToListAsync());
    }

    [Fact]
    public async Task One_businesss_coverage_never_accounts_for_anothers_period()
    {
        // The whole value of the record is that it says something about *this* business's data. A
        // leak here would let one business's successful sync present another's unread period as a
        // complete, real zero.
        await using var connection = (await CreateSqliteAsync()).Connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var db = TestAppDbContext.For(options, BusinessA))
            await StoreFor(db).RecordAsync(
                ExternalProvider.Nayax, ProviderDataSource.SalesSync,
                PeriodFrom.AddDays(-5), PeriodTo.AddDays(5), CancellationToken.None);

        await using var other = TestAppDbContext.For(options, BusinessB);
        var facts = await new EfBookkeepingReportFactsProvider(
                other, TestFinancialUseCases.ProcessingFees(other), EmptyCommissions())
            .GetFactsAsync(PeriodFrom, PeriodTo, null, CancellationToken.None);

        Assert.Empty(facts.SalesCoverage);
        Assert.Equal(
            ReportAvailabilityState.Missing,
            ReportAvailabilityPolicy.Decide(PeriodFrom, PeriodTo.AddDays(1), facts.SalesCoverage, facts.ContainsSalesRows).State);
    }

    [Fact]
    public async Task Sales_and_settlement_coverage_are_read_into_separate_families()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var store = StoreFor(db);
            await store.RecordAsync(ExternalProvider.Nayax, ProviderDataSource.SalesSync,
                PeriodFrom.AddDays(-1), new DateTime(2026, 7, 15), CancellationToken.None);
            await store.RecordAsync(ExternalProvider.Nayax, ProviderDataSource.SalesFileImport,
                new DateTime(2026, 7, 14), PeriodTo.AddDays(1), CancellationToken.None);
            await store.RecordAsync(ExternalProvider.Nayax, ProviderDataSource.SettlementImport,
                PeriodFrom, new DateTime(2026, 7, 11), CancellationToken.None);
            // Outside the requested period entirely: read by neither family.
            await store.RecordAsync(ExternalProvider.Nayax, ProviderDataSource.SalesSync,
                new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), CancellationToken.None);
        }

        await using var db2 = TestAppDbContext.For(options, BusinessA);
        var facts = await new EfBookkeepingReportFactsProvider(
                db2, TestFinancialUseCases.ProcessingFees(db2), EmptyCommissions())
            .GetFactsAsync(PeriodFrom, PeriodTo, null, CancellationToken.None);

        // The sync and the file import together span the period with no gap, so sales are complete;
        // the settlement import stops on the 11th, so the fee figures are only good up to then.
        Assert.Equal(2, facts.SalesCoverage.Count);
        Assert.Equal(
            ReportAvailabilityState.Complete,
            ReportAvailabilityPolicy.Decide(PeriodFrom, PeriodTo.AddDays(1), facts.SalesCoverage, facts.ContainsSalesRows).State);

        var settlement = ReportAvailabilityPolicy.Decide(
            PeriodFrom, PeriodTo.AddDays(1), facts.SettlementCoverage, facts.ImportedContainsRows);
        Assert.Equal(ReportAvailabilityState.Stale, settlement.State);
        Assert.Equal(new DateTime(2026, 7, 11), settlement.AsOfUtc);
    }

    [Fact]
    public async Task A_period_holding_only_non_completed_sales_still_counts_as_holding_data()
    {
        // Gross sales would be zero for this period, and it must not therefore read as "missing":
        // rows exist that no coverage accounts for, which is unverified.
        await using var connection = (await CreateSqliteAsync()).Connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 1,
                MachineID = 10,
                MachineName = "Machine A",
                SettlementValue = 4m,
                PaymentMethod = "Credit Card",
                MachineAuthorizationTime = new DateTime(2026, 7, 5),
                TransactionStatusId = Inventory.Domain.FinancialConfiguration.NayaxTransactionStatusIds.CancelledOrDeclined250,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var facts = await new EfBookkeepingReportFactsProvider(
                db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions())
            .GetFactsAsync(PeriodFrom, PeriodTo, null, CancellationToken.None);

        Assert.Equal(0m, facts.GrossSales);
        Assert.True(facts.ContainsSalesRows);
        Assert.Equal(
            ReportAvailabilityState.Unverified,
            ReportAvailabilityPolicy.Decide(PeriodFrom, PeriodTo.AddDays(1), facts.SalesCoverage, facts.ContainsSalesRows).State);
    }

    [Fact]
    public async Task The_report_carries_the_decided_states_through_to_its_response()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var db0 = TestAppDbContext.For(options, BusinessA))
            await StoreFor(db0).RecordAsync(ExternalProvider.Nayax, ProviderDataSource.SalesSync,
                PeriodFrom.AddDays(-2), PeriodTo.AddDays(2), CancellationToken.None);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var report = await new Inventory.Application.Reporting.Bookkeeping.GetBookkeepingReport(
                new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions()))
            .Handle(new ReportingFilterDto(PeriodFrom, PeriodTo), CancellationToken.None);

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Fees.State);
        Assert.Equal(0m, report.Sales);
    }
}
