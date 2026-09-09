using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class MachineServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetById_Computes_Revenues()
    {
        using var db = CreateDbContext("mach_test");
        // seed product referenced by Nayax
        db.Products.Add(new Product { Id = 200, Name = "px", UnitPrice = 2m });
        await db.SaveChangesAsync();

        var nayaxMock = new Mock<INayaxLynxClient>();
        nayaxMock.Setup(m => m.GetMachineAsync(1, default))
            .ReturnsAsync(new NayaxMachine { MachineID = 1, MachineName = "M1" });

        nayaxMock.Setup(m => m.GetMachineProductsAsync(1, default))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new NayaxMachineProduct { NayaxProductID = 200, ProductName = "ProdX", RetailPrice = 5m, CommissionValue = 10 }
            });

        nayaxMock.Setup(m => m.GetMachineLastSalesAsync(1, default))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new NayaxLastSalesReport { MachineID = 1, ProductName = "ProdX", SettlementValue = 5m, MachineAuthorizationTime = System.DateTime.UtcNow }
            });

        IMachineService svc = new MachineService(db, nayaxMock.Object);
        var machine = await svc.GetById(1);
        Assert.NotNull(machine);
        Assert.Equal(1, machine.MachineID);
        Assert.True(machine.TodayGrossRevenue >= 0);
    }

    [Fact]
    public async Task Sales_import_recosts_an_updated_transaction()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        db.Products.Add(new Product { Id = 10, Name = "Snack", AverageUnitCost = 2m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10, QuantityChange = 10, QuantityAfter = 10, Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m, EffectiveAt = new DateTime(2026, 9, 1)
        });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1, MachineID = 1, NayaxProductId = 999, ProductName = "Unknown",
            SettlementValue = 5m, TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineAuthorizationTime = new DateTime(2026, 9, 2)
        });
        await db.SaveChangesAsync();

        var service = new MachineService(db, new Mock<INayaxLynxClient>().Object);
        await service.ImportNayaxSalesFromExcelAsync(Csv(
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,ProductName,MachineAuthorizationTime\n" +
            "1,12,1,10,5,Snack,2/9/2026 2:30:00 PM"));

        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(2m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
    }

    [Fact]
    public void Week_to_date_comparison_uses_same_elapsed_period()
    {
        var reference = new System.DateTime(2026, 9, 4, 14, 30, 0);

        var current = MachineService.GetWeekToDateRange(reference);
        var previous = MachineService.GetPreviousComparableWeekRange(reference);

        Assert.Equal(new System.DateTime(2026, 8, 31), current.Start);
        Assert.Equal(reference, current.End);
        Assert.Equal(new System.DateTime(2026, 8, 24), previous.Start);
        Assert.Equal(new System.DateTime(2026, 8, 28, 14, 30, 0), previous.End);
    }

    [Fact]
    public void Month_to_date_starts_at_the_first_local_day()
    {
        var range = MachineService.GetMonthToDateRange(new System.DateTime(2026, 9, 4, 14, 30, 0));

        Assert.Equal(new System.DateTime(2026, 9, 1), range.Start);
        Assert.Equal(new System.DateTime(2026, 9, 4, 14, 30, 0), range.End);
    }

    private static Microsoft.AspNetCore.Http.IFormFile Csv(string content)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return new Microsoft.AspNetCore.Http.FormFile(stream, 0, stream.Length, "file", "sales.csv");
    }
}
