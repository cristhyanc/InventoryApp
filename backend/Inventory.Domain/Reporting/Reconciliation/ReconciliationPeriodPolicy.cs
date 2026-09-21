namespace Inventory.Domain.Reporting.Reconciliation;

/// <summary>
/// Already-aggregated facts for one reconciliation period (a matched imported reimbursement
/// period, or the single fallback period used when none matched) needed to compute its gross and
/// settlement difference/status. Contains no EF query shapes or Include graphs: those live in the
/// Application-owned facts port and its temporary EF adapter.
/// </summary>
public readonly record struct ReconciliationPeriodInputs(
    decimal CardSales,
    int CardTransactionCount,
    decimal ReportedGross,
    int ReportedCount,
    decimal ProcessingFeesExGst,
    decimal FeeGst,
    decimal OtherFees,
    decimal Adjustments,
    decimal ActualNetReimbursement,
    bool HasImported,
    bool Warning,
    decimal Tolerance);

public readonly record struct ReconciliationPeriodResult(
    int CountDifference,
    decimal GrossDifference,
    string GrossStatus,
    decimal ExpectedNetReimbursement,
    decimal SettlementDifference,
    string SettlementStatus,
    string OverallStatus);

/// <summary>
/// Deterministic reconciliation calculation shared by every period row and the report's totals
/// row: because expected-net and difference are linear in their inputs, applying this same policy
/// to summed period facts yields the same totals as summing each period's own result, so one
/// policy computes both without duplicating the formula. Reuses the shared
/// <see cref="ReconciliationStatusPolicy"/> for the gross/settlement tolerance classification
/// (originally private <c>StatusFor</c>/<c>IsReconciled</c> on
/// <c>InventoryApi.Services.ReportingService</c>); <see cref="OverallStatus"/> combines the two
/// independent gross/settlement statuses into one rollup and has no daily-report equivalent, so it
/// is reconciliation-specific rather than part of the shared policy.
/// </summary>
public static class ReconciliationPeriodPolicy
{
    public static ReconciliationPeriodResult Calculate(ReconciliationPeriodInputs inputs)
    {
        var countDifference = inputs.CardTransactionCount - inputs.ReportedCount;
        var grossDifference = inputs.CardSales - inputs.ReportedGross;
        var expectedNet = inputs.ReportedGross - inputs.ProcessingFeesExGst - inputs.OtherFees - inputs.FeeGst - inputs.Adjustments;
        var settlementDifference = expectedNet - inputs.ActualNetReimbursement;
        var grossStatus = ReconciliationStatusPolicy.StatusFor(!inputs.HasImported, grossDifference, inputs.Tolerance, inputs.Warning);
        var settlementStatus = ReconciliationStatusPolicy.StatusFor(!inputs.HasImported, settlementDifference, inputs.Tolerance, inputs.Warning);
        return new ReconciliationPeriodResult(
            countDifference, grossDifference, grossStatus, expectedNet, settlementDifference, settlementStatus,
            OverallStatus(grossStatus, settlementStatus));
    }

    private static string OverallStatus(string grossStatus, string settlementStatus) =>
        grossStatus == "Mismatch" || settlementStatus == "Mismatch" ? "Mismatch" :
        grossStatus == "Pending" || settlementStatus == "Pending" ? "Pending" :
        grossStatus == "Warning" || settlementStatus == "Warning" ? "Warning" : "Reconciled";
}
