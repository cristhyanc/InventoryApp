namespace Inventory.Application.Reporting.Shared;

/// <summary>
/// One imported Nayax payout as a reporting period and an amount (issue #1253): the sales period
/// the settlement itself declares it covers - never its payout, bank or import date - and the net
/// total it recorded for that period.
///
/// <para>A report publishes two separate lists of these: the payouts whose period lies
/// <em>wholly inside</em> the requested range, whose totals are that range's actual reimbursement,
/// and the payouts the range only <em>partly</em> overlaps, which are reported with their own dates
/// so a reader can see what the range cut and are added to no total at all (AGENTS.md
/// § Reimbursements and reconciliation: "Do not force an overlapping or partial reimbursement into
/// a different requested reporting period. Surface pending or unmatched periods").</para>
/// </summary>
/// <param name="PeriodStart">The first date the payout settles, date-only.</param>
/// <param name="PeriodEnd">The last date it settles, date-only and inclusive.</param>
/// <param name="Total">
/// The net total the import recorded, or <c>null</c> when the import stated none - which is not a
/// settlement of zero and is never counted as one.
/// </param>
public sealed record ImportedReimbursementPeriodDto(
    DateTime PeriodStart,
    DateTime PeriodEnd,
    decimal? Total);
