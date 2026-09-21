namespace Inventory.Domain.Reporting;

/// <summary>
/// Deterministic reconciliation-status policy for comparing a completed-sale amount against an
/// imported Nayax reimbursement within a tolerance. Originally private to the legacy daily and
/// reconciliation report methods on <c>InventoryApi.Services.ReportingService</c>; placed here,
/// alongside <see cref="ReportingCalculations"/>, so the daily slice and the still-legacy
/// reconciliation report call one authoritative implementation instead of duplicating it.
/// </summary>
public static class ReconciliationStatusPolicy
{
    public static bool IsReconciled(decimal difference, decimal tolerance) =>
        Math.Abs(difference) <= Math.Abs(tolerance);

    public static string StatusFor(bool pending, decimal difference, decimal tolerance, bool warning) =>
        pending ? "Pending" : !IsReconciled(difference, tolerance) ? "Mismatch" : warning ? "Warning" : "Reconciled";
}
