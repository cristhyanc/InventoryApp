using System.Text;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Providers;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// Issue #730 (parent #329 decision P9): what an uploaded Nayax transaction export records as
/// covered, and when it records nothing.
///
/// A sales export carries no requested-period metadata, so the instants of the rows it committed
/// are the only range it proves. The rule that matters financially is the other half: a file that
/// was not committed whole - a row that could not be identified, or a cost replay that failed -
/// records no coverage, so its period stays visibly unaccounted for instead of looking complete.
/// </summary>
public class ImportNayaxSalesCoverageTests
{
    private const string SalesHeader =
        "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,ProductName,MachineAuthorizationTime,AuthorizationDateTimeGMT";

    private static AppDbContext CreateDb() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ImportNayaxSales UseCase(
        AppDbContext db, FakeProviderDataCoverageStore coverage, IRebuildProductCost? rebuild = null)
    {
        var costRebuild = rebuild ?? TestCostingUseCases.Rebuild(db);
        return new ImportNayaxSales(
            new ClosedXmlNayaxSalesWorkbookReader(),
            new EfNayaxSalesImportStore(db),
            TestCostingUseCases.CostSale(db, costRebuild),
            costRebuild,
            coverage);
    }

    private static NayaxSalesFileInput Csv(string content) =>
        new("sales.csv", () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static string Row(long transactionId, string gmt) =>
        $"{transactionId},12,10,,4.00,Credit Card,Snack,{gmt},{gmt}";

    [Fact]
    public async Task A_file_committed_whole_claims_the_span_of_the_instants_it_stored()
    {
        await using var db = CreateDb();
        var coverage = new FakeProviderDataCoverageStore();

        var result = await UseCase(db, coverage).Handle(Csv(string.Join('\n',
            SalesHeader,
            Row(1, "2026-07-04T08:00:00Z"),
            Row(2, "2026-07-21T19:30:00Z"),
            Row(3, "2026-07-11T12:15:00Z"))));

        Assert.Equal(new NayaxSalesImportResult(3, 0, 0), result);
        var recorded = Assert.Single(coverage.Recorded);
        Assert.Equal(ExternalProvider.Nayax, recorded.Provider);
        Assert.Equal(ProviderDataSource.SalesFileImport, recorded.Source);
        Assert.Equal(new DateTime(2026, 7, 4, 8, 0, 0, DateTimeKind.Utc), recorded.FromUtc);
        Assert.Equal(new DateTime(2026, 7, 21, 19, 30, 0, DateTimeKind.Utc), recorded.ToUtc);
    }

    [Fact]
    public async Task A_file_with_a_row_the_import_could_not_identify_records_no_coverage()
    {
        // The skipped row is a sale of this period the import did not store, so the period is not
        // accounted for and must not be claimed - even though the other rows were saved.
        await using var db = CreateDb();
        var coverage = new FakeProviderDataCoverageStore();

        var result = await UseCase(db, coverage).Handle(Csv(string.Join('\n',
            SalesHeader,
            Row(1, "2026-07-04T08:00:00Z"),
            // No transaction identifier: skipped, never guessed at.
            $",12,10,,4.00,Credit Card,Snack,2026-07-05T08:00:00Z,2026-07-05T08:00:00Z")));

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.Single(await db.NayaxSales.ToListAsync());
        Assert.Empty(coverage.Recorded);
    }

    [Fact]
    public async Task An_import_whose_cost_replay_fails_part_way_records_no_coverage()
    {
        await using var db = CreateDb();
        db.Products.Add(new Inventory.Infrastructure.Models.Product
        {
            Id = 10,
            Name = "Snack",
            QuantityInStock = 5,
            AverageUnitCost = 2m,
            CostingQuantity = 5,
            InventoryValue = 10m
        });
        // The replay only runs for a product whose transition baseline the sale falls after, so the
        // failure this test is about is only reachable with one recorded.
        db.InventoryCostTransitionBaselines.Add(new Inventory.Infrastructure.Models.InventoryCostTransitionBaseline
        {
            ProductId = 10,
            CutoffAt = new DateTime(2026, 1, 1),
            OpeningCostingQuantity = 5,
            AverageUnitCost = 2m,
            InventoryValue = 10m,
            CostSource = Inventory.Infrastructure.Models.InventoryCostBaselineSource.ManualAuthoritative
        });
        await db.SaveChangesAsync();
        var coverage = new FakeProviderDataCoverageStore();
        var rebuild = new Mock<IRebuildProductCost>();
        rebuild.Setup(x => x.RebuildAsync(It.IsAny<long>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InventoryCostDataQualityException("unreplayable history"));

        await Assert.ThrowsAsync<InventoryCostDataQualityException>(() => UseCase(db, coverage, rebuild.Object)
            .Handle(Csv(string.Join('\n', SalesHeader, "1,12,10,10,4.00,Credit Card,Snack,2026-07-04T08:00:00Z,2026-07-04T08:00:00Z"))));

        Assert.Empty(coverage.Recorded);
    }

    [Fact]
    public async Task An_export_with_no_data_rows_claims_nothing()
    {
        await using var db = CreateDb();
        var coverage = new FakeProviderDataCoverageStore();

        var result = await UseCase(db, coverage).Handle(Csv(SalesHeader));

        Assert.Equal(new NayaxSalesImportResult(0, 0, 0), result);
        Assert.Empty(coverage.Recorded);
    }

    [Fact]
    public async Task A_single_row_file_proves_no_span_and_claims_nothing()
    {
        // Coverage is half-open, so one instant is an empty range: the row is stored and reported,
        // and it simply is not evidence that any period was read end to end.
        await using var db = CreateDb();
        var coverage = new FakeProviderDataCoverageStore();

        var result = await UseCase(db, coverage).Handle(Csv(string.Join('\n',
            SalesHeader, Row(1, "2026-07-04T08:00:00Z"))));

        Assert.Equal(1, result.Imported);
        Assert.Empty(coverage.Recorded);
    }
}
