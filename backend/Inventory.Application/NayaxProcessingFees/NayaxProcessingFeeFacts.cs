using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.NayaxProcessingFees;

public sealed record NayaxProcessingFeeFacts(
    IReadOnlyList<ProcessingFeeReimbursement> Reimbursements,
    IReadOnlyList<CompletedCardTransaction> CompletedSales);

/// <summary>
/// A fee period that is bounded by <c>Australia/Sydney</c> business days rather than by calendar
/// dates: the exact UTC instants its completed sales are selected between (inclusive at both ends),
/// and the first and last business date those instants cover (issue #310).
///
/// The machine dashboard's comparison periods are of this kind. Their boundaries are Sydney midnights,
/// which fall in the middle of a UTC day, so asking for the whole UTC dates they touch would charge a
/// period for fees on sales its own revenue excludes; the business dates are what the imported fee
/// data and the effective-dated rates are keyed by.
/// </summary>
public readonly record struct NayaxProcessingFeeBusinessPeriod(
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime FirstBusinessDate,
    DateTime LastBusinessDate);

public interface INayaxProcessingFeeFactsProvider
{
    Task<NayaxProcessingFeeFacts> GetFactsAsync(
        DateTime fromDate,
        DateTime toDate,
        long? machineId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same facts for a business-day-bounded period: imported reimbursements overlapping the
    /// period's business dates, and the completed sales between its exact UTC instants rather than
    /// everything in the UTC dates those instants fall on.
    /// </summary>
    Task<NayaxProcessingFeeFacts> GetBusinessPeriodFactsAsync(
        NayaxProcessingFeeBusinessPeriod period,
        long? machineId,
        CancellationToken cancellationToken);
}
