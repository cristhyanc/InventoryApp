using System.Text;
using Inventory.Application.Imports;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// Issue #730 (parent #329 decision P9): an uploaded Nayax transaction export records <b>no</b>
/// provider data coverage, however completely it imported.
///
/// <para>The import succeeding says what was stored; it does not say the workbook held every
/// transaction of the span its rows happen to cover. The upload is a file a person exported from
/// Nayax Core and it declares neither its range nor its scope - it may have been filtered to one
/// machine, one payment type or one site - and the authoritative <c>lastSales</c> contract carries
/// no requested-period metadata either. Recording the interval between the earliest and latest
/// imported instants would therefore claim completeness for time nothing accounted for, which is the
/// false zero the coverage record exists to prevent. These tests pin that: the imported sales are
/// reported in full, and their period stays <c>Unverified</c> - never <c>Complete</c> - until a
/// fully successful synchronization covers it.</para>
///
/// <para>Relational SQLite rather than in-memory, because the claim being tested is about what the
/// database holds and what the reporting read then makes of it (AGENTS.md § Database and
/// migrations).</para>
/// </summary>
public class ImportNayaxSalesCoverageTests
{
    private const int BusinessA = 1;

    private static readonly DateTime PeriodFrom = new(2026, 7, 1);
    private static readonly DateTime PeriodTo = new(2026, 7, 31);

    private const string SalesHeader =
        "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,ProductName,MachineAuthorizationTime,AuthorizationDateTimeGMT";

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        setup.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await setup.SaveChangesAsync();
        return options;
    }

    private static ImportNayaxSales UseCase(AppDbContext db) =>
        new(new ClosedXmlNayaxSalesWorkbookReader(),
            new EfNayaxSalesImportStore(db),
            TestCostingUseCases.CostSale(db),
            Mock.Of<Inventory.Application.Costing.IRebuildProductCost>());

    private static NayaxSalesFileInput Csv(string content) =>
        new("sales.csv", () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    /// <summary>One completed card sale of machine <paramref name="machineId"/>.</summary>
    private static string Row(long transactionId, long machineId, string gmt) =>
        $"{transactionId},12,{machineId},,4.00,Credit Card,Snack,{gmt},{gmt}";

    private static Inventory.Application.Commissions.IGetSiteCommissionReport EmptyCommissions()
    {
        var mock = new Mock<Inventory.Application.Commissions.IGetSiteCommissionReport>();
        mock.Setup(x => x.Handle(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, long? _, CancellationToken _) =>
                new Inventory.Application.Commissions.SiteCommissionReport(from, to, []));
        return mock.Object;
    }

    private static Task<BookkeepingReportDto> ReportAsync(AppDbContext db) =>
        new GetBookkeepingReport(
                new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions()))
            .Handle(new ReportingFilterDto(PeriodFrom, PeriodTo), CancellationToken.None);

    [Fact]
    public async Task A_workbook_imported_whole_records_no_coverage_at_all()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var result = await UseCase(db).Handle(Csv(string.Join('\n',
                SalesHeader,
                Row(1, 10, "2026-07-04T08:00:00Z"),
                Row(2, 10, "2026-07-21T19:30:00Z"),
                Row(3, 10, "2026-07-11T12:15:00Z"))));

            Assert.Equal(new NayaxSalesImportResult(3, 0, 0), result);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(await verify.ProviderDataCoverages.ToListAsync());
    }

    [Fact]
    public async Task A_machine_filtered_workbook_cannot_make_an_uncovered_period_complete()
    {
        // The workbook holds only machine 10, on the 4th and the 21st. The business also sold
        // through machine 11 on the 12th, and that sale is not in the file. Claiming
        // [4 July, 21 July) from the file's own extrema would have presented the whole middle of
        // the month as accounted for, which it is not - the file was filtered.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 99,
                MachineID = 11,
                MachineName = "Machine B",
                SettlementValue = 7m,
                PaymentMethod = "Credit Card",
                MachineAuthorizationTime = new DateTime(2026, 7, 12),
                TransactionStatusId = Inventory.Domain.FinancialConfiguration.NayaxTransactionStatusIds.Completed,
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var result = await UseCase(db).Handle(Csv(string.Join('\n',
                SalesHeader,
                Row(1, 10, "2026-07-04T08:00:00Z"),
                Row(2, 10, "2026-07-21T19:30:00Z"))));

            Assert.Equal(new NayaxSalesImportResult(2, 0, 0), result);
        }

        await using var db2 = TestAppDbContext.For(options, BusinessA);
        var report = await ReportAsync(db2);

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.ProviderAvailability.Sales.State);
        Assert.NotEqual(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        // The imported data is reported in full all the same: unverified labels it, it does not hide it.
        Assert.Equal(15m, report.Sales);
    }

    [Fact]
    public async Task A_workbook_with_a_row_the_import_could_not_identify_records_no_coverage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var result = await UseCase(db).Handle(Csv(string.Join('\n',
                SalesHeader,
                Row(1, 10, "2026-07-04T08:00:00Z"),
                // No transaction identifier: skipped, never guessed at.
                ",12,10,,4.00,Credit Card,Snack,2026-07-05T08:00:00Z,2026-07-05T08:00:00Z")));

            Assert.Equal(1, result.Imported);
            Assert.Equal(1, result.Skipped);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Single(await verify.NayaxSales.ToListAsync());
        Assert.Empty(await verify.ProviderDataCoverages.ToListAsync());
    }

    [Fact]
    public async Task An_export_with_no_data_rows_imports_nothing_and_records_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
            Assert.Equal(new NayaxSalesImportResult(0, 0, 0), await UseCase(db).Handle(Csv(SalesHeader)));

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(await verify.ProviderDataCoverages.ToListAsync());
    }
}
