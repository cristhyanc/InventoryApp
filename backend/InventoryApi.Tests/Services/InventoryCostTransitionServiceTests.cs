using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class InventoryCostTransitionServiceTests
{
    [Fact]
    public async Task Preview_uses_home_plus_each_machine_and_reports_legacy_discrepancy()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack", QuantityInStock = 19, AverageUnitCost = 1.25m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = 31,
            Reason = StockAdjustmentReason.Restock,
            EffectiveAt = DateTime.UtcNow.AddDays(-10)
        });
        await db.SaveChangesAsync();
        var nayax = NayaxWithStock(10);
        var service = new InventoryCostTransitionService(
            db, nayax.Object, new InventoryCostRebuildService(db));

        var preview = await service.PreviewAsync(new(
            10, 1.25m, InventoryCostBaselineSource.ManualAuthoritative));

        Assert.Equal(19, preview.HomeStockQuantity);
        Assert.Equal(11, preview.MachineStockQuantity);
        Assert.Equal(30, preview.OpeningCostingQuantity);
        Assert.Equal(37.50m, preview.InventoryValue);
        Assert.Equal(31, preview.LegacyReplayedPhysicalQuantity);
        Assert.Equal(-12, preview.LegacyPhysicalDiscrepancy);
        Assert.Collection(
            preview.MachineStocks,
            stock => Assert.Equal(6, stock.StockQuantity),
            stock => Assert.Equal(5, stock.StockQuantity));
    }

    [Fact]
    public async Task Apply_preserves_legacy_history_and_rebuilds_only_after_cutoff()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack", QuantityInStock = 19, AverageUnitCost = 1.25m });
        var legacyRestock = new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = 31,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = null,
            EffectiveAt = DateTime.UtcNow.AddDays(-10)
        };
        var historicalSale = new NayaxSales
        {
            TransactionID = 1,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = 10,
            MachineAuthorizationTime = DateTime.UtcNow.AddDays(-5),
            NayaxProductCostPrice = 1.10m,
            UnitCostAtSale = 1.10m,
            CostOfGoodsSold = 1.10m,
            CostingStatus = SaleCostingStatus.Costed,
            CostSource = SaleCostSource.NayaxTransactionExport
        };
        db.AddRange(legacyRestock, historicalSale);
        await db.SaveChangesAsync();
        var nayax = NayaxWithStock(10);
        var rebuild = new InventoryCostRebuildService(db);
        var service = new InventoryCostTransitionService(db, nayax.Object, rebuild);
        var preview = await service.PreviewAsync(new(
            10, 1.25m, InventoryCostBaselineSource.ManualAuthoritative));

        await service.ApplyAsync(new(preview.PreviewId, Confirmed: true));

        var baseline = await db.InventoryCostTransitionBaselines
            .Include(x => x.MachineStocks)
            .SingleAsync();
        Assert.Equal(30, baseline.OpeningCostingQuantity);
        Assert.Equal(-12, baseline.LegacyPhysicalDiscrepancy);
        Assert.Null((await db.StockAdjustments.SingleAsync()).UnitCost);
        Assert.Equal(1.10m, historicalSale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.NayaxTransactionExport, historicalSale.CostSource);

        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            ReceiptItemId = 99,
            QuantityChange = 10,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m,
            EffectiveAt = preview.CutoffAt.AddMinutes(1)
        });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = -5,
            Reason = StockAdjustmentReason.MachineRefill,
            EffectiveAt = preview.CutoffAt.AddMinutes(1).AddSeconds(30)
        });
        var newSale = new NayaxSales
        {
            TransactionID = 2,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = 10,
            MachineAuthorizationTime = preview.CutoffAt.AddMinutes(2)
        };
        db.NayaxSales.Add(newSale);
        await db.SaveChangesAsync();

        await rebuild.RebuildAsync(10, preview.CutoffAt);
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(24, product.QuantityInStock);
        Assert.Equal(39, product.CostingQuantity);
        Assert.Equal(1.4375m, newSale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, newSale.CostSource);
        Assert.Equal(1.10m, historicalSale.CostOfGoodsSold);
    }

    [Fact]
    public async Task Historical_sale_before_cutoff_uses_nayax_cost_instead_of_legacy_ledger()
    {
        await using var db = CreateDb();
        var cutoff = DateTime.UtcNow;
        db.Products.Add(new Product { Id = 10, Name = "Snack", QuantityInStock = 5, AverageUnitCost = 2m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = 10,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m,
            EffectiveAt = cutoff.AddDays(-2)
        });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = 10,
            CutoffAt = cutoff,
            HomeStockQuantity = 5,
            MachineStockQuantity = 5,
            OpeningCostingQuantity = 10,
            AverageUnitCost = 2m,
            InventoryValue = 20m,
            CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            DataQualityNote = "Legacy history retired."
        });
        await db.SaveChangesAsync();
        var sale = new NayaxSales
        {
            TransactionID = 3,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = 10,
            MachineAuthorizationTime = cutoff.AddDays(-1),
            NayaxProductCostPrice = 1.10m
        };

        await new SaleCostingService(db).CostSaleAsync(sale);

        Assert.Equal(1.10m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.NayaxTransactionExport, sale.CostSource);
    }

    [Fact]
    public async Task Live_post_cutoff_sale_advances_business_inventory_ledger()
    {
        await using var db = CreateDb();
        var cutoff = DateTime.UtcNow.AddHours(-1);
        db.Products.Add(new Product
        {
            Id = 10,
            Name = "Snack",
            QuantityInStock = 5,
            CostingQuantity = 10,
            InventoryValue = 20m,
            AverageUnitCost = 2m
        });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = 10,
            CutoffAt = cutoff,
            HomeStockQuantity = 5,
            MachineStockQuantity = 5,
            OpeningCostingQuantity = 10,
            AverageUnitCost = 2m,
            InventoryValue = 20m,
            CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            DataQualityNote = "Transition."
        });
        await db.SaveChangesAsync();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new()
                {
                    TransactionID = 4,
                    MachineID = 1,
                    ProductName = "Snack",
                    SettlementValue = 4m,
                    MachineAuthorizationTime = cutoff.AddMinutes(30)
                }
            });
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 10, ProductName = "Snack", PAR = 5, MissingStockByMDB = 1 }
            });
        var rebuild = new InventoryCostRebuildService(db);
        var service = new MachineService(
            db,
            nayax.Object,
            new SaleCostingService(db, rebuild),
            inventoryCostRebuild: rebuild);

        await service.GetAll();

        var product = await db.Products.SingleAsync();
        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(9, product.CostingQuantity);
        Assert.Equal(18m, product.InventoryValue);
        Assert.Equal(10, sale.NayaxProductId);
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal(2m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
    }

    [Fact]
    public async Task Preview_and_apply_all_creates_one_atomic_baseline_per_eligible_product()
    {
        await using var db = CreateDb();
        db.Products.AddRange(
            new Product { Id = 10, Name = "Snack", QuantityInStock = 4, AverageUnitCost = 1.50m },
            new Product { Id = 20, Name = "Drink", QuantityInStock = 6, AverageUnitCost = 2.00m });
        await db.SaveChangesAsync();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 10, PAR = 8, MissingStockByMDB = 3 },
                new() { NayaxProductID = 20, PAR = 10, MissingStockByMDB = 2 }
            });
        var service = new InventoryCostTransitionService(
            db, nayax.Object, new InventoryCostRebuildService(db));

        var preview = await service.PreviewAllAsync(
            new(InventoryCostBaselineSource.ManualAuthoritative));

        Assert.Equal(2, preview.ProductCount);
        Assert.Equal(10, preview.HomeStockQuantity);
        Assert.Equal(13, preview.MachineStockQuantity);
        Assert.Equal(23, preview.OpeningCostingQuantity);
        Assert.Equal(41.50m, preview.InventoryValue);

        await service.ApplyAllAsync(new(preview.PreviewId, Confirmed: true));

        Assert.Equal(2, await db.InventoryCostTransitionBaselines.CountAsync());
        var products = await db.Products.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(9, products[0].CostingQuantity);
        Assert.Equal(14, products[1].CostingQuantity);
        Assert.Equal(13.50m, products[0].InventoryValue);
        Assert.Equal(28m, products[1].InventoryValue);
    }

    [Fact]
    public async Task Live_sales_defaults_missing_status_for_existing_rows_without_overwriting_known_status()
    {
        await using var db = CreateDb();
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 40,
                MachineID = 1,
                TransactionStatusId = null,
                NayaxProductCostPrice = 1m,
                MachineAuthorizationTime = DateTime.UtcNow
            },
            new NayaxSales
            {
                TransactionID = 41,
                MachineID = 1,
                TransactionStatusId = NayaxTransactionStatusIds.CancelledOrDeclined31,
                MachineAuthorizationTime = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1 } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new() { TransactionID = 40, MachineID = 1, MachineAuthorizationTime = DateTime.UtcNow, SettlementValue = 1 },
                new() { TransactionID = 41, MachineID = 1, MachineAuthorizationTime = DateTime.UtcNow, SettlementValue = 1 },
                new() { TransactionID = 42, MachineID = 1, MachineAuthorizationTime = DateTime.UtcNow }
            });
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>());

        await new MachineService(db, nayax.Object).GetAll();

        Assert.Equal(
            NayaxTransactionStatusIds.Completed,
            (await db.NayaxSales.FindAsync(40L))!.TransactionStatusId);
        Assert.Equal(
            NayaxTransactionStatusIds.CancelledOrDeclined31,
            (await db.NayaxSales.FindAsync(41L))!.TransactionStatusId);
        Assert.Equal(
            NayaxTransactionStatusIds.CancelledOrDeclined250,
            (await db.NayaxSales.FindAsync(42L))!.TransactionStatusId);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Mock<INayaxLynxClient> NayaxWithStock(long productId)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>
            {
                new() { MachineID = 1, MachineName = "Machine A" },
                new() { MachineID = 2, MachineName = "Machine B" }
            });
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = productId, PAR = 10, MissingStockByMDB = 4 }
            });
        nayax.Setup(x => x.GetMachineProductsAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = productId, PAR = 8, MissingStockByMDB = 3 }
            });
        return nayax;
    }
}
