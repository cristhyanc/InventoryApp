using System;
using System.Collections.Generic;
using System.Threading;
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

public class SiteServiceTests
{
    [Fact]
    public async Task Site_summary_aggregates_all_machines_and_separates_low_and_empty_products()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new AppDbContext(options);
        db.Products.AddRange(
            new Product { Id = 1, Name = "Low", UnitPrice = 1m, LowStockThreshold = 0 },
            new Product { Id = 2, Name = "Empty", UnitPrice = 1m, LowStockThreshold = 0 });
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 5m, MachineAuthorizationTime = DateTime.Now },
            new NayaxSales { TransactionID = 2, MachineID = 11, SettlementValue = 7m, MachineAuthorizationTime = DateTime.Now });
        await db.SaveChangesAsync();

        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>
            {
                new() { MachineID = 10, CustomerID = 42, MachineName = "Pavillion Left" },
                new() { MachineID = 11, CustomerID = 42, MachineName = "Pavillion Right" }
            });
        nayax.Setup(x => x.GetMachineProductsAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { MachineID = 10, NayaxProductID = 1, PAR = 5, MissingStockByMDB = 4, VendOutAlertThreshold = 1 },
                new() { MachineID = 10, NayaxProductID = 2, PAR = 5, MissingStockByMDB = 5, VendOutAlertThreshold = 1 }
            });
        nayax.Setup(x => x.GetMachineProductsAsync(11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { MachineID = 11, NayaxProductID = 1, PAR = 5, MissingStockByMDB = 5, VendOutAlertThreshold = 1 },
                new() { MachineID = 11, NayaxProductID = 2, PAR = 5, MissingStockByMDB = 5, VendOutAlertThreshold = 1 }
            });

        ISiteService service = new SiteService(db, nayax.Object, Mock.Of<IMachineService>());
        var summary = Assert.Single(await service.GetAll());

        Assert.Equal(12m, summary.TodayRevenue);
        Assert.Equal(12m, summary.CurrentWeekRevenue);
        Assert.Equal(5m, summary.TotalStockPercentage);
        Assert.Equal(1, summary.LowProductCount);
        Assert.Equal(1, summary.EmptyProductCount);
    }

    [Fact]
    public async Task Configured_fee_changes_estimated_card_profit_without_hidden_literal()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new AppDbContext(options);
        db.Products.Add(new Product { Id = 1, Name = "Product", AverageUnitCost = 2m });
        db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
        {
            SiteId = 42,
            EffectiveFrom = new DateTime(2020, 1, 1),
            CommissionRate = 0m,
            Basis = CommissionBasis.GrossSales
        });
        var rate = new NayaxProcessingFeeRate
        {
            EffectiveFrom = new DateTime(2020, 1, 1),
            FeeExGst = .20m
        };
        db.NayaxProcessingFeeRates.Add(rate);
        await db.SaveChangesAsync();

        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>
            {
                new() { MachineID = 10, CustomerID = 42 }
            });
        nayax.Setup(x => x.GetMachineProductsAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { MachineID = 10, NayaxProductID = 1, RetailPrice = 5m }
            });
        var service = new SiteService(db, nayax.Object, Mock.Of<IMachineService>());

        var first = Assert.Single(await service.GetProducts(42));
        rate.FeeExGst = .25m;
        await db.SaveChangesAsync();
        var second = Assert.Single(await service.GetProducts(42));

        Assert.Equal(2.78m, first.EstimatedCardProfit!.Value);
        Assert.Equal(2.725m, second.EstimatedCardProfit!.Value);
        Assert.Equal(.055m, first.EstimatedCardProfit.Value - second.EstimatedCardProfit.Value);
    }
}
