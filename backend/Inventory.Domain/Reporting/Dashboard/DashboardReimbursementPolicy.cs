namespace Inventory.Domain.Reporting.Dashboard;

/// <summary>
/// Inputs required to derive the dashboard's expected-versus-actual Nayax reimbursement difference
/// and reconciliation status. All amounts are already-aggregated facts for the reporting
/// period/machine scope; this type carries no query or persistence behavior.
/// </summary>
public readonly record struct DashboardReimbursementInputs(
    decimal CardSales,
    decimal FeesIncludingGst,
    decimal ActualReimbursement,
    bool ImportedContainsRows);

public readonly record struct DashboardReimbursementResult(
    decimal ExpectedReimbursement,
    decimal ActualReimbursement,
    decimal Difference,
    bool IsReconciled,
    string Status);

/// <summary>
/// Deterministic dashboard-specific reconciliation derivation. Expected reimbursement is card sales
/// less Nayax fees including GST; actual reimbursement is the imported net settlement for the
/// period. Reuses the shared <see cref="ReconciliationStatusPolicy"/> tolerance check the
/// daily/reconciliation slices call, but keeps its own status vocabulary
/// ("Pending"/"Reconciled"/"Needs Review"): unlike those reports, the dashboard summary has no
/// separate "Warning" state, so it is not the same status rule and stays local to this feature.
/// </summary>
public static class DashboardReimbursementPolicy
{
    public const decimal Tolerance = 0.01m;

    public static DashboardReimbursementResult Calculate(DashboardReimbursementInputs inputs)
    {
        var expected = inputs.CardSales - inputs.FeesIncludingGst;
        var difference = inputs.ActualReimbursement - expected;
        var isWithinTolerance = ReconciliationStatusPolicy.IsReconciled(difference, Tolerance);
        var isReconciled = inputs.ImportedContainsRows && isWithinTolerance;
        var status = !inputs.ImportedContainsRows ? "Pending" : isWithinTolerance ? "Reconciled" : "Needs Review";

        return new DashboardReimbursementResult(expected, inputs.ActualReimbursement, difference, isReconciled, status);
    }
}
