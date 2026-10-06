using Inventory.Domain.Reporting.Reconciliation;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Reconciliation;

public class ReconciliationPeriodPolicyTests
{
    private static ReconciliationPeriodInputs Complete(
        decimal cardSales = 100m,
        int cardTransactionCount = 10,
        decimal reportedGross = 100m,
        int reportedCount = 10,
        decimal processingFeesExGst = 2m,
        decimal feeGst = 0.2m,
        decimal otherFees = 1m,
        decimal adjustments = 0m,
        decimal actualNetReimbursement = 96.8m,
        bool hasImported = true,
        bool warning = false,
        decimal tolerance = 0.01m) => new(
        cardSales, cardTransactionCount, reportedGross, reportedCount, processingFeesExGst, feeGst, otherFees,
        adjustments, actualNetReimbursement, hasImported, warning, tolerance);

    [Fact]
    public void Matching_gross_and_settlement_are_reconciled_with_zero_differences()
    {
        var result = ReconciliationPeriodPolicy.Calculate(Complete());

        Assert.Equal(0, result.CountDifference);
        Assert.Equal(0m, result.GrossDifference);
        Assert.Equal("Reconciled", result.GrossStatus);
        Assert.Equal(96.8m, result.ExpectedNetReimbursement);
        Assert.Equal(0m, result.SettlementDifference);
        Assert.Equal("Reconciled", result.SettlementStatus);
        Assert.Equal("Reconciled", result.OverallStatus);
    }

    [Fact]
    public void Gross_difference_exactly_at_tolerance_boundary_is_reconciled_on_both_sides()
    {
        var high = ReconciliationPeriodPolicy.Calculate(Complete(cardSales: 100.01m, reportedGross: 100m, tolerance: 0.01m));
        var low = ReconciliationPeriodPolicy.Calculate(Complete(cardSales: 99.99m, reportedGross: 100m, tolerance: 0.01m));

        Assert.Equal("Reconciled", high.GrossStatus);
        Assert.Equal("Reconciled", low.GrossStatus);
    }

    [Fact]
    public void Gross_difference_beyond_tolerance_is_a_mismatch()
    {
        var result = ReconciliationPeriodPolicy.Calculate(Complete(cardSales: 100.02m, reportedGross: 100m, tolerance: 0.01m));

        Assert.Equal("Mismatch", result.GrossStatus);
        Assert.Equal("Mismatch", result.OverallStatus);
    }

    [Fact]
    public void No_imported_reimbursement_reports_pending_regardless_of_difference()
    {
        var result = ReconciliationPeriodPolicy.Calculate(Complete(hasImported: false, reportedGross: 0m, reportedCount: 0,
            processingFeesExGst: 0m, feeGst: 0m, otherFees: 0m, actualNetReimbursement: 0m));

        Assert.Equal("Pending", result.GrossStatus);
        Assert.Equal("Pending", result.SettlementStatus);
        Assert.Equal("Pending", result.OverallStatus);
    }

    [Fact]
    public void Warning_flag_surfaces_as_warning_status_when_otherwise_reconciled()
    {
        var result = ReconciliationPeriodPolicy.Calculate(Complete(warning: true));

        Assert.Equal("Warning", result.GrossStatus);
        Assert.Equal("Warning", result.SettlementStatus);
        Assert.Equal("Warning", result.OverallStatus);
    }

    [Fact]
    public void Overall_status_prioritises_mismatch_over_pending_and_warning()
    {
        var result = ReconciliationPeriodPolicy.Calculate(Complete(
            cardSales: 200m, reportedGross: 100m, warning: true, tolerance: 0.01m));

        Assert.Equal("Mismatch", result.GrossStatus);
        Assert.Equal("Mismatch", result.OverallStatus);
    }

    [Fact]
    public void Gross_can_reconcile_while_settlement_mismatches_independently()
    {
        var result = ReconciliationPeriodPolicy.Calculate(Complete(
            cardSales: 100m, reportedGross: 100m, processingFeesExGst: 5m, actualNetReimbursement: 80m));

        Assert.Equal("Reconciled", result.GrossStatus);
        Assert.Equal("Mismatch", result.SettlementStatus);
        Assert.Equal("Mismatch", result.OverallStatus);
    }

    [Fact]
    public void Expected_net_subtracts_fees_and_adjustments_from_reported_gross()
    {
        var result = ReconciliationPeriodPolicy.Calculate(Complete(
            reportedGross: 100m, processingFeesExGst: 2m, otherFees: 1m, feeGst: 0.3m, adjustments: 0.5m,
            actualNetReimbursement: 96.2m));

        Assert.Equal(96.2m, result.ExpectedNetReimbursement);
        Assert.Equal(0m, result.SettlementDifference);
    }

    [Fact]
    public void Applying_the_policy_to_summed_period_facts_matches_summing_each_periods_own_result()
    {
        var periodA = Complete(cardSales: 60m, cardTransactionCount: 6, reportedGross: 58m, reportedCount: 5,
            processingFeesExGst: 1m, feeGst: 0.1m, otherFees: 0.5m, actualNetReimbursement: 56.4m);
        var periodB = Complete(cardSales: 40m, cardTransactionCount: 4, reportedGross: 42m, reportedCount: 4,
            processingFeesExGst: 0.8m, feeGst: 0.08m, otherFees: 0.2m, actualNetReimbursement: 40.9m);

        var resultA = ReconciliationPeriodPolicy.Calculate(periodA);
        var resultB = ReconciliationPeriodPolicy.Calculate(periodB);

        var summed = Complete(
            cardSales: periodA.CardSales + periodB.CardSales,
            cardTransactionCount: periodA.CardTransactionCount + periodB.CardTransactionCount,
            reportedGross: periodA.ReportedGross + periodB.ReportedGross,
            reportedCount: periodA.ReportedCount + periodB.ReportedCount,
            processingFeesExGst: periodA.ProcessingFeesExGst + periodB.ProcessingFeesExGst,
            feeGst: periodA.FeeGst + periodB.FeeGst,
            otherFees: periodA.OtherFees + periodB.OtherFees,
            actualNetReimbursement: periodA.ActualNetReimbursement + periodB.ActualNetReimbursement);
        var totalsResult = ReconciliationPeriodPolicy.Calculate(summed);

        Assert.Equal(resultA.GrossDifference + resultB.GrossDifference, totalsResult.GrossDifference);
        Assert.Equal(resultA.ExpectedNetReimbursement + resultB.ExpectedNetReimbursement, totalsResult.ExpectedNetReimbursement);
        Assert.Equal(resultA.SettlementDifference + resultB.SettlementDifference, totalsResult.SettlementDifference);
    }
}
