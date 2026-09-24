using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class SiteCommissionServiceTests
{
    [Fact]
    public async Task No_agreements_is_valid_zero_commission_and_keeps_sales_totals()
    {
        await using var db = CreateDb();
        AddSale(db, 1, 10, new DateTime(2025, 7, 15));
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db, (10, 91)).GetReportAsync(
            new DateTime(2025, 7, 15), new DateTime(2025, 7, 15), null)).Rows);

        Assert.Equal(10m, row.GrossSales);
        Assert.Equal(10m, row.CardSales);
        Assert.Equal(0m, row.CashSales);
        Assert.Equal(0m, row.CommissionDue);
        Assert.False(row.HasConfigurationGap);
        Assert.False(row.HasOverlap);
        Assert.Null(row.DataQuality);
    }

    [Fact]
    public async Task Agreement_gap_keeps_sales_totals_and_marks_configuration_incomplete()
    {
        await using var db = CreateDb();
        db.SiteCommissionAgreements.Add(Agreement(91, new DateTime(2025, 1, 1), new DateTime(2025, 6, 30), .10m));
        AddSale(db, 1, 10, new DateTime(2025, 7, 15));
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db, (10, 91)).GetReportAsync(
            new DateTime(2025, 7, 15), new DateTime(2025, 7, 15), null)).Rows);

        Assert.Equal(10m, row.GrossSales);
        Assert.Equal(10m, row.CardSales);
        Assert.Equal(0m, row.CommissionDue);
        Assert.True(row.HasConfigurationGap);
        Assert.Contains("Commission agreements exist but do not cover one or more sales.", row.DataQuality);
    }

    [Fact]
    public async Task Overlap_keeps_sales_totals_and_marks_configuration_incomplete()
    {
        await using var db = CreateDb();
        db.SiteCommissionAgreements.AddRange(
            Agreement(91, new DateTime(2025, 1, 1), null, .10m),
            Agreement(91, new DateTime(2025, 7, 1), null, .12m));
        AddSale(db, 1, 10, new DateTime(2025, 7, 15));
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db, (10, 91)).GetReportAsync(
            new DateTime(2025, 7, 15), new DateTime(2025, 7, 15), null)).Rows);

        Assert.Equal(10m, row.GrossSales);
        Assert.Equal(10m, row.CardSales);
        Assert.Equal(0m, row.CommissionDue);
        Assert.True(row.HasOverlap);
        Assert.Contains("Overlapping commission agreements cover one or more sales.", row.DataQuality);
    }

    [Fact]
    public async Task Multiple_valid_rates_are_complete_and_calculated_per_sale()
    {
        await using var db = CreateDb();
        db.SiteCommissionAgreements.AddRange(
            Agreement(91, new DateTime(2025, 1, 1), new DateTime(2025, 6, 30), .10m),
            Agreement(91, new DateTime(2025, 7, 1), null, .12m));
        AddSale(db, 1, 10, new DateTime(2025, 6, 15));
        AddSale(db, 2, 10, new DateTime(2025, 7, 15));
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db, (10, 91)).GetReportAsync(
            new DateTime(2025, 6, 1), new DateTime(2025, 7, 31), null)).Rows);

        Assert.Equal(20m, row.GrossSales);
        Assert.Equal(2.20m, row.CommissionDue);
        Assert.False(row.HasConfigurationGap);
        Assert.False(row.HasOverlap);
        Assert.True(row.UsesMultipleRates);
        Assert.Contains("Multiple commission rates were used in this period; the displayed rate is representative.", row.DataQuality);
    }

    [Fact]
    public async Task Machine_breakdown_keeps_sales_when_one_machine_has_a_gap()
    {
        await using var db = CreateDb();
        db.SiteCommissionAgreements.Add(Agreement(91, new DateTime(2025, 1, 1), new DateTime(2025, 6, 30), .10m));
        AddSale(db, 1, 10, new DateTime(2025, 6, 15));
        AddSale(db, 2, 11, new DateTime(2025, 7, 15));
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db, (10, 91), (11, 91)).GetReportAsync(
            new DateTime(2025, 6, 1), new DateTime(2025, 7, 31), null)).Rows);

        Assert.Equal(20m, row.GrossSales);
        Assert.Equal(2, row.Machines.Sum(x => x.TransactionCount));
        Assert.Equal(10m, Assert.Single(row.Machines, x => x.MachineId == 11).GrossSales);
        Assert.Equal(0m, Assert.Single(row.Machines, x => x.MachineId == 11).CommissionDue);
        Assert.Equal(1m, row.CommissionDue);
        Assert.True(row.HasConfigurationGap);
    }

    private static AppDbContext CreateDb() => TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SiteCommissionService Service(AppDbContext db, params (long MachineId, long SiteId)[] machines)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            machines.Select(x => new NayaxMachine { MachineID = x.MachineId, CustomerID = x.SiteId }).ToList());
        return new SiteCommissionService(db, nayax.Object);
    }

    private static void AddSale(AppDbContext db, long id, long machineId, DateTime date) => db.NayaxSales.Add(new NayaxSales
    {
        TransactionID = id,
        MachineID = machineId,
        SettlementValue = 10m,
        PaymentMethod = "Credit Card",
        TransactionStatusId = NayaxTransactionStatusIds.Completed,
        MachineAuthorizationTime = date
    });

    private static SiteCommissionAgreement Agreement(long siteId, DateTime from, DateTime? to, decimal rate) => new()
    {
        SiteId = siteId,
        EffectiveFrom = from,
        EffectiveTo = to,
        CommissionRate = rate,
        Basis = CommissionBasis.GrossSales
    };
}
