using Inventory.Application.Machines;
using Inventory.Application.Products;
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

public class MachineProfitabilityTests
{
    /// <summary>
    /// One pinned instant drives both the seeded sale timestamps and the dashboard clock, so a sale
    /// "just now" falls inside the Australia/Sydney business day the use case resolves (issue #310)
    /// whatever the host's own timezone is.
    /// </summary>
    private static readonly FixedSydneyTime Time = FixedSydneyTime.PinnedToNow();

    private static ListMachineProducts MachineProducts(AppDbContext db, INayaxLynxClient nayax) =>
        new(
            nayax,
            new EfProductCatalogStore(db),
            new ResolveMachineProductPricing(new EfSiteFactsStore(db), Time.Calendar));

    private static GetMachineDashboard MachineDashboard(AppDbContext db, INayaxLynxClient nayax) =>
        new(
            nayax,
            new EfMachineDashboardFactsStore(db, TestFinancialUseCases.ProcessingFees(db, Time.Calendar)),
            Time.Clock,
            Time.Calendar);

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

        var product = Assert.Single(await MachineProducts(db, nayax.Object).Handle(1, CancellationToken.None));

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
        var product = Assert.Single(await MachineProducts(db, nayax.Object).Handle(1, CancellationToken.None));

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

        var product = Assert.Single(
            await MachineProducts(db, PreviewClient().Object).Handle(1, CancellationToken.None));

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
            MachineAuthorizationTime = Time.NowUtc,
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

        var machine = await MachineDashboard(db, nayax.Object).Handle(1, CancellationToken.None);

        Assert.NotNull(machine);
        Assert.Null(machine.TodayDirectProfit);
        Assert.Contains("persisted COGS", machine.ProfitabilityStatus!);
    }

    private static AppDbContext Db() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
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
