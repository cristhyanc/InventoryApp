using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class NayaxTransactionStatusTests
{
    [Theory]
    [InlineData(12, NayaxTransactionStatus.Completed)]
    [InlineData(55, NayaxTransactionStatus.Pending)]
    [InlineData(80, NayaxTransactionStatus.Pending)]
    [InlineData(62, NayaxTransactionStatus.Refunded)]
    [InlineData(null, NayaxTransactionStatus.Unknown)]
    [InlineData(21, NayaxTransactionStatus.Unknown)]
    public void Classifier_maps_raw_status_ids(int? statusId, NayaxTransactionStatus expected)
    {
        Assert.Equal(expected, NayaxTransactionStatusClassifier.Classify(statusId));
    }

    [Fact]
    public async Task Pending_completed_reimport_updates_existing_transaction()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new AppDbContext(options);
        var client = new Mock<INayaxLynxClient>().Object;
        var service = new MachineService(db, client);

        await service.ImportNayaxSalesFromExcelAsync(File("TransactionID,TransactionStatusId,MachineID,SettlementValue,Quantity,MachineAuthorizationTime\n1,55,10,5,1,2/9/2026 2:30:00 PM"));
        await service.ImportNayaxSalesFromExcelAsync(File("TransactionID,TransactionStatusId,MachineID,SettlementValue,Quantity,MachineAuthorizationTime\n1,12,10,5,1,2/9/2026 2:30:00 PM"));

        var sale = Assert.Single(db.NayaxSales);
        Assert.Equal(12, sale.TransactionStatusId);
    }

    [Fact]
    public async Task Completed_sale_is_costed_from_historical_receipt_movement()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new AppDbContext(options);
        db.Products.Add(new Product { Id = 10, Name = "Snack", AverageUnitCost = 9m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10, QuantityChange = 10, QuantityAfter = 10,
            Reason = StockAdjustmentReason.Restock, UnitCost = 2.10m,
            TotalCost = 21m, EffectiveAt = new DateTime(2025, 8, 1)
        });
        await db.SaveChangesAsync();
        var sale = new NayaxSales
        {
            TransactionID = 1, TransactionStatusId = 12, MachineID = 1, NayaxProductId = 10,
            Quantity = 2, SettlementValue = 10m, MachineAuthorizationTime = new DateTime(2025, 8, 2)
        };
        var service = new SaleCostingService(db);

        await service.CostSaleAsync(sale);

        Assert.Equal(2.10m, sale.UnitCostAtSale);
        Assert.Equal(4.20m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
    }

    [Fact]
    public async Task Pending_sale_costing_dry_run_does_not_mutate_database()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new AppDbContext(options);
        db.Products.Add(new Product { Id = 10, Name = "Snack", AverageUnitCost = 2m });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1, TransactionStatusId = 12, MachineID = 1, NayaxProductId = 10,
            Quantity = 1, MachineAuthorizationTime = new DateTime(2025, 8, 2)
        });
        await db.SaveChangesAsync();

        var result = await new SaleCostingService(db).BackfillAsync(dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(SaleCostingStatus.Pending, (await db.NayaxSales.SingleAsync()).CostingStatus);
    }

    [Fact]
    public async Task Historical_cost_is_stable_after_a_later_purchase()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new AppDbContext(options);
        db.Products.Add(new Product { Id = 10, Name = "Snack", AverageUnitCost = 2.10m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10, QuantityChange = 10, QuantityAfter = 10,
            Reason = StockAdjustmentReason.Restock, UnitCost = 2.10m,
            EffectiveAt = new DateTime(2025, 8, 1)
        });
        var sale = new NayaxSales
        {
            TransactionID = 1, TransactionStatusId = 12, MachineID = 1, NayaxProductId = 10,
            Quantity = 1, MachineAuthorizationTime = new DateTime(2025, 8, 2)
        };
        db.NayaxSales.Add(sale);
        await db.SaveChangesAsync();
        var service = new SaleCostingService(db);
        await service.CostSaleAsync(sale);
        await db.SaveChangesAsync();

        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10, QuantityChange = 10, QuantityAfter = 20,
            Reason = StockAdjustmentReason.Restock, UnitCost = 4m,
            EffectiveAt = new DateTime(2025, 8, 3)
        });
        await db.SaveChangesAsync();

        Assert.Equal(2.10m, (await db.NayaxSales.SingleAsync()).UnitCostAtSale);
        Assert.Equal(2.10m, (await db.NayaxSales.SingleAsync()).CostOfGoodsSold);
    }

    private static IFormFile File(string csv)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        return new FormFile(stream, 0, stream.Length, "file", "sales.csv");
    }
}
