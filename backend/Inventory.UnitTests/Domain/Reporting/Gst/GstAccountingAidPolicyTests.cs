using Inventory.Domain.Reporting.Gst;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Gst;

public class GstAccountingAidPolicyTests
{
    [Fact]
    public void Taxable_sales_is_sales_minus_gst_on_sales()
    {
        var result = GstAccountingAidPolicy.Calculate(new GstAccountingAidInputs(
            Sales: 110m, GstOnSales: 10m, NayaxFeesExGst: 5m, GstOnFees: 0.5m, OperatingExpenseGst: 0m));

        Assert.Equal(100m, result.TaxableSales);
    }

    [Fact]
    public void Taxable_fees_passes_through_the_bookkeeping_reports_ex_gst_fee_figure()
    {
        var result = GstAccountingAidPolicy.Calculate(new GstAccountingAidInputs(
            Sales: 110m, GstOnSales: 10m, NayaxFeesExGst: 5m, GstOnFees: 0.5m, OperatingExpenseGst: 0m));

        Assert.Equal(5m, result.TaxableFees);
    }

    [Fact]
    public void Net_gst_subtracts_fee_gst_and_operating_expense_gst_from_gst_on_sales()
    {
        var result = GstAccountingAidPolicy.Calculate(new GstAccountingAidInputs(
            Sales: 110m, GstOnSales: 10m, NayaxFeesExGst: 5m, GstOnFees: 1m, OperatingExpenseGst: 2m));

        Assert.Equal(7m, result.NetGst);
    }

    [Fact]
    public void Zero_sales_and_zero_gst_figures_produce_zero_taxable_sales_and_zero_net_gst()
    {
        var result = GstAccountingAidPolicy.Calculate(new GstAccountingAidInputs(
            Sales: 0m, GstOnSales: 0m, NayaxFeesExGst: 0m, GstOnFees: 0m, OperatingExpenseGst: 0m));

        Assert.Equal(0m, result.TaxableSales);
        Assert.Equal(0m, result.TaxableFees);
        Assert.Equal(0m, result.NetGst);
    }

    [Fact]
    public void Fee_and_operating_expense_gst_exceeding_sales_gst_produce_a_negative_net_gst()
    {
        var result = GstAccountingAidPolicy.Calculate(new GstAccountingAidInputs(
            Sales: 10m, GstOnSales: 1m, NayaxFeesExGst: 20m, GstOnFees: 2m, OperatingExpenseGst: 3m));

        Assert.Equal(-4m, result.NetGst);
    }
}
