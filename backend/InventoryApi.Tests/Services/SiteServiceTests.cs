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
    public async Task Site_summary_returns_basic_overview_for_known_machine_data()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new AppDbContext(options);
        db.Products.Add(new Product { Id = 1, Name = "Low", UnitPrice = 1m, LowStockThreshold = 0 });
        db.NayaxSales.Add(new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 5m, MachineAuthorizationTime = DateTime.UtcNow, TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();

        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>
            {
                new() { MachineID = 10, CustomerID = 42, MachineName = "Pavillion Left" }
            });
        nayax.Setup(x => x.GetMachineProductsAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { MachineID = 10, NayaxProductID = 1, PAR = 5, MissingStockByMDB = 4, VendOutAlertThreshold = 1 }
            });

        var summary = Assert.Single(await new SiteService(db, nayax.Object, Mock.Of<IMachineService>()).GetAll());
        Assert.NotNull(summary);
        Assert.Equal(5m, summary.TodayRevenue);
    }
}
