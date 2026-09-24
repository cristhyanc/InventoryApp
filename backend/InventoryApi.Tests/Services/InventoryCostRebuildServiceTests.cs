using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Services;

public class InventoryCostRebuildServiceTests
{
    [Fact]
    public async Task Rebuild_keeps_machine_refills_out_of_business_cost_quantity()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 12 });
        db.StockAdjustments.AddRange(
            Movement(1, 10, 2m, StockAdjustmentReason.Restock, Day(1)),
            Movement(1, -8, null, StockAdjustmentReason.MachineRefill, Day(2)),
            Movement(1, 10, 4m, StockAdjustmentReason.Restock, Day(3)));
        await db.SaveChangesAsync();

        await new InventoryCostRebuildService(db).RebuildAsync(1);
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(12, product.QuantityInStock);
        Assert.Equal(20, product.CostingQuantity);
        Assert.Equal(60m, product.InventoryValue);
        Assert.Equal(3m, product.AverageUnitCost);
        var refill = await db.StockAdjustments.SingleAsync(movement => movement.Reason == StockAdjustmentReason.MachineRefill);
        Assert.Equal(10, refill.CostingQuantityAfter);
        Assert.Equal(20m, refill.InventoryValueAfter);
        Assert.Equal(2m, refill.AverageUnitCostAfter);
    }

    [Fact]
    public async Task Rebuild_consumes_completed_sales_and_recosts_from_changed_date()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 20 });
        db.StockAdjustments.Add(Movement(1, 10, 2m, StockAdjustmentReason.Restock, Day(1)));
        db.NayaxSales.AddRange(Enumerable.Range(1, 8).Select(index => Sale(index, Day(1).AddMinutes(index))));
        db.StockAdjustments.Add(Movement(1, 10, 4m, StockAdjustmentReason.Restock, Day(2)));
        await db.SaveChangesAsync();

        var result = await new InventoryCostRebuildService(db).RebuildAsync(1, Day(1));
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(12, product.CostingQuantity);
        Assert.Equal(44m, product.InventoryValue);
        Assert.Equal(44m / 12m, product.AverageUnitCost);
        Assert.Equal(8, result.RecostedSaleCount);
        Assert.All(await db.NayaxSales.ToListAsync(), sale =>
        {
            Assert.Equal(2m, sale.UnitCostAtSale);
            Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
        });
    }

    [Fact]
    public async Task Rebuild_only_consumes_sales_when_machine_refills_precede_sales()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 2 });
        db.StockAdjustments.AddRange(
            Movement(1, 10, 2m, StockAdjustmentReason.Restock, Day(1)),
            Movement(1, -8, null, StockAdjustmentReason.MachineRefill, Day(2)));
        db.NayaxSales.AddRange(Sale(1, Day(3)), Sale(2, Day(3).AddMinutes(1)), Sale(3, Day(3).AddMinutes(2)));
        await db.SaveChangesAsync();

        await new InventoryCostRebuildService(db).RebuildAsync(1, Day(1));
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(2, product.QuantityInStock);
        Assert.Equal(7, product.CostingQuantity);
        Assert.Equal(14m, product.InventoryValue);
    }

    [Fact]
    public async Task Rebuild_applies_cost_only_purchase_edit()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 10 });
        var purchase = Movement(1, 10, 1m, StockAdjustmentReason.Restock, Day(1));
        db.StockAdjustments.Add(purchase);
        await db.SaveChangesAsync();
        var rebuild = new InventoryCostRebuildService(db);

        await rebuild.RebuildAsync(1, Day(1));
        purchase.UnitCost = 2m;
        await rebuild.RebuildAsync(1, Day(1));
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(20m, product.InventoryValue);
        Assert.Equal(2m, product.AverageUnitCost);
    }

    [Fact]
    public async Task Rebuild_historical_purchase_change_recosts_later_sales_and_purchases()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 20 });
        var opening = Movement(1, 10, 2m, StockAdjustmentReason.Restock, Day(1));
        db.StockAdjustments.AddRange(opening, Movement(1, 10, 4m, StockAdjustmentReason.Restock, Day(3)));
        db.NayaxSales.Add(Sale(1, Day(2)));
        await db.SaveChangesAsync();
        var rebuild = new InventoryCostRebuildService(db);

        await rebuild.RebuildAsync(1, Day(1));
        opening.UnitCost = 1m;
        await rebuild.RebuildAsync(1, Day(1));
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(19, product.CostingQuantity);
        Assert.Equal(49m, product.InventoryValue);
        Assert.Equal(49m / 19m, product.AverageUnitCost);
        Assert.Equal(1m, (await db.NayaxSales.SingleAsync()).CostOfGoodsSold);
    }

    [Fact]
    public async Task Dry_run_reports_missing_opening_without_mutating_product()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 10, AverageUnitCost = 2m });
        db.NayaxSales.Add(Sale(1, Day(2)));
        await db.SaveChangesAsync();

        var result = await new InventoryCostRebuildService(db).RebuildAsync(1, Day(1), dryRun: true);

        Assert.Contains(result.Issues, issue => issue.Code == "MissingOpening");
        var product = await db.Products.SingleAsync();
        Assert.Equal(10, product.QuantityInStock);
        Assert.Null(product.CostingQuantity);
        Assert.Null(product.InventoryValue);
    }

    [Fact]
    public async Task Rebuild_costs_historical_positive_correction_at_baseline_average()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 8 });
        db.InventoryCostTransitionBaselines.Add(Baseline(1, homeStockQuantity: 4, openingCostingQuantity: 8, inventoryValue: 14m, Day(1)));
        db.StockAdjustments.Add(Movement(1, 4, null, StockAdjustmentReason.Correction, Day(2)));
        await db.SaveChangesAsync();

        var result = await new InventoryCostRebuildService(db).RebuildAsync(1);
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        var correction = await db.StockAdjustments.SingleAsync();
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "UnknownCost");
        Assert.Equal(8, product.QuantityInStock);
        Assert.Equal(12, product.CostingQuantity);
        Assert.Equal(21m, product.InventoryValue);
        Assert.Equal(1.75m, product.AverageUnitCost);
        Assert.Equal(1.75m, correction.UnitCost);
        Assert.Equal(7m, correction.TotalCost);
    }

    [Fact]
    public async Task Rebuild_costs_negative_correction_at_historical_average()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 2 });
        db.InventoryCostTransitionBaselines.Add(Baseline(1, homeStockQuantity: 4, openingCostingQuantity: 8, inventoryValue: 14m, Day(1)));
        db.StockAdjustments.Add(Movement(1, -2, null, StockAdjustmentReason.Correction, Day(2)));
        await db.SaveChangesAsync();

        await new InventoryCostRebuildService(db).RebuildAsync(1);
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        var correction = await db.StockAdjustments.SingleAsync();
        Assert.Equal(6, product.CostingQuantity);
        Assert.Equal(10.50m, product.InventoryValue);
        Assert.Equal(1.75m, product.AverageUnitCost);
        Assert.Equal(1.75m, correction.UnitCost);
        Assert.Equal(3.50m, correction.TotalCost);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task Rebuild_reports_fatal_issue_for_correction_without_known_average(int quantityChange)
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 2 });
        db.InventoryCostTransitionBaselines.Add(Baseline(1, homeStockQuantity: 2, openingCostingQuantity: 0, inventoryValue: 0m, Day(1)));
        db.StockAdjustments.Add(Movement(1, quantityChange, null, StockAdjustmentReason.Correction, Day(2)));
        await db.SaveChangesAsync();

        var result = await new InventoryCostRebuildService(db).RebuildAsync(1, dryRun: true);

        Assert.Contains(result.Issues, issue => issue.Code == "UnknownCost");
        var correction = await db.StockAdjustments.SingleAsync();
        Assert.Null(correction.UnitCost);
        Assert.Null(correction.TotalCost);
    }

    private static AppDbContext CreateDb() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static StockAdjustment Movement(long productId, int quantity, decimal? unitCost, StockAdjustmentReason reason, DateTime effectiveAt) =>
        new()
        {
            ProductId = productId,
            QuantityChange = quantity,
            Reason = reason,
            UnitCost = unitCost,
            EffectiveAt = effectiveAt
        };

    private static InventoryCostTransitionBaseline Baseline(
        long productId,
        int homeStockQuantity,
        int openingCostingQuantity,
        decimal inventoryValue,
        DateTime cutoffAt) =>
        new()
        {
            ProductId = productId,
            HomeStockQuantity = homeStockQuantity,
            OpeningCostingQuantity = openingCostingQuantity,
            InventoryValue = inventoryValue,
            AverageUnitCost = openingCostingQuantity > 0 ? inventoryValue / openingCostingQuantity : 0m,
            CutoffAt = cutoffAt
        };

    private static NayaxSales Sale(long id, DateTime at) =>
        new()
        {
            TransactionID = id,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = 1,
            MachineAuthorizationTime = at
        };

    private static DateTime Day(int day) => new(2026, 1, day, 12, 0, 0, DateTimeKind.Utc);
}
