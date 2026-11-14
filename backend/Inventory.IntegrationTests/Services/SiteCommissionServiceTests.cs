using Inventory.Infrastructure.Data;
using Inventory.Application.Nayax;
using Inventory.Application.Commissions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Sites;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Models;
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

        var row = Assert.Single((await Report(Service(db, (10, 91)),
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

        var row = Assert.Single((await Report(Service(db, (10, 91)),
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

        var row = Assert.Single((await Report(Service(db, (10, 91)),
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

        var row = Assert.Single((await Report(Service(db, (10, 91)),
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

        var row = Assert.Single((await Report(Service(db, (10, 91), (11, 91)),
            new DateTime(2025, 6, 1), new DateTime(2025, 7, 31), null)).Rows);

        Assert.Equal(20m, row.GrossSales);
        Assert.Equal(2, row.Machines.Sum(x => x.TransactionCount));
        Assert.Equal(10m, Assert.Single(row.Machines, x => x.MachineId == 11).GrossSales);
        Assert.Equal(0m, Assert.Single(row.Machines, x => x.MachineId == 11).CommissionDue);
        Assert.Equal(1m, row.CommissionDue);
        Assert.True(row.HasConfigurationGap);
    }

    /// <summary>
    /// Issue #1251: a commission payment that was recorded before the app stopped recording them is
    /// still in the database, and the report no longer reads it. The row is commission due over the
    /// period's sales and nothing about payment - which is what makes the figure the same for every
    /// range that holds the same sales, instead of only for the range a payment was recorded for.
    /// </summary>
    [Fact]
    public async Task A_stored_payment_row_does_not_change_the_commission_the_report_states()
    {
        await using var db = CreateDb();
        db.SiteCommissionAgreements.Add(Agreement(91, new DateTime(2025, 1, 1), null, .10m, paymentDueDaysAfterPeriodEnd: 14));
        db.CommissionPayments.Add(new CommissionPayment
        {
            SiteId = 91,
            PeriodStart = new DateTime(2025, 7, 15),
            PeriodEnd = new DateTime(2025, 7, 15),
            PaymentDate = new DateTime(2025, 7, 15),
            Amount = 1m
        });
        AddSale(db, 1, 10, new DateTime(2025, 7, 15));
        await db.SaveChangesAsync();

        var row = Assert.Single((await Report(Service(db, (10, 91)),
            new DateTime(2025, 7, 15), new DateTime(2025, 7, 15), null)).Rows);

        Assert.Equal(10m, row.GrossSales);
        Assert.Equal(1m, row.CommissionDue);
        Assert.Equal(1, await db.CommissionPayments.CountAsync());
    }

    private static AppDbContext CreateDb() => TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static GetSiteCommissionReport Service(AppDbContext db, params (long MachineId, long SiteId)[] machines)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            machines.Select(x => new NayaxMachine { MachineID = x.MachineId, CustomerID = x.SiteId }).ToList());
        return new GetSiteCommissionReport(
            nayax.Object,
            new EfSiteCommissionStore(db),
            new SiteNameResolver());
    }

    private static Task<SiteCommissionReport> Report(
        GetSiteCommissionReport service, DateTime from, DateTime to, long? siteId) =>
        service.Handle(from, to, siteId, CancellationToken.None);

    private static void AddSale(AppDbContext db, long id, long machineId, DateTime date) => db.NayaxSales.Add(new NayaxSales
    {
        TransactionID = id,
        MachineID = machineId,
        SettlementValue = 10m,
        PaymentMethod = "Credit Card",
        TransactionStatusId = NayaxTransactionStatusIds.Completed,
        MachineAuthorizationTime = date
    });

    private static SiteCommissionAgreement Agreement(long siteId, DateTime from, DateTime? to, decimal rate, int? paymentDueDaysAfterPeriodEnd = null) => new()
    {
        SiteId = siteId,
        EffectiveFrom = from,
        EffectiveTo = to,
        CommissionRate = rate,
        Basis = CommissionBasis.GrossSales,
        PaymentDueDaysAfterPeriodEnd = paymentDueDaysAfterPeriodEnd
    };
}
