using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

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
        db.Products.Add(new Product { Id = 10, Name = "Snack", UnitPrice = 3m, AverageUnitCost = 2m });
        await db.SaveChangesAsync();

        var result = await CreateImportService(db).ImportNayaxSalesFromExcelAsync(Csv(
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
        Assert.Equal(3m, product.UnitPrice);
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
        var importer = CreateImportService(db);

        await importer.ImportNayaxSalesFromExcelAsync(Csv(
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,ProductName,Product Cost Price,MachineAuthorizationTime\n" +
            "1001,12,1,10,3.00,Snack,1.20,2/9/2026 2:30:00 PM"));
        await importer.ImportNayaxSalesFromExcelAsync(Csv(
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

        await CreateImportService(db).ImportNayaxSalesFromExcelAsync(Csv(
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
        var rebuild = new Mock<IInventoryCostRebuildService>();
        rebuild.Setup(x => x.GetAverageUnitCostAtAsync(
                10, It.IsAny<DateTime>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.15m);
        var sale = Sale(1, 10, 1.20m, "Card");

        await new SaleCostingService(db, rebuild.Object).CostSaleAsync(sale);

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
        var rebuild = new Mock<IInventoryCostRebuildService>();
        rebuild.Setup(x => x.GetAverageUnitCostAtAsync(
                10, It.IsAny<DateTime>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        var sale = Sale(1, 10, 1.20m, "Card");

        await new SaleCostingService(db, rebuild.Object).CostSaleAsync(sale);

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

        await new SaleCostingService(db).CostSaleAsync(sale);

        Assert.Equal(1.25m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.NayaxTransactionExport, sale.CostSource);
    }

    [Fact]
    public async Task Declined_sale_keeps_raw_cost_without_normal_COGS()
    {
        await using var db = CreateDb();
        var sale = Sale(1, 10, 1.25m, "Card");
        sale.TransactionStatusId = NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed;

        await new SaleCostingService(db).CostSaleAsync(sale);

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

        await new SaleCostingService(db).CostSaleAsync(sale);

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

        await new SaleCostingService(db).CostSaleAsync(sale);

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
        var service = new SaleCostingService(db);

        var dryRun = await service.BackfillHistoricalCostsFromNayaxAsync(dryRun: true);

        Assert.Equal(100, dryRun.SalesReviewed);
        Assert.Equal(80, dryRun.SalesWithNayaxCost);
        Assert.Equal(80, dryRun.SalesWouldBeCosted);
        Assert.Equal(20, dryRun.SalesStillPending);
        Assert.All(await db.NayaxSales.ToListAsync(), sale => Assert.Equal(SaleCostingStatus.Pending, sale.CostingStatus));

        var applied = await service.BackfillHistoricalCostsFromNayaxAsync(dryRun: false);

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

        var nayaxFees = new NayaxProcessingFeeService(db);
        var siteCommissions = Mock.Of<ISiteCommissionService>();
        var getBookkeepingReport = new GetBookkeepingReport(new EfBookkeepingReportFactsProvider(db, nayaxFees, siteCommissions));
        var getDailyReport = new GetDailyReport(new EfDailyReportFactsProvider(db, nayaxFees));
        var getReconciliationReport = new GetReconciliationReport(new EfReconciliationReportFactsProvider(db));
        var getMachineProfitabilityReport = new GetMachineProfitabilityReport(new EfMachineProfitabilityReportFactsProvider(db, nayaxFees, siteCommissions));
        var getProductProfitabilityReport = new GetProductProfitabilityReport(new EfProductProfitabilityReportFactsProvider(db));
        var csv = Encoding.UTF8.GetString(await new ReportingService(db, nayaxFees,
            siteCommissions, getBookkeepingReport, getDailyReport, getReconciliationReport,
            getMachineProfitabilityReport, getProductProfitabilityReport).ExportCsvAsync(
            "transactions",
            new Inventory.Application.Reporting.Transactions.TransactionSalesFilterDto(
                From: new DateTime(2026, 9, 2), To: new DateTime(2026, 9, 2))));

        Assert.Contains("NayaxProductCostPrice", csv);
        Assert.Contains("UnitCostAtSale", csv);
        Assert.Contains("CostOfGoods", csv);
        Assert.Contains("CostingStatus", csv);
        Assert.Contains("CostSource", csv);
        Assert.Contains("Inventory Ledger", csv);
    }

    [Fact]
    public async Task Machine_lookup_refreshes_sales_without_erasing_imported_costs()
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
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
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
        var service = new MachineService(db, nayax.Object);

        await service.GetAll();

        nayax.Verify(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal("Snack", sale.ProductName);
        Assert.Equal(1.20m, sale.NayaxProductCostPrice);
        Assert.Equal(1.15m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ImportService CreateImportService(AppDbContext db) =>
        new(
            db,
            new Mock<IWebHostEnvironment>().Object,
            new Mock<ILogger<ImportService>>().Object,
            new Mock<INayaxLynxClient>().Object,
            new SaleCostingService(db),
            new Mock<IInventoryCostRebuildService>().Object);

    private static NayaxSales Sale(long id, long productId, decimal? nayaxCost, string paymentMethod) =>
        new()
        {
            TransactionID = id,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = productId,
            ProductName = "Snack",
            SettlementValue = 3m,
            PaymentMethod = paymentMethod,
            NayaxProductCostPrice = nayaxCost,
            MachineAuthorizationTime = new DateTime(2026, 9, 2, 14, 30, 0)
        };

    private static IFormFile Csv(string content)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return new FormFile(stream, 0, stream.Length, "file", "sales.csv");
    }
}
