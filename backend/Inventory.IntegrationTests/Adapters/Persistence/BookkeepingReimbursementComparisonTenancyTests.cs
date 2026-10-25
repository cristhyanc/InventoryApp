using Inventory.Application.Commissions;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.FinancialConfiguration;
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
/// Issue #805: the bookkeeping report's expected-versus-actual reimbursement comparison is built
/// from this business's own card sales and its own imported settlement. Two businesses hold a
/// settlement for the same period here - the case the central tenant query filters exist for - and
/// each report must compare only its own, because a comparison that mixed two operators' money
/// would reconcile to a number belonging to neither. Relational, because the overlap predicate, the
/// FK-backed reimbursement navigation and the filters themselves are SQL.
/// </summary>
public sealed class BookkeepingReimbursementComparisonTenancyTests : IDisposable
{
    private const long MachineId = 10;
    private static readonly DateTime Day = new(2026, 9, 1);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public BookkeepingReimbursementComparisonTenancyTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var seed = TestAppDbContext.Unrestricted(_options);
        seed.Database.EnsureCreated();
        seed.Businesses.AddRange(
            new Business { Id = 1, Name = "A", Status = BusinessStatus.Active, CreatedAtUtc = Day },
            new Business { Id = 2, Name = "B", Status = BusinessStatus.Active, CreatedAtUtc = Day });
        seed.SaveChanges();

        // Business 1 reconciles exactly: 100.00 of card sales, no fees configured, 100.00 settled.
        // Business 2 does not: 50.00 of card sales against 10.00 settled.
        foreach (var (businessId, cardSales, settled) in new[] { (1, 100m, 100m), (2, 50m, 10m) })
        {
            seed.NayaxSales.Add(new NayaxSales
            {
                BusinessId = businessId,
                TransactionID = businessId,
                MachineID = MachineId,
                PaymentMethod = "Credit Card",
                SettlementValue = cardSales,
                MachineAuthorizationTime = Day,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                CostOfGoodsSold = 1m
            });
            seed.ImportedFiles.Add(new ImportedFile
            {
                BusinessId = businessId,
                FileName = "settlement.xml",
                FileHash = "same-external-hash",
                ImportedAt = Day,
                Reimbursements =
                [
                    new ImportedReimbursement
                    {
                        BusinessId = businessId,
                        ReimbursementStartDate = Day,
                        ReimbursementEndDate = Day,
                        Total = settled,
                        // The device row is what a machine-filtered report matches on, by the
                        // provider's own machine number.
                        Devices =
                        [
                            new ImportedReimbursementDevice
                            {
                                BusinessId = businessId,
                                MachineNumber = MachineId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            }
                        ]
                    }
                ]
            });
        }
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IGetSiteCommissionReport EmptyCommissions()
    {
        var mock = new Mock<IGetSiteCommissionReport>();
        mock.Setup(x => x.Handle(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, long? _, CancellationToken _) => new SiteCommissionReport(from, to, []));
        return mock.Object;
    }

    private async Task<BookkeepingReportDto> ReportForAsync(int businessId, long? machineId = null)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        var useCase = new GetBookkeepingReport(new EfBookkeepingReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions()));
        return await useCase.Handle(new ReportingFilterDto(Day, Day, MachineId: machineId), CancellationToken.None);
    }

    [Fact]
    public async Task Each_business_compares_only_its_own_card_sales_and_its_own_imported_settlement()
    {
        var first = await ReportForAsync(1);
        var second = await ReportForAsync(2);

        Assert.Equal(100m, first.ExpectedReimbursement);
        Assert.Equal(100m, first.ActualReimbursement);
        Assert.Equal(0m, first.ReimbursementDifference);
        Assert.True(first.IsReconciled);
        Assert.Equal("Reconciled", first.ReconciliationStatus);

        Assert.Equal(50m, second.ExpectedReimbursement);
        Assert.Equal(10m, second.ActualReimbursement);
        Assert.Equal(-40m, second.ReimbursementDifference);
        Assert.False(second.IsReconciled);
        Assert.Equal("Needs Review", second.ReconciliationStatus);
    }

    /// <summary>
    /// An unresolved business reads nothing, so it has no card sales and no settlement to compare -
    /// and that is reported as a pending comparison, never as a reconciled zero (AGENTS.md § Tenant
    /// ownership and data isolation: fail closed).
    /// </summary>
    [Fact]
    public async Task An_unresolved_business_reports_a_pending_comparison_rather_than_a_reconciled_zero()
    {
        await using var db = TestAppDbContext.Denied(_options);
        var useCase = new GetBookkeepingReport(new EfBookkeepingReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions()));

        var report = await useCase.Handle(new ReportingFilterDto(Day, Day), CancellationToken.None);

        Assert.Equal(0m, report.ExpectedReimbursement);
        Assert.Equal(0m, report.ActualReimbursement);
        Assert.False(report.IsReconciled);
        Assert.Equal("Pending", report.ReconciliationStatus);
    }

    /// <summary>
    /// A date range the settlement does not cover matches no imported row, so the report is pending
    /// against its own expectation instead of borrowing another period's figure.
    /// </summary>
    [Fact]
    public async Task A_period_the_settlement_does_not_cover_is_pending_for_that_business()
    {
        await using var db = TestAppDbContext.For(_options, 1);
        var useCase = new GetBookkeepingReport(new EfBookkeepingReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions()));

        var report = await useCase.Handle(
            new ReportingFilterDto(Day.AddMonths(1), Day.AddMonths(1)), CancellationToken.None);

        Assert.Equal(0m, report.ActualReimbursement);
        Assert.Equal("Pending", report.ReconciliationStatus);
        Assert.False(report.IsReconciled);
    }

    /// <summary>
    /// The machine filter narrows both sides to the selected machine's own figures, and a machine
    /// no imported device row mentions reports that rather than silently comparing against the
    /// whole business's settlement - which is the established matching behaviour this issue
    /// deliberately did not change.
    /// </summary>
    [Fact]
    public async Task A_machine_filter_compares_the_selected_machines_own_scoped_figures()
    {
        var filtered = await ReportForAsync(2, MachineId);
        var otherMachine = await ReportForAsync(2, 999);

        Assert.Equal(50m, filtered.ExpectedReimbursement);
        Assert.Equal(10m, filtered.ActualReimbursement);
        Assert.Equal(-40m, filtered.ReimbursementDifference);

        Assert.Equal(0m, otherMachine.ExpectedReimbursement);
        Assert.Equal(0m, otherMachine.ActualReimbursement);
        Assert.Contains(otherMachine.DataQuality.Notes!,
            note => note.Contains("No reimbursement device row matched the selected machine ID"));
    }
}
