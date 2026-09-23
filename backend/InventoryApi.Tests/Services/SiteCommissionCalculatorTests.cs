using InventoryApi.Models;
using InventoryApi.Services;
using Xunit;

namespace InventoryApi.Tests.Services;

public class SiteCommissionCalculatorTests
{
    [Fact]
    public void Gross_sales_commission_uses_full_sale()
    {
        var agreement = Agreement(CommissionBasis.GrossSales, .10m);

        Assert.Equal(1m, SiteCommissionCalculator.CommissionAmount(
            agreement, 10m, NayaxPaymentType.Card));
    }

    [Fact]
    public void Card_sales_commission_excludes_cash()
    {
        var agreement = Agreement(CommissionBasis.CardSales, .10m);

        Assert.Equal(1m, SiteCommissionCalculator.CommissionAmount(
            agreement, 10m, NayaxPaymentType.Card));
        Assert.Equal(0m, SiteCommissionCalculator.CommissionAmount(
            agreement, 10m, NayaxPaymentType.Cash));
    }

    [Fact]
    public void Sales_ex_gst_commission_uses_existing_gst_calculation()
    {
        var agreement = Agreement(CommissionBasis.SalesExGst, .10m);

        Assert.Equal(1m, SiteCommissionCalculator.CommissionAmount(
            agreement, 11m, NayaxPaymentType.Card));
    }

    [Fact]
    public void Effective_agreement_changes_on_boundary_date()
    {
        var agreements = new[]
        {
            Agreement(CommissionBasis.GrossSales, .10m, new DateTime(2026, 1, 1), new DateTime(2026, 6, 30)),
            Agreement(CommissionBasis.GrossSales, .12m, new DateTime(2026, 7, 1))
        };

        Assert.Equal(.10m, EffectiveFinancialConfiguration.ResolveAgreement(
            agreements, 1, new DateTime(2026, 6, 30))!.CommissionRate);
        Assert.Equal(.12m, EffectiveFinancialConfiguration.ResolveAgreement(
            agreements, 1, new DateTime(2026, 7, 1))!.CommissionRate);
    }

    [Fact]
    public void Overlapping_agreements_are_rejected()
    {
        var agreements = new[]
        {
            Agreement(CommissionBasis.GrossSales, .10m, new DateTime(2026, 1, 1)),
            Agreement(CommissionBasis.GrossSales, .12m, new DateTime(2026, 7, 1))
        };

        Assert.Throws<InvalidOperationException>(() =>
            EffectiveFinancialConfiguration.ResolveAgreement(
                agreements, 1, new DateTime(2026, 7, 1)));
    }

    private static SiteCommissionAgreement Agreement(
        CommissionBasis basis,
        decimal rate,
        DateTime? from = null,
        DateTime? to = null) =>
        new()
        {
            SiteId = 1,
            EffectiveFrom = from ?? new DateTime(2026, 1, 1),
            EffectiveTo = to,
            CommissionRate = rate,
            Basis = basis
        };
}
