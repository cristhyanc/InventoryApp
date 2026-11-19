using Inventory.Domain.Reporting.Dashboard;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Dashboard;

public class DashboardReimbursementPolicyTests
{
    [Fact]
    public void Expected_reimbursement_is_card_sales_less_fees_including_gst()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 100m, FeesIncludingGst: 5m, ActualReimbursement: 95m));

        Assert.Equal(95m, result.ExpectedReimbursement);
    }

    /// <summary>
    /// Issue #1253: no payout lies wholly inside the range, so there is no actual reimbursement to
    /// compare. Nothing is compared, no difference is published, and the range is neither reconciled
    /// nor flagged for review - it is <c>Pending</c>, and the expected figure still stands on its own
    /// line.
    /// </summary>
    [Fact]
    public void No_actual_reimbursement_is_pending_with_no_difference_at_all()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 100m, FeesIncludingGst: 5m, ActualReimbursement: null));

        Assert.Equal("Pending", result.Status);
        Assert.False(result.IsReconciled);
        Assert.Null(result.ActualReimbursement);
        Assert.Null(result.Difference);
        Assert.Equal(95m, result.ExpectedReimbursement);
    }

    /// <summary>
    /// The issue's own case: a payout for 1-7 September asked about 7 September alone. The range cuts
    /// that payout's period, so the status says so instead of reporting "Needs Review" against a
    /// week's money - which is the false alarm issue #1253 exists to remove.
    /// </summary>
    [Fact]
    public void A_range_that_only_cuts_a_payout_period_says_so_instead_of_needing_review()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 100m, FeesIncludingGst: 2.75m, ActualReimbursement: null, HasPartlyCoveredPayout: true));

        Assert.Equal("Range cuts a payout period", result.Status);
        Assert.NotEqual("Needs Review", result.Status);
        Assert.False(result.IsReconciled);
        Assert.Null(result.ActualReimbursement);
        Assert.Null(result.Difference);
    }

    /// <summary>
    /// A partly covered payout explains an absent actual; it never changes a comparison that could
    /// be made. A range holding both compares the payouts it covers in full exactly as before.
    /// </summary>
    [Fact]
    public void A_partly_covered_payout_beside_a_fully_covered_one_does_not_change_the_comparison()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 100m, FeesIncludingGst: 5m, ActualReimbursement: 95m, HasPartlyCoveredPayout: true));

        Assert.Equal("Reconciled", result.Status);
        Assert.True(result.IsReconciled);
        Assert.Equal(0m, result.Difference);
    }

    /// <summary>
    /// A settlement that really paid nothing is a measured zero and is compared like any other
    /// amount - it is not the absent actual above, and the two are never read as each other.
    /// </summary>
    [Fact]
    public void An_actual_of_zero_is_compared_and_is_not_the_absent_case()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 0m, FeesIncludingGst: 0m, ActualReimbursement: 0m));

        Assert.Equal("Reconciled", result.Status);
        Assert.True(result.IsReconciled);
        Assert.Equal(0m, result.Difference);
        Assert.Equal(0m, result.ActualReimbursement);
    }

    [Fact]
    public void Difference_within_one_cent_tolerance_is_reconciled()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 100m, FeesIncludingGst: 5m, ActualReimbursement: 95.01m));

        Assert.Equal(0.01m, result.Difference);
        Assert.True(result.IsReconciled);
        Assert.Equal("Reconciled", result.Status);
    }

    [Fact]
    public void Difference_beyond_tolerance_needs_review()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 100m, FeesIncludingGst: 5m, ActualReimbursement: 90m));

        Assert.Equal(-5m, result.Difference);
        Assert.False(result.IsReconciled);
        Assert.Equal("Needs Review", result.Status);
    }

    [Fact]
    public void Actual_reimbursement_passes_through_unchanged()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 200m, FeesIncludingGst: 10m, ActualReimbursement: 123.45m));

        Assert.Equal(123.45m, result.ActualReimbursement);
    }
}
