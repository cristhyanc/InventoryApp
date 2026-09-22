using System;
using System.Collections.Generic;
using Inventory.Domain.Reporting.Transactions;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Transactions;

public class TransactionRowPolicyTests
{
    private static readonly DateTime SaleDate = new(2025, 8, 1);

    private static TransactionRowInputs CompletedCard(decimal sale = 10m, long? siteId = null,
        decimal? costOfGoodsSold = 4m, bool hasPersistedCost = true) =>
        new(sale, TransactionPaymentType.Card, TransactionSaleStatus.Completed, SaleDate, siteId, costOfGoodsSold, hasPersistedCost);

    [Fact]
    public void Completed_card_sale_with_an_effective_rate_is_estimated_and_reduces_direct_profit()
    {
        var rates = new[] { new EffectiveFeeRate(new DateTime(2025, 1, 1), 0.20m) };

        // A site with no configured agreement is a valid zero-commission case, isolating the fee effect below.
        var result = TransactionRowPolicy.Calculate(CompletedCard(siteId: 91), rates, []);

        Assert.Equal("Estimated", result.FeeSource);
        Assert.False(result.FeeUnavailable);
        Assert.Equal(0.20m, result.FeeExGst);
        Assert.Equal(0.02m, result.FeeGst);
        Assert.Equal(0.22m, result.FeeIncGst);
        Assert.Equal(6m, result.GrossProfit);
        Assert.Equal(5.78m, result.DirectProfit);
    }

    [Fact]
    public void Completed_card_sale_with_no_effective_rate_makes_the_fee_and_direct_profit_unavailable()
    {
        var result = TransactionRowPolicy.Calculate(CompletedCard(), [], []);

        Assert.Equal("Unavailable", result.FeeSource);
        Assert.True(result.FeeUnavailable);
        Assert.Null(result.FeeExGst);
        Assert.Null(result.FeeIncGst);
        Assert.Equal(6m, result.GrossProfit);
        Assert.Null(result.DirectProfit);
    }

    [Fact]
    public void Fee_rate_lookup_uses_the_latest_rate_effective_at_or_before_the_sale_date()
    {
        var rates = new[]
        {
            new EffectiveFeeRate(new DateTime(2025, 1, 1), 0.10m),
            new EffectiveFeeRate(new DateTime(2025, 7, 1), 0.25m),
            new EffectiveFeeRate(new DateTime(2025, 9, 1), 0.99m), // after the sale date; must not apply
        };

        var result = TransactionRowPolicy.Calculate(CompletedCard(), rates, []);

        Assert.Equal(0.25m, result.FeeExGst);
    }

    [Fact]
    public void Cash_sale_has_no_fee_and_is_not_applicable_rather_than_unavailable()
    {
        var sale = new TransactionRowInputs(5m, TransactionPaymentType.Cash, TransactionSaleStatus.Completed,
            SaleDate, null, 2m, true);

        var result = TransactionRowPolicy.Calculate(sale, [], []);

        Assert.Equal("Not applicable", result.FeeSource);
        Assert.False(result.FeeUnavailable);
        Assert.Equal(0m, result.FeeIncGst);
    }

    [Fact]
    public void Completed_unknown_payment_type_makes_the_fee_unavailable()
    {
        var sale = new TransactionRowInputs(5m, TransactionPaymentType.Unknown, TransactionSaleStatus.Completed,
            SaleDate, null, 2m, true);

        var result = TransactionRowPolicy.Calculate(sale, [new EffectiveFeeRate(new DateTime(2025, 1, 1), 0.2m)], []);

        Assert.Equal("Unavailable", result.FeeSource);
        Assert.True(result.FeeUnavailable);
    }

    [Fact]
    public void Non_completed_sale_has_no_fee_and_a_zero_not_null_commission_amount()
    {
        var sale = new TransactionRowInputs(9m, TransactionPaymentType.Card, TransactionSaleStatus.Pending,
            SaleDate, 91, null, false);

        var result = TransactionRowPolicy.Calculate(sale, [new EffectiveFeeRate(new DateTime(2025, 1, 1), 0.2m)],
            [new EffectiveCommissionAgreement(91, new DateTime(2025, 1, 1), null, TransactionCommissionBasis.GrossSales, 0.1m)]);

        Assert.Equal("Not applicable", result.FeeSource);
        Assert.Equal(0m, result.CommissionAmount);
        Assert.False(result.CommissionUnavailable);
        Assert.Null(result.GrossProfit);
        Assert.False(result.IsCosted);
    }

    [Fact]
    public void No_commission_agreement_for_the_site_is_a_valid_zero_commission_case()
    {
        var result = TransactionRowPolicy.Calculate(CompletedCard(siteId: 91), [], []);

        Assert.Equal(0m, result.CommissionAmount);
        Assert.False(result.CommissionUnavailable);
        Assert.Null(result.CommissionRate);
    }

    [Fact]
    public void Agreements_exist_for_the_site_but_none_cover_the_sale_date_makes_commission_unavailable()
    {
        var agreements = new[]
        {
            new EffectiveCommissionAgreement(91, new DateTime(2025, 9, 1), null, TransactionCommissionBasis.GrossSales, 0.1m),
        };

        var result = TransactionRowPolicy.Calculate(CompletedCard(siteId: 91), [], agreements);

        Assert.Null(result.CommissionAmount);
        Assert.True(result.CommissionUnavailable);
        Assert.False(result.HasOverlappingCommission);
    }

    [Fact]
    public void Overlapping_agreements_for_the_site_make_the_commission_and_direct_profit_unavailable()
    {
        var agreements = new[]
        {
            new EffectiveCommissionAgreement(91, new DateTime(2025, 1, 1), null, TransactionCommissionBasis.GrossSales, 0.1m),
            new EffectiveCommissionAgreement(91, new DateTime(2025, 1, 1), null, TransactionCommissionBasis.CardSales, 0.2m),
        };

        var result = TransactionRowPolicy.Calculate(CompletedCard(siteId: 91), [], agreements);

        Assert.True(result.HasOverlappingCommission);
        Assert.True(result.CommissionUnavailable);
        Assert.Null(result.CommissionAmount);
        Assert.Null(result.DirectProfit);
    }

    [Fact]
    public void Completed_sale_with_no_site_mapping_makes_commission_unavailable()
    {
        var result = TransactionRowPolicy.Calculate(CompletedCard(siteId: null), [], []);

        Assert.True(result.CommissionUnavailable);
        Assert.Null(result.CommissionAmount);
    }

    [Fact]
    public void Gross_sales_basis_applies_the_rate_to_the_full_sale_amount()
    {
        var agreements = new[] { new EffectiveCommissionAgreement(91, new DateTime(2025, 1, 1), null, TransactionCommissionBasis.GrossSales, 0.1m) };

        var result = TransactionRowPolicy.Calculate(CompletedCard(sale: 10m, siteId: 91), [], agreements);

        Assert.Equal(1m, result.CommissionAmount);
    }

    [Fact]
    public void Card_sales_basis_applies_the_rate_to_a_card_sale()
    {
        var agreements = new[] { new EffectiveCommissionAgreement(91, new DateTime(2025, 1, 1), null, TransactionCommissionBasis.CardSales, 0.1m) };

        var result = TransactionRowPolicy.Calculate(CompletedCard(sale: 10m, siteId: 91), [], agreements);

        Assert.Equal(1m, result.CommissionAmount);
    }

    [Fact]
    public void Card_sales_basis_excludes_a_cash_sale_from_commission()
    {
        var sale = new TransactionRowInputs(10m, TransactionPaymentType.Cash, TransactionSaleStatus.Completed,
            SaleDate, 91, 4m, true);
        var agreements = new[] { new EffectiveCommissionAgreement(91, new DateTime(2025, 1, 1), null, TransactionCommissionBasis.CardSales, 0.1m) };

        var result = TransactionRowPolicy.Calculate(sale, [], agreements);

        Assert.Equal(0m, result.CommissionAmount);
    }

    [Fact]
    public void Uncosted_completed_sale_has_no_gross_or_direct_profit()
    {
        var result = TransactionRowPolicy.Calculate(CompletedCard(costOfGoodsSold: null, hasPersistedCost: false), [], []);

        Assert.False(result.IsCosted);
        Assert.Null(result.GrossProfit);
        Assert.Null(result.DirectProfit);
    }

    [Fact]
    public void Direct_profit_requires_fee_commission_and_gross_profit_to_all_be_available()
    {
        var rates = new[] { new EffectiveFeeRate(new DateTime(2025, 1, 1), 0.20m) };
        var agreements = new[] { new EffectiveCommissionAgreement(91, new DateTime(2025, 1, 1), null, TransactionCommissionBasis.GrossSales, 0.1m) };

        var result = TransactionRowPolicy.Calculate(CompletedCard(sale: 10m, siteId: 91), rates, agreements);

        Assert.Equal(6m, result.GrossProfit);
        Assert.Equal(1m, result.CommissionAmount);
        Assert.Equal(0.22m, result.FeeIncGst);
        Assert.Equal(4.78m, result.DirectProfit);
        Assert.Equal(47.8m, result.DirectMarginPercent);
    }
}
