using System.Text;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Imports;
using InventoryApi.Adapters.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// The uploaded Nayax sales import use case (issue #301, child 3 of 3 of #151), moved out of
/// <c>InventoryApi.Services.ImportService.ImportNayaxSalesFromExcelAsync</c>. These run it over its
/// real workbook reader and its real EF store, so the dedup, costing and rebuild path they assert
/// is the one production runs.
///
/// Imported Nayax sales drive historical costing and every financial report, so the rules pinned
/// here are the ones a regression would be expensive in: which rows are importable, that a replay
/// updates instead of double counting, that an unknown product and a non-completed status stay
/// visible rather than being costed, and the save/rebuild/save order a fatal replay must not break.
/// </summary>
public class ImportNayaxSalesTests
{
    private const string SalesHeader =
        "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,ProductName,MachineAuthorizationTime";

    /// <summary>
    /// Moved from the removed <c>ImportServiceTests</c> with its assertions unchanged: re-importing
    /// a transaction whose product was unknown recosts it from the inventory ledger.
    /// </summary>
    [Fact]
    public async Task Sales_import_recosts_an_updated_transaction()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack", QuantityInStock = 10, AverageUnitCost = 2m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = 10,
            QuantityAfter = 10,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m,
            EffectiveAt = new DateTime(2026, 9, 1)
        });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            MachineID = 1,
            NayaxProductId = 999,
            ProductName = "Unknown",
            SettlementValue = 5m,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineAuthorizationTime = new DateTime(2026, 9, 2)
        });
        await db.SaveChangesAsync();

        await CreateSalesImport(db).Handle(Csv(
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,ProductName,MachineAuthorizationTime\n" +
            "1,12,1,10,5,Snack,2/9/2026 2:30:00 PM"));

        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(2m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
    }

    /// <summary>
    /// A row the import cannot identify is counted and stays visible; it is never imported with a
    /// guessed transaction, machine or instant, because every one of the three is what later
    /// reconciliation, machine attribution and costing key off.
    /// </summary>
    [Fact]
    public async Task A_row_without_an_identifiable_transaction_machine_or_instant_is_skipped()
    {
        await using var db = CreateDb();

        var result = await CreateSalesImport(db).Handle(Csv(
            SalesHeader + "\n" +
            ",12,1,10,5,Card,Snack,2/9/2026 2:30:00 PM\n" +
            "0,12,1,10,5,Card,Snack,2/9/2026 2:30:00 PM\n" +
            "1003,12,,10,5,Card,Snack,2/9/2026 2:30:00 PM\n" +
            "1004,12,0,10,5,Card,Snack,2/9/2026 2:30:00 PM\n" +
            "1005,12,1,10,5,Card,Snack,\n" +
            "1006,12,1,10,5,Card,Snack,2026-09-02T14:30:00Z\n" +
            "1007,12,1,10,5,Card,Snack,2/9/2026 2:30:00 PM"));

        Assert.Equal(new NayaxSalesImportResult(1, 0, 6), result);
        Assert.Equal([1007L], await db.NayaxSales.Select(sale => sale.TransactionID).ToListAsync());
    }

    /// <summary>
    /// Issue #380: an export carrying the authoritative <c>AuthorizationDateTimeGMT</c> column is
    /// imported at that instant, not at the machine-local <c>MachineAuthorizationTime</c> column. The
    /// row here is the production symptom's own sale - 23:30 on Sunday 4 October 2026 in Sydney, the
    /// evening daylight saving started, which is 12:30Z - so reading the machine-local column would
    /// store 23:30Z and move the sale onto Monday 5 October in Sydney.
    /// </summary>
    [Fact]
    public async Task An_export_carrying_the_GMT_column_is_imported_at_that_instant()
    {
        await using var db = CreateDb();

        var result = await CreateSalesImport(db).Handle(Csv(
            "TransactionID,TransactionStatusId,MachineID,SettlementValue,MachineAuthorizationTime,AuthorizationDateTimeGMT\n" +
            "1001,12,1,3.00,4/10/2026 11:30:00 PM,2026-10-04T12:30:00Z"));

        Assert.Equal(new NayaxSalesImportResult(1, 0, 0), result);
        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(new DateTime(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc), sale.MachineAuthorizationTime);
    }

    /// <summary>
    /// Issue #380: re-uploading the same export must leave the stored instant exactly where it is, so
    /// an offset-aware timestamp can never be applied a second time.
    /// </summary>
    [Fact]
    public async Task A_replayed_export_does_not_shift_the_stored_instant_a_second_time()
    {
        await using var db = CreateDb();
        var import = CreateSalesImport(db);
        var file = "TransactionID,TransactionStatusId,MachineID,SettlementValue,AuthorizationDateTimeGMT\n" +
            "1001,12,1,3.00,2026-10-04T23:30:00+11:00";

        await import.Handle(Csv(file));
        await import.Handle(Csv(file));

        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(new DateTime(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc), sale.MachineAuthorizationTime);
    }

    /// <summary>
    /// The export is replayed in practice - an operator re-downloads an overlapping date range -
    /// so a transaction already held must be updated in place with the later file's facts, never
    /// counted a second time (AGENTS.md: "Avoid double counting imported transactions").
    /// </summary>
    [Fact]
    public async Task A_replayed_export_updates_the_stored_transaction_instead_of_double_counting_it()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        await db.SaveChangesAsync();
        var import = CreateSalesImport(db);

        var first = await import.Handle(Csv(
            SalesHeader + "\n1001,55,1,10,3.00,Card,Snack,2/9/2026 2:30:00 PM"));
        var replay = await import.Handle(Csv(
            SalesHeader + "\n1001,12,4,10,3.50,Cash,Snack,2/9/2026 2:30:00 PM"));

        Assert.Equal(new NayaxSalesImportResult(1, 0, 0), first);
        Assert.Equal(new NayaxSalesImportResult(0, 1, 0), replay);
        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal(4, sale.MachineID);
        Assert.Equal(3.50m, sale.SettlementValue);
        Assert.Equal("Cash", sale.PaymentMethod);
    }

    /// <summary>
    /// A product Nayax reports but this catalogue does not hold is imported and left visibly
    /// uncosted - never costed from another product's cost, and never dropped, because an
    /// unmatched transaction is a data-quality fact the reports have to be able to show.
    /// </summary>
    [Fact]
    public async Task An_unknown_product_is_imported_and_left_visibly_uncosted()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack", AverageUnitCost = 2m });
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        var result = await CreateSalesImport(db, rebuild.Object).Handle(Csv(
            SalesHeader + "\n1001,12,1,777,3.00,Card,Mystery Bar,2/9/2026 2:30:00 PM"));

        Assert.Equal(new NayaxSalesImportResult(1, 0, 0), result);
        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(777, sale.NayaxProductId);
        Assert.Null(sale.UnitCostAtSale);
        Assert.Null(sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Error, sale.CostingStatus);
        Assert.Equal(SaleCostSource.Unknown, sale.CostSource);
        rebuild.Verify(
            x => x.RebuildAsync(It.IsAny<long>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Every status the Domain classifier knows, plus an unrecognised one and none at all: the raw
    /// identifier is persisted as imported, and only an approved sale (status ID 12) is costed and
    /// replays the product's inventory cost. A pending, refunded, cancelled, declined or unknown
    /// row stays stored and visible instead of being treated as completed or discarded.
    /// </summary>
    [Theory]
    [InlineData(NayaxTransactionStatusIds.Completed, true)]
    [InlineData(NayaxTransactionStatusIds.PendingSettlementNotFinal, false)]
    [InlineData(NayaxTransactionStatusIds.PendingBatch, false)]
    [InlineData(NayaxTransactionStatusIds.Refunded, false)]
    [InlineData(NayaxTransactionStatusIds.CancelledOrDeclined26, false)]
    [InlineData(NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed, false)]
    [InlineData(NayaxTransactionStatusIds.CancelledOrDeclined31, false)]
    [InlineData(NayaxTransactionStatusIds.CancelledOrDeclined250, false)]
    [InlineData(21, false)]
    [InlineData(null, false)]
    public async Task Only_an_approved_sale_is_costed_and_replays_the_products_inventory_cost(
        int? statusId, bool expectedCompleted)
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        db.InventoryCostTransitionBaselines.Add(Baseline(10, openingCostingQuantity: 10, inventoryValue: 20m));
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        await CreateSalesImport(db, rebuild.Object).Handle(Csv(
            SalesHeader + ",Product Cost Price\n" +
            $"1001,{statusId},1,10,3.00,Card,Snack,3/10/2026 7:30:00 PM,1.10"));

        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(statusId, sale.TransactionStatusId);
        Assert.Equal(1.10m, sale.NayaxProductCostPrice);
        // The persisted raw identifier and the Domain rule's verdict on it, so a reimplemented
        // status list in the import would be caught here rather than silently costing a refund.
        Assert.Equal(
            expectedCompleted,
            NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId));
        Assert.Equal(
            expectedCompleted ? SaleCostingStatus.Costed : SaleCostingStatus.Pending,
            sale.CostingStatus);
        Assert.Equal(
            expectedCompleted ? SaleCostSource.NayaxTransactionExport : SaleCostSource.Unknown,
            sale.CostSource);
        rebuild.Verify(
            x => x.RebuildAsync(10, It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            expectedCompleted ? Times.Once() : Times.Never());
    }

    /// <summary>
    /// A completed sale at or before the product's transition-baseline cutoff is covered by that
    /// baseline, so it must not trigger a replay that would recost history the transition owns.
    /// </summary>
    [Fact]
    public async Task A_completed_sale_before_the_transition_cutoff_does_not_replay_the_products_cost()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        db.InventoryCostTransitionBaselines.Add(Baseline(10, openingCostingQuantity: 10, inventoryValue: 20m));
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        await CreateSalesImport(db, rebuild.Object).Handle(Csv(
            SalesHeader + "\n1001,12,1,10,3.00,Card,Snack,2/9/2026 2:30:00 PM"));

        Assert.Single(await db.NayaxSales.ToListAsync());
        rebuild.Verify(
            x => x.RebuildAsync(It.IsAny<long>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A product with no transition baseline at all has nothing to replay from, exactly as the
    /// legacy import decided: its sales are imported and costed, and no rebuild is started.
    /// </summary>
    [Fact]
    public async Task A_product_with_no_transition_baseline_is_never_replayed()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        await CreateSalesImport(db, rebuild.Object).Handle(Csv(
            SalesHeader + "\n1001,12,1,10,3.00,Card,Snack,3/10/2026 7:30:00 PM"));

        Assert.Single(await db.NayaxSales.ToListAsync());
        rebuild.Verify(
            x => x.RebuildAsync(It.IsAny<long>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The uploaded import's failure behaviour, which issue #301 requires to stay distinct from the
    /// latest-sales synchronization's (issue #362, pinned by
    /// <c>NayaxHistoricalCostTests.Latest_sales_sync_saves_other_products_costs_when_one_products_history_is_fatal</c>).
    /// Here a fatal replay aborts the replay loop and the rebuilt costs are never saved - no
    /// per-product catch and continue. That is safe for this path precisely because re-uploading
    /// the export runs the whole import again, which the sync can never do for a sale it has
    /// already stored.
    ///
    /// The unreplayable product's sale is listed first, so the healthy product is only left
    /// un-replayed if the loop genuinely stops at the failure.
    /// </summary>
    [Fact]
    public async Task A_fatal_replay_aborts_the_replay_loop_and_saves_no_rebuilt_cost()
    {
        await using var db = CreateDb();
        db.Products.AddRange(
            new Product { Id = 10, Name = "Costed Snack", QuantityInStock = 10 },
            new Product { Id = 20, Name = "Uncosted Water", QuantityInStock = 4 });
        db.InventoryCostTransitionBaselines.AddRange(
            Baseline(10, openingCostingQuantity: 10, inventoryValue: 20m, homeStockQuantity: 10),
            Baseline(20, openingCostingQuantity: 0, inventoryValue: 0m, homeStockQuantity: 4));
        await db.SaveChangesAsync();
        var rebuild = new RecordingRebuild(TestCostingUseCases.Rebuild(db));

        var exception = await Assert.ThrowsAsync<InventoryCostDataQualityException>(
            () => CreateSalesImport(db, rebuild).Handle(Csv(
                SalesHeader + "\n" +
                "2001,12,1,20,3.00,Card,Uncosted Water,3/10/2026 7:30:00 PM\n" +
                "1001,12,1,10,3.00,Card,Costed Snack,3/10/2026 7:30:00 PM")));

        Assert.Contains("no known opening cost", exception.Message);
        Assert.Equal([20L], rebuild.ReplayedProductIds);
        // Both sales were saved before the replay began, so the import is not silently discarded.
        Assert.Equal(
            new long[] { 1001, 2001 },
            await db.NayaxSales.AsNoTracking().OrderBy(sale => sale.TransactionID)
                .Select(sale => sale.TransactionID).ToArrayAsync());
        // Read untracked: nothing the replay staged was saved, for either product.
        var products = await db.Products.AsNoTracking().OrderBy(product => product.Id).ToListAsync();
        Assert.All(products, product =>
        {
            Assert.Null(product.CostingQuantity);
            Assert.Null(product.InventoryValue);
        });
    }

    /// <summary>
    /// The format rule the endpoint has always enforced, with the message it answers
    /// <c>400 Bad Request</c> with. It is checked before the upload is read at all, so an
    /// unsupported file is never parsed or persisted.
    /// </summary>
    [Theory]
    [InlineData("sales.txt")]
    [InlineData("sales.xlsm")]
    [InlineData("sales")]
    public async Task An_unsupported_upload_format_is_refused_without_reading_it(string fileName)
    {
        await using var db = CreateDb();
        var reader = new Mock<INayaxSalesWorkbookReader>(MockBehavior.Strict);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ImportNayaxSales(
                    reader.Object,
                    new EfNayaxSalesImportStore(db),
                    TestCostingUseCases.CostSale(db),
                    Mock.Of<IRebuildProductCost>())
                .Handle(new NayaxSalesFileInput(fileName, () => new MemoryStream())));

        Assert.Equal("Only .xlsx, .xls, or .csv files are supported.", exception.Message);
        Assert.Empty(await db.NayaxSales.ToListAsync());
    }

    [Theory]
    [InlineData("sales.xlsx")]
    [InlineData("SALES.CSV")]
    [InlineData("sales.XLS")]
    public async Task A_supported_upload_format_is_accepted_whatever_its_casing(string fileName)
    {
        await using var db = CreateDb();
        var reader = new Mock<INayaxSalesWorkbookReader>();
        reader.Setup(x => x.Read(It.IsAny<Stream>(), fileName)).Returns([]);

        var result = await new ImportNayaxSales(
                reader.Object,
                new EfNayaxSalesImportStore(db),
                TestCostingUseCases.CostSale(db),
                Mock.Of<IRebuildProductCost>())
            .Handle(new NayaxSalesFileInput(fileName, () => new MemoryStream()));

        Assert.Equal(new NayaxSalesImportResult(0, 0, 0), result);
    }

    /// <summary>
    /// An export carrying no data rows writes nothing and reads nothing - not even the catalogue -
    /// so an operator who uploads an empty report cannot disturb stored sales or costing.
    /// </summary>
    [Fact]
    public async Task An_export_with_no_data_rows_reads_and_writes_nothing()
    {
        await using var db = CreateDb();
        var store = new Mock<INayaxSalesImportStore>(MockBehavior.Strict);
        var reader = new Mock<INayaxSalesWorkbookReader>();
        reader.Setup(x => x.Read(It.IsAny<Stream>(), It.IsAny<string>())).Returns([]);

        var result = await new ImportNayaxSales(
                reader.Object, store.Object, Mock.Of<ICostSale>(), Mock.Of<IRebuildProductCost>())
            .Handle(Csv("TransactionID,MachineID,MachineAuthorizationTime"));

        Assert.Equal(new NayaxSalesImportResult(0, 0, 0), result);
    }

    /// <summary>
    /// A file of nothing but skipped rows is reported honestly and changes nothing: no save runs,
    /// so a report that produced no usable row cannot look like a successful import.
    /// </summary>
    [Fact]
    public async Task A_file_of_only_skipped_rows_saves_nothing()
    {
        await using var db = CreateDb();
        var store = new Mock<INayaxSalesImportStore>();
        store.Setup(x => x.GetProductCandidatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var reader = new Mock<INayaxSalesWorkbookReader>();
        reader.Setup(x => x.Read(It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns([new NayaxSalesImportRow(0, 0, null, null, null, null, 0m, null, null, null)]);

        var result = await new ImportNayaxSales(
                reader.Object, store.Object, Mock.Of<ICostSale>(), Mock.Of<IRebuildProductCost>())
            .Handle(Csv("ignored"));

        Assert.Equal(new NayaxSalesImportResult(0, 0, 1), result);
        store.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static InventoryCostTransitionBaseline Baseline(
        long productId, int openingCostingQuantity, decimal inventoryValue, int homeStockQuantity = 0) =>
        new()
        {
            ProductId = productId,
            HomeStockQuantity = homeStockQuantity,
            OpeningCostingQuantity = openingCostingQuantity,
            InventoryValue = inventoryValue,
            AverageUnitCost = openingCostingQuantity > 0 ? inventoryValue / openingCostingQuantity : 0m,
            CutoffAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    private static AppDbContext CreateDb() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>
    /// The use case over its real workbook reader and its real EF store, with the real costing
    /// use cases over the same context - the wiring the production container builds.
    /// </summary>
    private static ImportNayaxSales CreateSalesImport(AppDbContext db, IRebuildProductCost? rebuild = null)
    {
        var costRebuild = rebuild ?? TestCostingUseCases.Rebuild(db);
        return new ImportNayaxSales(
            new ClosedXmlNayaxSalesWorkbookReader(),
            new EfNayaxSalesImportStore(db),
            TestCostingUseCases.CostSale(db, costRebuild),
            costRebuild);
    }

    private static NayaxSalesFileInput Csv(string content) =>
        new("sales.csv", () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// The real product-cost rebuild, recording which products the import actually asked it to
    /// replay, so a loop that silently continued past a fatal product would be visible.
    /// </summary>
    private sealed class RecordingRebuild : IRebuildProductCost
    {
        private readonly IRebuildProductCost _inner;

        public RecordingRebuild(IRebuildProductCost inner) => _inner = inner;

        public List<long> ReplayedProductIds { get; } = [];

        public Task<InventoryCostRebuildResult> RebuildAsync(
            long productId,
            DateTime? recostCompletedSalesFrom = null,
            bool dryRun = false,
            CancellationToken cancellationToken = default)
        {
            ReplayedProductIds.Add(productId);
            return _inner.RebuildAsync(productId, recostCompletedSalesFrom, dryRun, cancellationToken);
        }

        public Task<InventoryCostRebuildResult> RebuildCostingOnlyAsync(
            long productId, DateTime recostCompletedSalesFrom, CancellationToken cancellationToken = default) =>
            _inner.RebuildCostingOnlyAsync(productId, recostCompletedSalesFrom, cancellationToken);

        public Task<decimal?> GetAverageUnitCostAtAsync(
            long productId, DateTime saleTime, long? saleTransactionId = null, CancellationToken cancellationToken = default) =>
            _inner.GetAverageUnitCostAtAsync(productId, saleTime, saleTransactionId, cancellationToken);
    }
}
