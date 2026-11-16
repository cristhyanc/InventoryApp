using Inventory.Domain.Reporting.Gst;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Gst;

/// <summary>
/// The BAS-labelled summary of issue #1318. It renames the GST accounting aid's own figures onto the
/// labels a BAS reader looks for and decides one thing of its own - whether the estimated net GST is
/// payable, a credit or nil, or whether that question has no answer because nothing measured the
/// amount - so no presentation has to decide it.
/// </summary>
public class BasGstSummaryPolicyTests
{
    private static BasGstSummaryResult Summary(
        decimal totalSales = 110m,
        decimal gstOnSales = 10m,
        decimal purchaseInputGst = 0m,
        decimal operatingExpenseGst = 0m,
        decimal gstOnFees = 0m,
        bool purchaseGstIncomplete = false,
        bool salesIncomplete = false,
        bool feesIncomplete = false,
        bool feeGstIncludesEstimates = false,
        bool hasSalesAssumedTaxable = false) =>
        BasGstSummaryPolicy.Calculate(new BasGstSummaryInputs(
            TotalSalesIncludingGst: totalSales,
            GstOnSales: gstOnSales,
            PurchaseInputGst: purchaseInputGst,
            OperatingExpenseGst: operatingExpenseGst,
            GstOnFees: gstOnFees,
            PurchaseGstIncomplete: purchaseGstIncomplete,
            SalesIncomplete: salesIncomplete,
            FeesIncomplete: feesIncomplete,
            FeeGstIncludesEstimates: feeGstIncludesEstimates,
            HasSalesAssumedTaxable: hasSalesAssumedTaxable));

    [Fact]
    public void G1_is_the_periods_gst_inclusive_total_sales_including_gst_free_sales()
    {
        // G1 is the whole of the period's sales, not the taxable part: a GST-free sale is still a
        // sale, and leaving it out would understate total sales on the BAS.
        var result = Summary(totalSales: 136m, gstOnSales: 3m);

        Assert.Equal(136m, result.TotalSales);
    }

    [Fact]
    public void One_b_counts_each_credit_source_once()
    {
        // The issue's own worked example: 1A $30, with $8 of purchase GST, $4 of expense GST and $3
        // of fee GST, gives 1B $15 and $15 payable.
        var result = Summary(gstOnSales: 30m, purchaseInputGst: 8m, operatingExpenseGst: 4m, gstOnFees: 3m);

        Assert.Equal(30m, result.GstOnSales);
        Assert.Equal(15m, result.GstCredits);
        Assert.Equal(8m, result.PurchaseGstCredits);
        Assert.Equal(4m, result.OperatingExpenseGstCredits);
        Assert.Equal(3m, result.NayaxFeeGstCredits);
        Assert.Equal(15m, result.NetGst);
        Assert.Equal(BasNetGstOutcome.Payable, result.Outcome);
    }

    [Fact]
    public void Net_gst_is_always_one_a_minus_one_b()
    {
        // The identity the whole summary rests on, pinned over several shapes so a later change to
        // either side cannot quietly break it.
        foreach (var (sales, purchases, expenses, fees) in new[]
        {
            (30m, 8m, 4m, 3m), (0m, 0m, 0m, 0m), (1.05m, 0.55m, 0m, 0.01m), (12m, 40m, 2m, 1m)
        })
        {
            var result = Summary(gstOnSales: sales, purchaseInputGst: purchases,
                operatingExpenseGst: expenses, gstOnFees: fees);

            Assert.Equal(result.GstOnSales - result.GstCredits, result.NetGst);
        }
    }

    [Fact]
    public void Net_gst_matches_the_gst_accounting_aids_own_net_gst_for_the_same_figures()
    {
        // "Reuse the GST Accounting Aid calculation": the summary must never be a second net-GST
        // rule sitting beside the aid's.
        var aid = GstAccountingAidPolicy.Calculate(new GstAccountingAidInputs(
            TaxableSalesIncludingGst: 110m, GstOnSales: 10m, NayaxFeesExGst: 20m, GstOnFees: 2m,
            OperatingExpenseGst: 3m, PurchaseInputGst: 1m));

        var result = Summary(gstOnSales: 10m, purchaseInputGst: 1m, operatingExpenseGst: 3m, gstOnFees: 2m);

        Assert.Equal(aid.NetGst, result.NetGst);
        Assert.Equal(aid.GstCredits, result.GstCredits);
    }

    [Fact]
    public void Credits_above_gst_on_sales_are_an_estimated_credit_rather_than_a_negative_payable()
    {
        var result = Summary(gstOnSales: 10m, purchaseInputGst: 12m);

        Assert.Equal(-2m, result.NetGst);
        Assert.Equal(BasNetGstOutcome.Credit, result.Outcome);
    }

    [Fact]
    public void Exactly_offsetting_figures_are_nil_rather_than_payable_or_credit()
    {
        var result = Summary(gstOnSales: 10m, purchaseInputGst: 6m, operatingExpenseGst: 3m, gstOnFees: 1m);

        Assert.Equal(0m, result.NetGst);
        Assert.Equal(BasNetGstOutcome.Nil, result.Outcome);
    }

    [Fact]
    public void A_period_with_nothing_recorded_is_nil_rather_than_payable()
    {
        var result = Summary(totalSales: 0m, gstOnSales: 0m);

        Assert.Equal(0m, result.TotalSales);
        Assert.Equal(0m, result.NetGst);
        Assert.Equal(BasNetGstOutcome.Nil, result.Outcome);
        Assert.False(result.IsIncomplete);
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void Any_missing_or_estimated_evidence_makes_the_estimate_incomplete(
        bool purchaseGstIncomplete, bool salesIncomplete, bool feesIncomplete,
        bool feeGstIncludesEstimates, bool hasSalesAssumedTaxable)
    {
        var result = Summary(
            purchaseGstIncomplete: purchaseGstIncomplete,
            salesIncomplete: salesIncomplete,
            feesIncomplete: feesIncomplete,
            feeGstIncludesEstimates: feeGstIncludesEstimates,
            hasSalesAssumedTaxable: hasSalesAssumedTaxable);

        Assert.True(result.IsIncomplete);
    }

    [Fact]
    public void Complete_classifications_coverage_and_fee_evidence_leave_the_estimate_complete()
    {
        Assert.False(Summary(gstOnSales: 30m, purchaseInputGst: 8m).IsIncomplete);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_net_amount_no_coverage_measured_has_no_payable_credit_or_nil_answer(
        bool salesIncomplete, bool feesIncomplete)
    {
        // A period whose sales or fee coverage never accounted for it has nothing but an internal
        // zero here, and every presentation shows the coverage state in place of that zero
        // (issue #730). "Nil" beside "No provider data" would announce a nil BAS nobody measured,
        // so there is no outcome to spell at all.
        var result = Summary(totalSales: 0m, gstOnSales: 0m,
            salesIncomplete: salesIncomplete, feesIncomplete: feesIncomplete);

        Assert.Equal(0m, result.NetGst);
        Assert.Null(result.Outcome);
        Assert.True(result.IsIncomplete);
    }

    [Fact]
    public void An_incomplete_period_that_measured_a_real_net_amount_keeps_its_answer()
    {
        // Partial provider data is real data: the figure stays visible beside the state that says
        // it may not be all of it, so it keeps the answer that names which way it falls.
        var payable = Summary(gstOnSales: 30m, purchaseInputGst: 8m, salesIncomplete: true);
        var credit = Summary(gstOnSales: 10m, purchaseInputGst: 12m, feesIncomplete: true);

        Assert.Equal(BasNetGstOutcome.Payable, payable.Outcome);
        Assert.Equal(BasNetGstOutcome.Credit, credit.Outcome);
    }

    [Fact]
    public void A_covered_period_that_genuinely_owed_nothing_is_still_nil()
    {
        // The valid zero: coverage accounted for the whole period, so the zero is a measurement and
        // nil is the right answer. Only the unmeasured zero above loses its outcome.
        var result = Summary(totalSales: 0m, gstOnSales: 0m, purchaseGstIncomplete: true);

        Assert.Equal(0m, result.NetGst);
        Assert.Equal(BasNetGstOutcome.Nil, result.Outcome);
    }
}
