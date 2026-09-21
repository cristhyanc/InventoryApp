using Inventory.Domain.Reporting.Bookkeeping;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Bookkeeping;

public class BookkeepingProfitPolicyTests
{
    private static BookkeepingProfitInputs WholeBusinessComplete(decimal sales = 100m, decimal cost = 40m,
        decimal cardSales = 80m, decimal feesIncludingGst = 5m, decimal siteCommission = 2m,
        decimal receiptCosts = 3m, decimal operatingExpenses = 1m) => new(
        Sales: sales,
        PartialCostOfGoods: cost,
        IsCogsComplete: true,
        CardSales: cardSales,
        FeesIncludingGst: feesIncludingGst,
        HasMissingFeeRates: false,
        IsMachineFiltered: false,
        CommissionCompleteForScope: true,
        SiteCommission: siteCommission,
        ReceiptCosts: receiptCosts,
        OperatingExpensesTotal: operatingExpenses,
        ImportedHasNetSettlement: false,
        ImportedNetSettlement: 0m);

    [Fact]
    public void Whole_business_complete_scope_returns_net_profit_and_margin_but_no_direct_profit()
    {
        var result = BookkeepingProfitPolicy.Calculate(WholeBusinessComplete());

        Assert.True(result.IsProfitComplete);
        Assert.Equal(60m, result.GrossProfit);
        Assert.Null(result.DirectProfit);
        Assert.Null(result.DirectMarginPercent);
        Assert.Equal(60m - 5m - 2m - 3m - 1m, result.NetProfit);
        Assert.NotNull(result.NetMarginPercent);
    }

    [Fact]
    public void Machine_filtered_scope_returns_direct_profit_but_never_net_profit()
    {
        var inputs = WholeBusinessComplete() with { IsMachineFiltered = true };

        var result = BookkeepingProfitPolicy.Calculate(inputs);

        Assert.Equal(60m - 5m - 2m - 1m, result.DirectProfit);
        Assert.NotNull(result.DirectMarginPercent);
        Assert.Null(result.NetProfit);
        Assert.Null(result.NetMarginPercent);
    }

    [Fact]
    public void Incomplete_cogs_makes_gross_and_all_profit_null_but_still_reports_gst_and_settlement()
    {
        var inputs = WholeBusinessComplete(sales: 110m) with { IsCogsComplete = false };

        var result = BookkeepingProfitPolicy.Calculate(inputs);

        Assert.Null(result.GrossProfit);
        Assert.False(result.IsProfitComplete);
        Assert.Null(result.NetProfit);
        Assert.Null(result.DirectProfit);
        Assert.Equal(10m, result.GstOnSales);
        Assert.Equal(80m - 5m, result.NetSettlement);
    }

    [Fact]
    public void Missing_fee_rates_make_profit_incomplete_even_when_cogs_is_complete()
    {
        var inputs = WholeBusinessComplete() with { HasMissingFeeRates = true };

        var result = BookkeepingProfitPolicy.Calculate(inputs);

        Assert.False(result.IsProfitComplete);
        Assert.Null(result.NetProfit);
        Assert.NotNull(result.GrossProfit);
    }

    [Fact]
    public void Incomplete_commission_coverage_for_scope_makes_profit_incomplete()
    {
        var inputs = WholeBusinessComplete() with { CommissionCompleteForScope = false };

        var result = BookkeepingProfitPolicy.Calculate(inputs);

        Assert.False(result.IsProfitComplete);
        Assert.Null(result.NetProfit);
    }

    [Fact]
    public void Net_settlement_uses_the_imported_value_when_present()
    {
        var inputs = WholeBusinessComplete() with { ImportedHasNetSettlement = true, ImportedNetSettlement = 123.45m };

        var result = BookkeepingProfitPolicy.Calculate(inputs);

        Assert.Equal(123.45m, result.NetSettlement);
    }

    [Fact]
    public void Net_settlement_falls_back_to_card_sales_minus_fees_when_not_imported()
    {
        var inputs = WholeBusinessComplete(cardSales: 80m, feesIncludingGst: 5m) with { ImportedHasNetSettlement = false };

        var result = BookkeepingProfitPolicy.Calculate(inputs);

        Assert.Equal(75m, result.NetSettlement);
    }

    [Fact]
    public void Gst_on_sales_is_zero_when_sales_are_zero()
    {
        var inputs = WholeBusinessComplete(sales: 0m, cost: 0m);

        var result = BookkeepingProfitPolicy.Calculate(inputs);

        Assert.Equal(0m, result.GstOnSales);
        Assert.Equal(0m, result.NetMarginPercent);
    }
}
