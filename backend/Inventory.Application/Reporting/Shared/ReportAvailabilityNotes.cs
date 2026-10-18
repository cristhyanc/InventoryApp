using System.Globalization;
using Inventory.Domain.Reporting.Availability;

namespace Inventory.Application.Reporting.Shared;

/// <summary>
/// The one wording of each non-complete availability state, shared by every report that shows a
/// provider-sourced figure (issue #730 established it for bookkeeping; issue #731 moved it here when
/// the daily, transactions, reconciliation, product/machine profitability and dashboard reports
/// began reporting the same states).
///
/// <para>It is one helper rather than a sentence per report so a reader is told the same thing about
/// the same gap wherever they meet it, and so a wording change cannot land on five reports and miss
/// the sixth. Each report supplies only the two things that genuinely differ: which of its figures
/// the state governs, and what "nothing at all" means for that family.</para>
///
/// <para>A complete period adds nothing: an empty note list means no problem was detected, exactly
/// as issue #476 established for these reports' data-quality notes.</para>
///
/// <para>The stale note prints a UTC instant rather than a business date on purpose. It is the
/// moment coverage reaches, not a business day, and the business reporting timezone governs which
/// day a sale belongs to - not how an "as of" instant is spelled (AGENTS.md § Reporting dates and
/// filters).</para>
/// </summary>
public static class ReportAvailabilityNotes
{
    /// <summary>What "nothing at all" means for a sales-sourced figure family.</summary>
    public const string NoSalesData = "no sales data is recorded for it";

    /// <summary>What it means for a figure family fed by imported settlement data.</summary>
    public const string NoSettlementData = "no settlement data is imported for it";

    /// <summary>
    /// Adds this family's note when its state is not <see cref="ReportAvailabilityState.Complete"/>.
    /// </summary>
    /// <param name="notes">The report's data-quality note list.</param>
    /// <param name="availability">The state the server decided for this family.</param>
    /// <param name="figures">The figures it governs, named as this report shows them.</param>
    /// <param name="missingReason">
    /// Why the <see cref="ReportAvailabilityState.Missing"/> case holds nothing, normally
    /// <see cref="NoSalesData"/> or <see cref="NoSettlementData"/>.
    /// </param>
    public static void Add(
        List<string> notes, ReportAvailability availability, string figures, string missingReason)
    {
        ArgumentNullException.ThrowIfNull(notes);

        switch (availability.State)
        {
            case ReportAvailabilityState.Missing:
                notes.Add($"{figures} are unavailable for this period: {missingReason}, and no synchronization or import covered it. The figures shown are not a measured zero.");
                break;
            case ReportAvailabilityState.Stale:
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{figures} are accounted for only as of {availability.AsOfUtc:yyyy-MM-dd HH:mm} UTC; the rest of this period has not been covered by a successful synchronization or import."));
                break;
            case ReportAvailabilityState.Unverified:
                notes.Add($"{figures} are not accounted for across the whole period: the data predates coverage recording, or a failed or partial run left a gap. The amounts shown may be incomplete.");
                break;
            case ReportAvailabilityState.Complete:
            default:
                break;
        }
    }

    /// <summary>The same, from an already-reported state.</summary>
    public static void Add(
        List<string> notes, ReportAvailabilityDto availability, string figures, string missingReason) =>
        Add(notes, (availability ?? ReportAvailabilityDto.Undecided).ToDomain(), figures, missingReason);

    /// <summary>
    /// Adds both of a report's figure families' notes, in the order a reader meets the figures: the
    /// sales family first, then the family fed by imported settlement data. The combined state adds
    /// no note of its own - it is never worse than both of its inputs, so saying it a third time
    /// would repeat what the two notes above already explain.
    /// </summary>
    public static void AddBoth(
        List<string> notes, ReportProviderAvailabilityDto availability, string salesFigures, string feeFigures)
    {
        ArgumentNullException.ThrowIfNull(availability);

        Add(notes, availability.Sales, salesFigures, NoSalesData);
        Add(notes, availability.Fees, feeFigures, NoSettlementData);
    }
}
