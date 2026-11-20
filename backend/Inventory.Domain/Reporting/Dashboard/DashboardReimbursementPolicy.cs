namespace Inventory.Domain.Reporting.Dashboard;

/// <summary>
/// Inputs required to derive the dashboard's expected-versus-actual Nayax reimbursement difference
/// and reconciliation status. All amounts are already-aggregated facts for the reporting
/// period/machine scope; this type carries no query or persistence behavior.
/// </summary>
/// <param name="CardSales">The period's completed card sales.</param>
/// <param name="FeesIncludingGst">Its Nayax processing fees including GST.</param>
/// <param name="ActualReimbursement">
/// The net total of the imported payouts whose own period lies <em>wholly inside</em> the requested
/// range, or <c>null</c> when no payout does (issue #1253). <c>null</c> is "Not available": the
/// range has nothing to compare against, which is never the same statement as a settlement of zero.
/// </param>
/// <param name="HasPartlyCoveredPayout">
/// Whether an imported payout overlaps the requested range without lying inside it - the case where
/// the range cuts a payout period. Such a payout is reported on its own and is part of no amount
/// here, so it can explain an absent actual reimbursement but can never change one.
/// </param>
public readonly record struct DashboardReimbursementInputs(
    decimal CardSales,
    decimal FeesIncludingGst,
    decimal? ActualReimbursement,
    bool HasPartlyCoveredPayout = false);

public readonly record struct DashboardReimbursementResult(
    decimal ExpectedReimbursement,
    decimal? ActualReimbursement,
    decimal? Difference,
    bool IsReconciled,
    string Status);

/// <summary>
/// Deterministic dashboard-specific reconciliation derivation. Expected reimbursement is card sales
/// less Nayax fees including GST; actual reimbursement is the imported net settlement of the payouts
/// the requested range covers in full. Reuses the shared <see cref="ReconciliationStatusPolicy"/>
/// tolerance check the daily/reconciliation slices call, but keeps its own status vocabulary: unlike
/// those reports, the dashboard summary has no separate "Warning" state, so it is not the same
/// status rule and stays local to this feature.
///
/// <para>Since issue #1253 the comparison is made only where there is something to compare. A range
/// with no wholly-inside payout has no actual reimbursement at all, so no difference is computed and
/// no <see cref="NeedsReviewStatus"/> is published - a week's payout counted as a single day's money
/// produced exactly that false alarm. When such a range only cuts a payout period, the status says
/// so (<see cref="RangeCutsPayoutPeriodStatus"/>) instead of the bare
/// <see cref="PendingStatus"/>, which still means no settlement has been imported for these dates at
/// all.</para>
/// </summary>
public static class DashboardReimbursementPolicy
{
    public const decimal Tolerance = 0.01m;

    /// <summary>No imported payout relates to the requested range at all.</summary>
    public const string PendingStatus = "Pending";

    /// <summary>The compared range and its payouts agree within <see cref="Tolerance"/>.</summary>
    public const string ReconciledStatus = "Reconciled";

    /// <summary>They were compared and disagree by more than the tolerance.</summary>
    public const string NeedsReviewStatus = "Needs Review";

    /// <summary>
    /// Nothing was compared because every imported payout for these dates settles days outside the
    /// requested range: the range cuts a payout period. Reported instead of a difference, so a
    /// reader is told why there is no actual reimbursement rather than shown a false one.
    /// </summary>
    public const string RangeCutsPayoutPeriodStatus = "Range cuts a payout period";

    public static DashboardReimbursementResult Calculate(DashboardReimbursementInputs inputs)
    {
        var expected = inputs.CardSales - inputs.FeesIncludingGst;
        var actual = inputs.ActualReimbursement;
        decimal? difference = actual.HasValue ? actual.Value - expected : null;
        var isWithinTolerance = difference.HasValue && ReconciliationStatusPolicy.IsReconciled(difference.Value, Tolerance);
        var status = Status(actual.HasValue, isWithinTolerance, inputs.HasPartlyCoveredPayout);

        return new DashboardReimbursementResult(expected, actual, difference, isWithinTolerance, status);
    }

    /// <summary>
    /// The one status rule, as the three questions it answers in order: was there an actual
    /// reimbursement to compare, did it agree, and - when there was none - did the range cut a
    /// payout period or does no payout relate to these dates at all.
    /// </summary>
    private static string Status(bool hasActual, bool isWithinTolerance, bool hasPartlyCoveredPayout)
    {
        if (hasActual)
            return isWithinTolerance ? ReconciledStatus : NeedsReviewStatus;

        return hasPartlyCoveredPayout ? RangeCutsPayoutPeriodStatus : PendingStatus;
    }
}
