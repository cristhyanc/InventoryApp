using Inventory.Domain.Reporting.Dashboard;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Dashboard;

public class DashboardReimbursementPolicyTests
{
    [Fact]
    public void Expected_reimbursement_is_card_sales_less_fees_including_gst()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 100m, FeesIncludingGst: 5m, ActualReimbursement: 95m, ImportedContainsRows: true));

        Assert.Equal(95m, result.ExpectedReimbursement);
    }

    [Fact]
    public void No_imported_rows_is_pending_regardless_of_difference()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 100m, FeesIncludingGst: 5m, ActualReimbursement: 0m, ImportedContainsRows: false));

        Assert.Equal("Pending", result.Status);
        Assert.False(result.IsReconciled);
    }

    [Fact]
    public void Difference_within_one_cent_tolerance_is_reconciled()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 100m, FeesIncludingGst: 5m, ActualReimbursement: 95.01m, ImportedContainsRows: true));

        Assert.Equal(0.01m, result.Difference);
        Assert.True(result.IsReconciled);
        Assert.Equal("Reconciled", result.Status);
    }

    [Fact]
    public void Difference_beyond_tolerance_needs_review()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 100m, FeesIncludingGst: 5m, ActualReimbursement: 90m, ImportedContainsRows: true));

        Assert.Equal(-5m, result.Difference);
        Assert.False(result.IsReconciled);
        Assert.Equal("Needs Review", result.Status);
    }

    [Fact]
    public void Actual_reimbursement_passes_through_unchanged()
    {
        var result = DashboardReimbursementPolicy.Calculate(new DashboardReimbursementInputs(
            CardSales: 200m, FeesIncludingGst: 10m, ActualReimbursement: 123.45m, ImportedContainsRows: true));

        Assert.Equal(123.45m, result.ActualReimbursement);
    }
}
