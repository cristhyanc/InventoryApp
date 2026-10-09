using Inventory.Application.Sites;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Time;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Application.Nayax;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

using Inventory.Domain.FinancialConfiguration;

namespace InventoryApi.Tests.Services;

/// <summary>
/// Regression tests for the migrated site dashboard slice (issue #241):
/// <see cref="GetSiteSummaries"/>/<see cref="GetSiteProducts"/> over the real
/// <see cref="EfSiteFactsStore"/>, matching the former <c>InventoryApi.Services.SiteService</c> tests
/// this replaces exactly.
/// </summary>
public class SiteServiceTests
{
    /// <summary>
    /// One pinned instant drives both the seeded sale timestamps and the use cases' clock, so a sale
    /// "just now" falls inside the Australia/Sydney business day the use case resolves (issue #310)
    /// whatever the host's own timezone is and whatever real time the suite runs at.
    /// </summary>
    private static readonly FixedSydneyTime Time = FixedSydneyTime.PinnedToNow();

    [Fact]
    public async Task Site_summary_aggregates_all_machines_and_separates_low_and_empty_products()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = TestAppDbContext.Unrestricted(options);
        db.Products.AddRange(
            new Product { Id = 1, Name = "Low", UnitPrice = 1m, LowStockThreshold = 0 },
            new Product { Id = 2, Name = "Empty", UnitPrice = 1m, LowStockThreshold = 0 });
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 5m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = Time.NowUtc },
            new NayaxSales { TransactionID = 2, MachineID = 11, SettlementValue = 7m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = Time.NowUtc });
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

        var service = new GetSiteSummaries(
            nayax.Object, new EfSiteFactsStore(db), new SiteNameResolver(), Time.Clock, Time.Calendar);
        var summary = Assert.Single(await service.Handle(CancellationToken.None));

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
        using var db = TestAppDbContext.Unrestricted(options);
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
        var service = new GetSiteProducts(nayax.Object, new EfSiteFactsStore(db), Time.Calendar);

        var first = Assert.Single(await service.Handle(42, CancellationToken.None));
        rate.FeeExGst = .25m;
        await db.SaveChangesAsync();
        var second = Assert.Single(await service.Handle(42, CancellationToken.None));

        Assert.Equal(2.78m, first.EstimatedCardProfit!.Value);
        Assert.Equal(2.725m, second.EstimatedCardProfit!.Value);
        Assert.Equal(.055m, first.EstimatedCardProfit.Value - second.EstimatedCardProfit.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Product_preview_handles_no_agreement_and_overlap_without_throwing(bool overlapping)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = TestAppDbContext.Unrestricted(options);
        db.Products.Add(new Product { Id = 1, Name = "Product", AverageUnitCost = 2m });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = DateTime.Today.AddYears(-1), FeeExGst = .20m });
        if (overlapping)
            db.SiteCommissionAgreements.AddRange(
                new SiteCommissionAgreement { SiteId = 42, EffectiveFrom = DateTime.Today.AddYears(-1), CommissionRate = .10m, Basis = CommissionBasis.GrossSales },
                new SiteCommissionAgreement { SiteId = 42, EffectiveFrom = DateTime.Today.AddMonths(-1), CommissionRate = .12m, Basis = CommissionBasis.GrossSales });
        await db.SaveChangesAsync();

        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new NayaxMachine { MachineID = 10, CustomerID = 42 }]);
        nayax.Setup(x => x.GetMachineProductsAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync([new NayaxMachineProduct { MachineID = 10, NayaxProductID = 1, RetailPrice = 5m }]);

        var service = new GetSiteProducts(nayax.Object, new EfSiteFactsStore(db), Time.Calendar);
        var product = Assert.Single(await service.Handle(42, CancellationToken.None));

        Assert.Equal(overlapping ? null : 2.78m, product.EstimatedCardProfit);
    }
}
