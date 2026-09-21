using Inventory.Domain.Reporting;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting;

public class ReconciliationStatusPolicyTests
{
    [Theory]
    [InlineData(0.01, 0.01, true)]
    [InlineData(-0.01, 0.01, true)]
    [InlineData(0.02, 0.01, false)]
    [InlineData(-0.02, 0.01, false)]
    [InlineData(0, 0.01, true)]
    public void IsReconciled_uses_absolute_difference_within_tolerance(decimal difference, decimal tolerance, bool expected)
    {
        Assert.Equal(expected, ReconciliationStatusPolicy.IsReconciled(difference, tolerance));
    }

    [Fact]
    public void StatusFor_reports_pending_regardless_of_difference()
    {
        Assert.Equal("Pending", ReconciliationStatusPolicy.StatusFor(pending: true, difference: 0m, tolerance: 0.01m, warning: false));
        Assert.Equal("Pending", ReconciliationStatusPolicy.StatusFor(pending: true, difference: 999m, tolerance: 0.01m, warning: true));
    }

    [Fact]
    public void StatusFor_reports_mismatch_when_difference_exceeds_tolerance()
    {
        Assert.Equal("Mismatch", ReconciliationStatusPolicy.StatusFor(pending: false, difference: 5m, tolerance: 0.01m, warning: false));
    }

    [Fact]
    public void StatusFor_reports_warning_when_reconciled_but_flagged()
    {
        Assert.Equal("Warning", ReconciliationStatusPolicy.StatusFor(pending: false, difference: 0m, tolerance: 0.01m, warning: true));
    }

    [Fact]
    public void StatusFor_reports_reconciled_when_within_tolerance_and_no_warning()
    {
        Assert.Equal("Reconciled", ReconciliationStatusPolicy.StatusFor(pending: false, difference: 0m, tolerance: 0.01m, warning: false));
    }

    [Fact]
    public void StatusFor_mismatch_takes_priority_over_warning()
    {
        Assert.Equal("Mismatch", ReconciliationStatusPolicy.StatusFor(pending: false, difference: 5m, tolerance: 0.01m, warning: true));
    }
}
