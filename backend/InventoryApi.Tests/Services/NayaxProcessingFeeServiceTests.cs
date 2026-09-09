using System;
using System.Linq;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Services;

public class NayaxProcessingFeeServiceTests
{
    private static AppDbContext Db()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Estimates_completed_card_sales_with_gst_and_excludes_non_eligible_sales()
    {
        using var db = Db();
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = .17m });
        db.NayaxSales.AddRange(
            Sale(1, 1, "Credit Card", 12), Sale(2, 1, "Cash", 12), Sale(3, 1, "Credit Card", 55),
            Sale(4, 1, "Credit Card", 62), Sale(5, 1, "Credit Card", null));
        await db.SaveChangesAsync();

        var result = await new NayaxProcessingFeeService(db).GetProcessingFeesAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 1));

        Assert.Equal(.17m, result.EstimatedFeeExGst);
        Assert.Equal(.017m, result.EstimatedFeeGst);
        Assert.Equal(.187m, result.EstimatedFeeIncGst);
        Assert.Equal(1, result.EstimatedCardTransactionCount);
    }

    [Fact]
    public async Task Uses_actual_fee_for_covered_days_and_estimates_only_uncovered_days()
    {
        using var db = Db();
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = .17m });
        db.NayaxSales.AddRange(Sale(1, 1, "Credit Card", 12, 1), Sale(2, 1, "Credit Card", 12, 2));
        db.ImportedReimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2026, 9, 1), ReimbursementEndDate = new DateTime(2026, 9, 1),
            Fees = { new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 1m, TotalSumWithVat = 1.1m } }
        });
        await db.SaveChangesAsync();

        var result = await new NayaxProcessingFeeService(db).GetProcessingFeesAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 2));

        Assert.Equal(1m, result.ActualFeeExGst);
        Assert.Equal(.1m, result.ActualFeeGst);
        Assert.Equal(.17m, result.EstimatedFeeExGst);
        Assert.Equal(1.287m, result.TotalFeeIncGst);
        Assert.Equal(1, result.EstimatedCardTransactionCount);
    }

    [Fact]
    public async Task Uses_effective_dated_rate_and_device_fee_for_machine()
    {
        using var db = Db();
        db.NayaxProcessingFeeRates.AddRange(
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = .17m },
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 9, 2), FeeExGst = .19m });
        db.NayaxSales.AddRange(Sale(1, 10, "Credit Card", 12, 1), Sale(2, 10, "Credit Card", 12, 2), Sale(3, 11, "Credit Card", 12, 1));
        db.ImportedReimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2026, 9, 1), ReimbursementEndDate = new DateTime(2026, 9, 1),
            Devices = { new ImportedReimbursementDevice { MachineNumber = "10", ProcessingFee = .25m } }
        });
        await db.SaveChangesAsync();

        var machine = await new NayaxProcessingFeeService(db).GetProcessingFeesAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 2), 10);

        Assert.Equal(.25m, machine.ActualFeeExGst);
        Assert.Equal(.19m, machine.EstimatedFeeExGst);
        Assert.Equal(1, machine.EstimatedCardTransactionCount);
    }

    [Fact]
    public async Task Uses_effective_dated_rates_and_cash_has_no_fee()
    {
        using var db = Db();
        db.NayaxProcessingFeeRates.AddRange(
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = .17m },
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 7, 1), FeeExGst = .20m });
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 10, MachineID = 1, PaymentMethod = "Credit Card",
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineAuthorizationTime = new DateTime(2026, 6, 30)
            },
            new NayaxSales
            {
                TransactionID = 11, MachineID = 1, PaymentMethod = "Credit Card",
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineAuthorizationTime = new DateTime(2026, 7, 1)
            },
            new NayaxSales
            {
                TransactionID = 12, MachineID = 1, PaymentMethod = "Cash",
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineAuthorizationTime = new DateTime(2026, 7, 1)
            });
        await db.SaveChangesAsync();

        var service = new NayaxProcessingFeeService(db);
        var june = await service.GetProcessingFeesAsync(
            new DateTime(2026, 6, 30), new DateTime(2026, 6, 30));
        var july = await service.GetProcessingFeesAsync(
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 1));

        Assert.Equal(.17m, june.EstimatedFeeExGst);
        Assert.Equal(.20m, july.EstimatedFeeExGst);
        Assert.Equal(1, july.EstimatedCardTransactionCount);
    }

    [Fact]
    public async Task Missing_effective_rate_is_reported_instead_of_estimated_as_zero()
    {
        using var db = Db();
        db.NayaxSales.Add(Sale(20, 1, "Credit Card", NayaxTransactionStatusIds.Completed));
        await db.SaveChangesAsync();

        var result = await new NayaxProcessingFeeService(db).GetProcessingFeesAsync(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 1));

        Assert.True(result.HasMissingRates);
        Assert.Equal(1, result.MissingRateTransactionCount);
        Assert.Equal(0, result.EstimatedCardTransactionCount);
    }

    [Fact]
    public void ResolveNayaxFeeRate_is_independent_of_input_order()
    {
        var rates = new[]
        {
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 7, 1), FeeExGst = .20m },
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = .10m },
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 4, 1), FeeExGst = .15m },
        };

        var effectiveAt = new DateTime(2026, 5, 1);
        var expected = .15m;

        Assert.Equal(expected, EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates, effectiveAt)!.FeeExGst);
        Assert.Equal(expected, EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates.Reverse().ToArray(), effectiveAt)!.FeeExGst);
        Assert.Equal(expected, EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates.OrderByDescending(x => x.EffectiveFrom).ToArray(), effectiveAt)!.FeeExGst);
    }

    [Fact]
    public void ResolveNayaxFeeRate_uses_latest_applicable_rate()
    {
        var rates = new[]
        {
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = .10m },
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 4, 1), FeeExGst = .15m },
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 7, 1), FeeExGst = .20m },
        };

        var result = EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates, new DateTime(2026, 8, 1));

        Assert.Equal(.20m, result!.FeeExGst);
    }

    [Fact]
    public void ResolveNayaxFeeRate_ignores_future_rates()
    {
        var rates = new[]
        {
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = .10m },
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 4, 1), FeeExGst = .15m },
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 12, 1), FeeExGst = .30m },
        };

        var result = EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates, new DateTime(2026, 6, 1));

        Assert.Equal(.15m, result!.FeeExGst);
    }

    [Fact]
    public void ResolveNayaxFeeRate_uses_exact_effective_date()
    {
        var rates = new[]
        {
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 4, 1), FeeExGst = .15m },
        };

        var result = EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates, new DateTime(2026, 4, 1));

        Assert.Equal(.15m, result!.FeeExGst);
    }

    [Fact]
    public void ResolveNayaxFeeRate_returns_null_when_no_rate_applies()
    {
        var rates = new[]
        {
            new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 4, 1), FeeExGst = .15m },
        };

        var result = EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates, new DateTime(2026, 3, 1));

        Assert.Null(result);
    }

    private static NayaxSales Sale(long id, long machine, string paymentMethod, int? status, int day = 1) =>
        new()
        {
            TransactionID = id, MachineID = machine, PaymentMethod = paymentMethod, TransactionStatusId = status,
            SettlementValue = 1m, MachineAuthorizationTime = new DateTime(2026, 9, day)
        };
}
