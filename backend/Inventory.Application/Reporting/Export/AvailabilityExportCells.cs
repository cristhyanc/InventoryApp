using System.Globalization;
using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Export;

/// <summary>
/// How a downloaded report spells a provider-sourced figure and the availability behind it
/// (issue #730, parent #329 decision P9).
///
/// <para>A CSV or XLSX file is read away from the application, often months later and often pasted
/// straight into a BAS working paper, so it is the surface where an unlabelled <c>0</c> does the most
/// damage: nothing on the page says whether that zero was measured. The rule is therefore the same
/// one the API and the Angular reports follow, written once here so the three cannot drift apart
/// (AGENTS.md § Nayax processing fees and GST, and § Reporting dates and filters: an export must
/// represent the same data-quality state as its API/UI report):</para>
/// <list type="bullet">
///   <item>Only <c>Complete</c> coverage lets a zero be printed as a zero.</item>
///   <item>In any other state an amount that would read as <c>0</c> is replaced by the state's own
///   name, so a missing or unverified figure can never be mistaken for a measured zero.</item>
///   <item>A non-zero amount in an incomplete state is real partial data and stays exactly as
///   calculated - beside the state columns that say it may not be all of it.</item>
///   <item>A figure the business records itself - delivery, packaging, operating expenses, purchase
///   input GST - never passes through here: it carries no provider state and stays visible and
///   exact (AGENTS.md § Nayax processing fees and GST).</item>
///   <item>A result with no data rows at all still reports its state, through
///   <see cref="NoRowsRow"/>: the rule covers an empty row list too, because an empty download is
///   read as "nothing sold" by exactly the same reader (AGENTS.md § Nayax processing fees and
///   GST: "an absent machine, an absent product and an empty transaction list are reported with
///   the state behind them").</item>
/// </list>
/// </summary>
internal static class AvailabilityExportCells
{
    /// <summary>
    /// A provider-sourced amount, or the availability state in place of a zero nothing measured.
    /// </summary>
    internal static string Amount(decimal? value, ReportAvailabilityDto availability)
    {
        if (availability.IsComplete)
            return value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        return value is null or 0m
            ? availability.State
            : value.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The decided state's name, which is always reported, <c>Complete</c> included.</summary>
    internal static string State(ReportAvailabilityDto availability) => availability.State;

    /// <summary>
    /// The instant a <c>Stale</c> family is accounted for up to, in UTC and never shifted into a
    /// business or host timezone: it is the moment a run reached, not a business day (AGENTS.md
    /// § Reporting dates and filters). Empty in every other state, which has no such instant.
    /// </summary>
    internal static string AsOfUtc(ReportAvailabilityDto availability) =>
        availability.AsOfUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// The one trailing metadata row a per-record report with no data rows at all carries, so the
    /// report-level availability survives an empty download instead of being lost with the rows
    /// that would have held it (issue #731; see <see cref="ReportExportTable.NoRowsLabel"/> for why
    /// this shape and not a fabricated record).
    /// </summary>
    /// <param name="header">
    /// The report's header row, whose width this row matches and whose trailing columns the
    /// availability cells line up with.
    /// </param>
    /// <param name="availabilityCells">
    /// The report's own availability cells, in the order its trailing availability columns declare -
    /// two for the single-state reports, five for machine profitability.
    /// </param>
    internal static List<string> NoRowsRow(IReadOnlyList<string> header, params string[] availabilityCells)
    {
        var blankCells = header.Count - 1 - availabilityCells.Length;
        var row = new List<string>(header.Count) { ReportExportTable.NoRowsLabel };
        for (var i = 0; i < blankCells; i++)
            row.Add(string.Empty);
        row.AddRange(availabilityCells);
        return row;
    }
}
