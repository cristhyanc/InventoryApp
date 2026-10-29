using Inventory.Application.Time;
using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// Finishes one collector search (issue #937, parent #817): refuses an answer a collector may not
/// give, and hands the rest to the one transactional port that records it and decides what the
/// search's absence proves.
///
/// <para><strong>This call is what makes a search finished, and it exists because a search can
/// arrive in pieces.</strong> One search may legitimately return more packs than a single batch
/// carries, so "the collector has sent everything about this product at this supplier" is not
/// something the backend can observe - the collector states it, with the number of distinct
/// supplier product ids it submitted, and the backend accepts the answer only while that number
/// equals what it actually received. A batch lost in transit therefore leaves the search unanswered
/// (and so <c>NotSearched</c> when the run closes) rather than silently finished, which is what
/// stops a listing being recorded as missing from a search that was never completely delivered.
/// That comparison needs the rows in the same transaction as the write, so it is the adapter's; the
/// vocabulary is the Domain's and is checked here before anything is read.</para>
/// </summary>
public sealed class AnswerPriceCollectorSearch
{
    private readonly IPriceCollectorResultStore _results;
    private readonly IClock _clock;

    public AnswerPriceCollectorSearch(IPriceCollectorResultStore results, IClock clock)
    {
        _results = results;
        _clock = clock;
    }

    /// <summary>
    /// Records one search's answer, or answers <see langword="null"/> when that run has no such
    /// work item.
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainValidationException">
    /// The outcome is not one a collector may report, the failure reason does not belong with it,
    /// or a "nothing found" answer contradicts the rows already submitted. Nothing is stored.
    /// </exception>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The run has finished or expired, the declared total does not match the rows received, or the
    /// search was already answered differently. The search stays unanswered.
    /// </exception>
    public async Task<PriceCollectorSearchAnswerReceipt?> Handle(
        PriceCollectorSearchAnswer answer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answer);

        PriceCollectorWorkItemPolicy.RequireReportableAnswer(
            answer.Outcome,
            answer.FailureReason,
            answer.SubmittedRowCount);

        return await _results
            .AnswerSearchAsync(answer, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
    }
}
