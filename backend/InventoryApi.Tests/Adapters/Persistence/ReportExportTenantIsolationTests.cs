using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The tenant boundary around report <em>exports</em> (issue #64).
///
/// An export is the one report surface that leaves the application as a file, so it is the worst
/// place for another business's figures to appear. It is also a distinct path from the API
/// response: <see cref="GetReportExportRows"/> builds the rows and a separate adapter encodes the
/// bytes, so proving the API report is scoped does not by itself prove the export is.
///
/// One export family is enough. Export does not query anything itself - every row comes from the
/// same authoritative report use case the API response uses, which is the property
/// <c>GetReportExportRowsTests</c> pins down - so scoping is established once, at the facts
/// provider, for all of them. This test therefore wires the <em>real</em> scoped
/// <see cref="EfDailyReportFactsProvider"/> through the real <see cref="GetDailyReport"/> into the
/// real <see cref="GetReportExportRows"/>, and fakes only the report families it does not ask for.
///
/// Relational SQLite, so the scoping survives translation into the aggregate SQL the export reads.
///
/// Business A is 1 and business B is 2, matching the other tenancy tests.
/// </summary>
public sealed class ReportExportTenantIsolationTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private const decimal SalesForA = 3m;
    private const decimal SalesForB = 500m;

    private static readonly DateTime ReportDay = new(2026, 8, 1);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ReportExportTenantIsolationTests()
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
    /// Both businesses trade on the reported day. The file business A downloads must contain A's
    /// takings and no trace of B's - not the combined figure, and not B's figure on a second row.
    /// </summary>
    [Fact]
    public async Task An_export_generated_for_one_business_contains_only_its_own_figures()
    {
        await SeedCompletedSaleAsync(BusinessA, transactionId: 1, machineId: 10, settlementValue: SalesForA);
        await SeedCompletedSaleAsync(BusinessB, transactionId: 2, machineId: 20, settlementValue: SalesForB);

        await using var db = TestAppDbContext.For(_options, BusinessA);
        var table = await ExportRowsFor(db).Handle(
            "daily",
            new ReportingFilterDto(ReportDay, ReportDay),
            CancellationToken.None);

        var header = table.Rows[0].ToArray();
        var grossSalesColumn = Array.IndexOf(header, "GrossSales");
        Assert.True(grossSalesColumn >= 0, "the daily export must have a GrossSales column to assert on.");

        // One day row, then the TOTAL row the export appends. The totals row matters most here:
        // a boundary leak would show up as a sum, where no individual value looks wrong.
        var dayRow = table.Rows[1];
        var totalRow = table.Rows[^1];

        Assert.Equal("TOTAL", totalRow[0]);
        Assert.Equal(Invariant(SalesForA), dayRow[grossSalesColumn]);
        Assert.Equal(Invariant(SalesForA), totalRow[grossSalesColumn]);

        // Neither business B's figure nor the combined total may appear in any cell.
        var everyCell = table.Rows.SelectMany(row => row).ToList();
        Assert.DoesNotContain(Invariant(SalesForB), everyCell);
        Assert.DoesNotContain(Invariant(SalesForA + SalesForB), everyCell);
    }

    private static string Invariant(decimal value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The daily family is real and scoped to <paramref name="db"/>; the others are fakes, because
    /// this test asks the export only for "daily" and a fake there would defeat its purpose.
    /// </summary>
    private static GetReportExportRows ExportRowsFor(AppDbContext db)
    {
        var daily = new GetDailyReport(new EfDailyReportFactsProvider(db, new NayaxProcessingFeeService(db)));

        var bookkeeping = new GetBookkeepingReport(
            new Application.Reporting.Bookkeeping.FakeBookkeepingReportFactsProvider(
                Application.Reporting.Bookkeeping.FakeBookkeepingReportFactsProvider.Complete()));
        var transactions = new GetTransactionSalesReport(
            new Application.Reporting.Transactions.FakeTransactionSalesReportFactsProvider(
                Application.Reporting.Transactions.FakeTransactionSalesReportFactsProvider.Empty()));
        var reconciliation = new Inventory.Application.Reporting.Reconciliation.GetReconciliationReport(
            new Application.Reporting.Reconciliation.FakeReconciliationReportFactsProvider(
                Application.Reporting.Reconciliation.FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machineProfitability = new Inventory.Application.Reporting.MachineProfitability.GetMachineProfitabilityReport(
            new Application.Reporting.MachineProfitability.FakeMachineProfitabilityReportFactsProvider(
                Application.Reporting.MachineProfitability.FakeMachineProfitabilityReportFactsProvider.Empty()));
        var productProfitability = new GetProductProfitabilityReport(
            new Application.Reporting.ProductProfitability.FakeProductProfitabilityReportFactsProvider(
                Application.Reporting.ProductProfitability.FakeProductProfitabilityReportFactsProvider.Empty()));
        var gst = new Inventory.Application.Reporting.Gst.GetGstAccountingAid(
            bookkeeping,
            new Application.Reporting.Gst.FakeGstReportFactsProvider(
                Application.Reporting.Gst.FakeGstReportFactsProvider.Complete()));
        var dashboard = new Inventory.Application.Reporting.Dashboard.GetDashboardReport(
            bookkeeping,
            new Application.Reporting.ProductProfitability.FakeGetProductProfitabilityReport(
                new ProductProfitabilityReportDto(default, default, [], new ReportingDataQualityDto())),
            new Application.Reporting.Dashboard.FakeDashboardReportFactsProvider(
                Application.Reporting.Dashboard.FakeDashboardReportFactsProvider.Complete()));

        return new GetReportExportRows(bookkeeping, daily, reconciliation, machineProfitability,
            productProfitability, gst, dashboard, transactions);
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
