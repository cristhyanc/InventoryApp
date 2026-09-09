using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class MachineProfitabilityTests
{
    [Fact]
    public async Task Product_pricing_uses_site_agreement_instead_of_nayax_commission()
    {
        await using var db = Db();
        db.Products.Add(new Product { Id = 200, Name = "Product", AverageUnitCost = 2m });
        db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
        {
            SiteId = 91,
            EffectiveFrom = new DateTime(2020, 1, 1),
            CommissionRate = .10m,
            Basis = CommissionBasis.GrossSales
        });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate
        {
            EffectiveFrom = new DateTime(2020, 1, 1),
            FeeExGst = .20m
        });
        await db.SaveChangesAsync();

        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineAsync(1, default))
            .ReturnsAsync(new NayaxMachine { MachineID = 1, CustomerID = 91 });
        nayax.Setup(x => x.GetMachineProductsAsync(1, default))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new()
                {
                    NayaxProductID = 200,
                    RetailPrice = 10m,
                    CommissionValue = 20m
                }
            });

        var product = Assert.Single(await new MachineService(db, nayax.Object).GetMachineProducts(1));

        Assert.Equal(6.78m, product.SuggestedNetValue!.Value);
        Assert.Equal(5.55m, product.SuggestedPriceValue!.Value);
    }

    [Fact]
    public async Task Product_pricing_with_no_agreements_uses_valid_zero_commission()
    {
        await using var db = Db();
        db.Products.Add(new Product { Id = 200, Name = "Product", AverageUnitCost = 2m });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = DateTime.Today.AddYears(-1), FeeExGst = .20m });
        await db.SaveChangesAsync();

        var nayax = PreviewClient();
        var product = Assert.Single(await new MachineService(db, nayax.Object).GetMachineProducts(1));

        Assert.Equal(2.78m, product.SuggestedNetValue);
        Assert.Equal(4.44m, product.SuggestedPriceValue);
    }

    [Fact]
    public async Task Product_pricing_with_overlapping_agreements_is_unavailable()
    {
        await using var db = Db();
        db.Products.Add(new Product { Id = 200, Name = "Product", AverageUnitCost = 2m });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = DateTime.Today.AddYears(-1), FeeExGst = .20m });
        db.SiteCommissionAgreements.AddRange(
            new SiteCommissionAgreement { SiteId = 91, EffectiveFrom = DateTime.Today.AddYears(-1), CommissionRate = .10m, Basis = CommissionBasis.GrossSales },
            new SiteCommissionAgreement { SiteId = 91, EffectiveFrom = DateTime.Today.AddMonths(-1), CommissionRate = .12m, Basis = CommissionBasis.GrossSales });
        await db.SaveChangesAsync();

        var product = Assert.Single(await new MachineService(db, PreviewClient().Object).GetMachineProducts(1));

        Assert.Null(product.SuggestedNetValue);
        Assert.Null(product.SuggestedPriceValue);
    }

    [Fact]
    public async Task Missing_persisted_cogs_makes_machine_profit_unavailable()
    {
        await using var db = Db();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            MachineID = 1,
            SettlementValue = 5m,
            PaymentMethod = "Credit Card",
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineAuthorizationTime = DateTime.Now,
            CostOfGoodsSold = null
        });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate
        {
            EffectiveFrom = new DateTime(2020, 1, 1),
            FeeExGst = .10m
        });
        await db.SaveChangesAsync();

        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineAsync(1, default))
            .ReturnsAsync(new NayaxMachine { MachineID = 1, CustomerID = 91 });

        var machine = await new MachineService(db, nayax.Object).GetById(1);

        Assert.NotNull(machine);
        Assert.Null(machine.TodayDirectProfit);
        Assert.Contains("persisted COGS", machine.ProfitabilityStatus!);
    }

    private static AppDbContext Db() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Mock<INayaxLynxClient> PreviewClient()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineAsync(1, default)).ReturnsAsync(new NayaxMachine { MachineID = 1, CustomerID = 91 });
        nayax.Setup(x => x.GetMachineProductsAsync(1, default)).ReturnsAsync(new List<NayaxMachineProduct>
        {
            new() { NayaxProductID = 200, RetailPrice = 5m }
        });
        return nayax;
    }
}
