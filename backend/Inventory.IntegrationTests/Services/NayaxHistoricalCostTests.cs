using System.Text;
using Inventory.Application.Costing;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Transactions;
using Inventory.Application.SalesSync;
using InventoryApi.Tests.Application.SalesSync;
using Inventory.Application.Commissions;
using Inventory.Application.Imports;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Reporting;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Application.Nayax;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

using Inventory.Domain.FinancialConfiguration;

namespace InventoryApi.Tests.Services;

public class NayaxHistoricalCostTests
{
    [Theory]
    [InlineData("Product Cost Price")]
    [InlineData("ProductCostPrice")]
    [InlineData("Product Cost")]
    [InlineData("Cost Price")]
    [InlineData("ProductCost")]
    public async Task Import_maps_supported_transaction_cost_headers(string header)
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack", AverageUnitCost = 2m });
        await db.SaveChangesAsync();

        var result = await CreateSalesImport(db).Handle(Csv(
            $"TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,ProductName,{header},MachineAuthorizationTime\n" +
            "1001,12,1,10,3.00,Card,Snack,1.10,2/9/2026 2:30:00 PM"));

        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(1, result.Imported);
        Assert.Equal(1.10m, sale.NayaxProductCostPrice);
        Assert.Equal(1.10m, sale.UnitCostAtSale);
        Assert.Equal(1.10m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
        Assert.Equal(SaleCostSource.NayaxTransactionExport, sale.CostSource);
        var product = await db.Products.SingleAsync();
        Assert.Equal(2m, product.AverageUnitCost);
    }

    [Fact]
    public async Task Duplicate_import_enriches_pending_sale_and_does_not_erase_cost_when_omitted()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1001,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = 10,
            ProductName = "Snack",
            SettlementValue = 3m,
            MachineAuthorizationTime = new DateTime(2026, 9, 2, 14, 30, 0)
        });
        await db.SaveChangesAsync();
        var importer = CreateSalesImport(db);

        await importer.Handle(Csv(
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,ProductName,Product Cost Price,MachineAuthorizationTime\n" +
            "1001,12,1,10,3.00,Snack,1.20,2/9/2026 2:30:00 PM"));
        await importer.Handle(Csv(
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,ProductName,MachineAuthorizationTime\n" +
            "1001,12,1,10,3.00,Snack,2/9/2026 2:30:00 PM"));

        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(1.20m, sale.NayaxProductCostPrice);
        Assert.Equal(1.20m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.NayaxTransactionExport, sale.CostSource);
    }

    [Fact]
    public async Task Duplicate_import_stores_Nayax_cost_without_replacing_inventory_ledger_cost()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1001,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = 10,
            ProductName = "Snack",
            SettlementValue = 3m,
            MachineAuthorizationTime = new DateTime(2026, 9, 2, 14, 30, 0),
            UnitCostAtSale = 1.15m,
            CostOfGoodsSold = 1.15m,
            CostingStatus = SaleCostingStatus.Costed,
            CostSource = SaleCostSource.InventoryLedger
        });
        await db.SaveChangesAsync();

        await CreateSalesImport(db).Handle(Csv(
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,ProductName,ProductCostPrice,MachineAuthorizationTime\n" +
            "1001,12,1,10,3.00,Snack,1.20,2/9/2026 2:30:00 PM"));

        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(1.20m, sale.NayaxProductCostPrice);
        Assert.Equal(1.15m, sale.UnitCostAtSale);
        Assert.Equal(1.15m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
    }

    [Fact]
    public async Task Internal_historical_AVCO_takes_priority_over_Nayax_transaction_cost()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();
        rebuild.Setup(x => x.GetAverageUnitCostAtAsync(
                10, It.IsAny<DateTime>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.15m);
        var sale = Sale(1, 10, 1.20m, "Card");

        await TestCostingUseCases.CostSale(db, rebuild.Object).CostAsync(sale);

        Assert.Equal(1.15m, sale.UnitCostAtSale);
        Assert.Equal(1.15m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
    }

    [Fact]
    public async Task Zero_internal_historical_AVCO_is_valid_and_takes_priority()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();
        rebuild.Setup(x => x.GetAverageUnitCostAtAsync(
                10, It.IsAny<DateTime>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        var sale = Sale(1, 10, 1.20m, "Card");

        await TestCostingUseCases.CostSale(db, rebuild.Object).CostAsync(sale);

        Assert.Equal(0m, sale.UnitCostAtSale);
        Assert.Equal(0m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
    }

    [Theory]
    [InlineData("Card")]
    [InlineData("Cash")]
    public async Task Transaction_cost_applies_to_completed_card_and_cash_sales(string paymentMethod)
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        await db.SaveChangesAsync();
        var sale = Sale(1, 10, 1.25m, paymentMethod);

        await TestCostingUseCases.CostSale(db).CostAsync(sale);

        Assert.Equal(1.25m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.NayaxTransactionExport, sale.CostSource);
    }

    [Fact]
    public async Task Declined_sale_keeps_raw_cost_without_normal_COGS()
    {
        await using var db = CreateDb();
        var sale = Sale(1, 10, 1.25m, "Card");
        sale.TransactionStatusId = NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed;

        await TestCostingUseCases.CostSale(db).CostAsync(sale);

        Assert.Equal(1.25m, sale.NayaxProductCostPrice);
        Assert.Null(sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Pending, sale.CostingStatus);
        Assert.Equal(SaleCostSource.Unknown, sale.CostSource);
    }

    [Fact]
    public async Task Completed_sale_without_any_cost_remains_pending()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        await db.SaveChangesAsync();
        var sale = Sale(1, 10, null, "Card");

        await TestCostingUseCases.CostSale(db).CostAsync(sale);

        Assert.Null(sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Pending, sale.CostingStatus);
        Assert.Equal(SaleCostSource.Unknown, sale.CostSource);
    }

    [Fact]
    public async Task Negative_transaction_cost_is_stored_but_not_applied()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        await db.SaveChangesAsync();
        var sale = Sale(1, 10, -1m, "Card");

        await TestCostingUseCases.CostSale(db).CostAsync(sale);

        Assert.Equal(-1m, sale.NayaxProductCostPrice);
        Assert.Null(sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Error, sale.CostingStatus);
        Assert.Equal(SaleCostSource.Unknown, sale.CostSource);
    }

    [Fact]
    public async Task Historical_backfill_dry_run_and_apply_recover_only_eligible_sales()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        db.NayaxSales.AddRange(Enumerable.Range(1, 100).Select(index =>
            Sale(index, 10, index <= 80 ? 1.10m : null, index % 2 == 0 ? "Cash" : "Card")));
        await db.SaveChangesAsync();
        var service = TestCostingUseCases.BackfillNayaxHistoricalSaleCosts(db);

        var dryRun = await service.Handle(dryRun: true);

        Assert.Equal(100, dryRun.SalesReviewed);
        Assert.Equal(80, dryRun.SalesWithNayaxCost);
        Assert.Equal(80, dryRun.SalesWouldBeCosted);
        Assert.Equal(20, dryRun.SalesStillPending);
        Assert.All(await db.NayaxSales.ToListAsync(), sale => Assert.Equal(SaleCostingStatus.Pending, sale.CostingStatus));

        var applied = await service.Handle(dryRun: false);

        Assert.Equal(80, applied.SalesWouldBeCosted);
        Assert.Equal(80, await db.NayaxSales.CountAsync(s => s.CostingStatus == SaleCostingStatus.Costed &&
            s.CostSource == SaleCostSource.NayaxTransactionExport));
        Assert.Equal(20, await db.NayaxSales.CountAsync(s => s.CostingStatus == SaleCostingStatus.Pending));
    }

    [Fact]
    public async Task Transaction_export_contains_raw_applied_and_source_cost_fields()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack" });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = 10,
            ProductName = "Snack",
            SettlementValue = 3m,
            NayaxProductCostPrice = 1.20m,
            UnitCostAtSale = 1.15m,
            CostOfGoodsSold = 1.15m,
            CostingStatus = SaleCostingStatus.Costed,
            CostSource = SaleCostSource.InventoryLedger,
            MachineAuthorizationTime = new DateTime(2026, 9, 2, 14, 30, 0)
        });
        await db.SaveChangesAsync();

        var nayaxFees = TestFinancialUseCases.ProcessingFees(db);
        var siteCommissions = Mock.Of<IGetSiteCommissionReport>();
        var getBookkeepingReport = new GetBookkeepingReport(new EfBookkeepingReportFactsProvider(
            db, nayaxFees, siteCommissions, TestFinancialUseCases.TrivialCalendar));
        var getDailyReport = new GetDailyReport(new EfDailyReportFactsProvider(db, nayaxFees, TestFinancialUseCases.TrivialCalendar));
        var getReconciliationReport = new GetReconciliationReport(new EfReconciliationReportFactsProvider(
            db, TestFinancialUseCases.TrivialCalendar));
        var getMachineProfitabilityReport = new GetMachineProfitabilityReport(new EfMachineProfitabilityReportFactsProvider(db, nayaxFees, siteCommissions));
        var getProductProfitabilityReport = new GetProductProfitabilityReport(
            new EfProductProfitabilityReportFactsProvider(db), new EfProductPurchaseCostFactsProvider(db));
        var getGstAccountingAid = new GetGstAccountingAid(getBookkeepingReport, new EfGstReportFactsProvider(db));
        var getDashboardReport = new GetDashboardReport(getBookkeepingReport, getProductProfitabilityReport,
            new EfDashboardReportFactsProvider(db, siteCommissions, TestFinancialUseCases.TrivialCalendar));
        var getTransactionSalesReport = new GetTransactionSalesReport(new EfTransactionSalesReportFactsProvider(db));
        var getReportExportRows = new GetReportExportRows(getBookkeepingReport, getDailyReport, getReconciliationReport,
            getMachineProfitabilityReport, getProductProfitabilityReport, getGstAccountingAid, getDashboardReport,
            getTransactionSalesReport);
        var csv = Encoding.UTF8.GetString(new ReportExportFileWriter().WriteCsv(await getReportExportRows.Handle(
            "transactions",
            new TransactionSalesFilterDto(
                From: new DateTime(2026, 9, 2), To: new DateTime(2026, 9, 2)),
            CancellationToken.None)));

        Assert.Contains("NayaxProductCostPrice", csv);
        Assert.Contains("UnitCostAtSale", csv);
        Assert.Contains("CostOfGoods", csv);
        Assert.Contains("CostingStatus", csv);
        Assert.Contains("CostSource", csv);
        Assert.Contains("Inventory Ledger", csv);
    }

    [Fact]
    public async Task Latest_sales_sync_refreshes_sales_without_erasing_imported_costs()
    {
        await using var db = CreateDb();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = 10,
            ProductName = "Snack",
            SettlementValue = 3m,
            NayaxProductCostPrice = 1.20m,
            UnitCostAtSale = 1.15m,
            CostOfGoodsSold = 1.15m,
            CostingStatus = SaleCostingStatus.Costed,
            CostSource = SaleCostSource.InventoryLedger,
            MachineAuthorizationTime = new DateTime(2026, 9, 2, 14, 30, 0)
        });
        await db.SaveChangesAsync();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine" } });
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>());
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new()
                {
                    TransactionID = 1,
                    MachineID = 1,
                    SettlementValue = 3m,
                    MachineAuthorizationTime = new DateTime(2026, 9, 2, 14, 30, 0)
                }
            });
        var syncLatestSales = LatestSalesSync(db, nayax.Object);

        await syncLatestSales.Handle();

        nayax.Verify(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal("Snack", sale.ProductName);
        Assert.Equal(1.20m, sale.NayaxProductCostPrice);
        Assert.Equal(1.15m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
    }

    /// <summary>
    /// Issue #362: one product whose cost history cannot be replayed must not discard the rebuilds
    /// the same sync already produced for the other products. The healthy product's rebuilt costing
    /// position is saved, the failing product is left untouched and both sales stay persisted. The
    /// failure no longer fails the sync: it is kept as the product's pending rebuild with its failure
    /// code, which is what the Dashboard's "costs need attention" read reports.
    /// </summary>
    [Fact]
    public async Task Latest_sales_sync_saves_other_products_costs_when_one_products_history_is_fatal()
    {
        await using var db = CreateDb();
        db.Products.AddRange(
            new Product { Id = 10, Name = "Costed Snack", QuantityInStock = 10 },
            new Product { Id = 20, Name = "Uncosted Water", QuantityInStock = 4 });
        db.InventoryCostTransitionBaselines.AddRange(
            Baseline(10, homeStockQuantity: 10, openingCostingQuantity: 10, inventoryValue: 20m),
            Baseline(20, homeStockQuantity: 4, openingCostingQuantity: 0, inventoryValue: 0m));
        await db.SaveChangesAsync();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                // The product that cannot be rebuilt is read first, so a later healthy product only
                // keeps its rebuild if the batch really continues past the failure.
                LastSale(2001, 20, "Uncosted Water"),
                LastSale(1001, 10, "Costed Snack")
            });

        var outcome = await LatestSalesSync(db, nayax.Object).Handle();

        // A run that happened: not refused, not busy, not a recent-sync skip.

        Assert.Null(outcome.RetryAfter);

        Assert.False(outcome.IsBusy);

        Assert.Null(outcome.RecentlySyncedAtUtc);
        var pending = await db.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
        Assert.Equal(20, pending.ProductId);
        Assert.Equal("data_quality", pending.LastFailureCode);
        Assert.Equal(1, pending.AttemptCount);
        // Read untracked, so these assert what the sync actually saved rather than what it staged.
        var costed = await db.Products.AsNoTracking().SingleAsync(product => product.Id == 10);
        Assert.Equal(9, costed.CostingQuantity);
        Assert.Equal(18m, costed.InventoryValue);
        Assert.Equal(2m, costed.AverageUnitCost);
        var uncosted = await db.Products.AsNoTracking().SingleAsync(product => product.Id == 20);
        Assert.Null(uncosted.CostingQuantity);
        Assert.Null(uncosted.InventoryValue);
        Assert.Equal(0m, uncosted.AverageUnitCost);
        Assert.Equal(
            new long[] { 1001, 2001 },
            await db.NayaxSales.AsNoTracking().OrderBy(sale => sale.TransactionID)
                .Select(sale => sale.TransactionID).ToArrayAsync());
    }

    /// <summary>
    /// A sale the ledger cannot cost - the product has no costed stock or purchase recorded before it -
    /// takes the transaction-level Nayax <c>Product Cost Price</c> it carries instead of failing the
    /// rebuild. It consumes no costing inventory, because the ledger never held the unit,
    /// and nothing is left pending.
    /// </summary>
    [Fact]
    public async Task Latest_sales_sync_costs_a_sale_with_no_ledger_cost_and_no_purchase_at_its_Nayax_cost()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 20, Name = "Uncosted Water", QuantityInStock = 4 });
        db.InventoryCostTransitionBaselines.Add(
            Baseline(20, homeStockQuantity: 4, openingCostingQuantity: 0, inventoryValue: 0m));
        await db.SaveChangesAsync();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine" } });
        var sale = LastSale(2001, 20, "Uncosted Water");
        sale.ProductCostPrice = 1.40m;
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport> { sale });

        var outcome = await LatestSalesSync(db, nayax.Object).Handle();

        // A run that happened: not refused, not busy, not a recent-sync skip.

        Assert.Null(outcome.RetryAfter);

        Assert.False(outcome.IsBusy);

        Assert.Null(outcome.RecentlySyncedAtUtc);
        var stored = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(1.40m, stored.UnitCostAtSale);
        Assert.Equal(1.40m, stored.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Costed, stored.CostingStatus);
        Assert.Equal(SaleCostSource.NayaxTransactionExport, stored.CostSource);
        var product = await db.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == 20);
        Assert.Equal(0, product.CostingQuantity);
        Assert.Equal(0m, product.InventoryValue);
        Assert.Empty(await db.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// When costed stock runs out, a sale takes the unit cost of the latest costed purchase replayed
    /// before it (the repository owner's 10 November 2026 decision), ahead of its Nayax cost, and is
    /// stored as Costed with an Estimated source so reports show it was not costed from real stock.
    /// </summary>
    [Fact]
    public async Task Latest_sales_sync_costs_a_sale_past_the_recorded_stock_at_the_latest_purchase_cost()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 20, Name = "Monster Original", QuantityInStock = 0 });
        db.InventoryCostTransitionBaselines.Add(
            Baseline(20, homeStockQuantity: 0, openingCostingQuantity: 0, inventoryValue: 0m));
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 20,
            Reason = StockAdjustmentReason.Restock,
            QuantityChange = 1,
            UnitCost = 2.19m,
            TotalCost = 2.19m,
            EffectiveAt = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine" } });
        var covered = LastSale(2001, 20, "Monster Original");
        var pastTheStock = LastSale(2002, 20, "Monster Original");
        pastTheStock.ProductCostPrice = 1.10m;
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport> { covered, pastTheStock });

        var outcome = await LatestSalesSync(db, nayax.Object).Handle();

        Assert.Null(outcome.RetryAfter);
        var sales = await db.NayaxSales.AsNoTracking().OrderBy(sale => sale.TransactionID).ToListAsync();
        Assert.Equal(2.19m, sales[0].UnitCostAtSale);
        Assert.Equal(SaleCostSource.InventoryLedger, sales[0].CostSource);
        Assert.Equal(2.19m, sales[1].UnitCostAtSale);
        Assert.Equal(2.19m, sales[1].CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Costed, sales[1].CostingStatus);
        Assert.Equal(SaleCostSource.Estimated, sales[1].CostSource);
        var product = await db.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == 20);
        Assert.Equal(0, product.CostingQuantity);
        Assert.Equal(0m, product.InventoryValue);
        Assert.Empty(await db.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
    }

    private static NayaxLastSalesReport LastSale(long transactionId, long nayaxProductId, string productName) =>
        new()
        {
            TransactionID = transactionId,
            MachineID = 1,
            MachineName = "Machine",
            NayaxProductId = nayaxProductId,
            ProductName = productName,
            SettlementValue = 3m,
            PaymentMethod = "Card",
            AuthorizationDateTimeGmt = new DateTime(2026, 10, 3, 19, 30, 0, DateTimeKind.Utc)
        };

    private static InventoryCostTransitionBaseline Baseline(
        long productId, int homeStockQuantity, int openingCostingQuantity, decimal inventoryValue) =>
        new()
        {
            ProductId = productId,
            HomeStockQuantity = homeStockQuantity,
            OpeningCostingQuantity = openingCostingQuantity,
            InventoryValue = inventoryValue,
            AverageUnitCost = openingCostingQuantity > 0 ? inventoryValue / openingCostingQuantity : 0m,
            CutoffAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    /// <summary>
    /// The latest-sales synchronization use case (issue #187) over the real
    /// <see cref="EfLatestNayaxSalesStore"/> adapter, so the historical-cost behaviour asserted here
    /// is exercised through the same dedup/costing path production uses.
    /// </summary>
    private static SyncLatestNayaxSales LatestSalesSync(AppDbContext db, INayaxLynxClient nayax)
    {
        var rebuild = TestCostingUseCases.Rebuild(db);
        return TestSalesSync.UseCase(
            nayax,
            new EfLatestNayaxSalesStore(
                db,
                TestCostingUseCases.CostSale(db, rebuild),
                rebuild,
                IdentityLinks(),
                TestClocks.Fixed));
    }

    private static AppDbContext CreateDb() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>
    /// The uploaded Nayax sales import use case (issue #301) over its real workbook reader and EF
    /// store, with a stubbed post-transition rebuild: these tests assert the imported historical
    /// cost and its provenance, which <see cref="TestCostingUseCases.CostSale"/> decides over the
    /// real inventory ledger, not the replay the import then triggers.
    /// </summary>
    private static ImportNayaxSales CreateSalesImport(AppDbContext db) =>
        new(
            new ClosedXmlNayaxSalesWorkbookReader(),
            new EfNayaxSalesImportStore(db),
            TestCostingUseCases.CostSale(db),
            new Mock<IRebuildProductCost>().Object,
            IdentityLinks());

    /// <summary>
    /// The links issue #732's backfill recorded for data that already existed - every Nayax product
    /// id names the local product with the same id - so these historical-cost cases keep asserting
    /// the same matches while both Nayax sale paths resolve a remote id through the link lookup
    /// (issue #745) instead of comparing it with <c>Product.Id</c>.
    /// </summary>
    private static Inventory.Application.Nayax.INayaxProductLinkStore IdentityLinks() =>
        InventoryApi.Tests.Application.Nayax.FakeNayaxProductLinkStore.EveryIdLinkedToItself();

    private static NayaxSales Sale(long id, long productId, decimal? nayaxCost, string paymentMethod) =>
        new()
        {
            TransactionID = id,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            ProductId = productId,
            NayaxProductId = productId,
            ProductName = "Snack",
            SettlementValue = 3m,
            PaymentMethod = paymentMethod,
            NayaxProductCostPrice = nayaxCost,
            MachineAuthorizationTime = new DateTime(2026, 9, 2, 14, 30, 0)
        };

    private static NayaxSalesFileInput Csv(string content) =>
        new("sales.csv", () => new MemoryStream(Encoding.UTF8.GetBytes(content)));
}
