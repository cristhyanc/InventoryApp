using Inventory.Application.Time;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// Closes one collector run (issue #937, parent #817).
///
/// <para><strong>Closing is what turns silence into a recorded outcome.</strong> Every search the
/// collector never answered becomes <c>NotSearched</c> - which is a different fact from "searched
/// and found nothing" and from "searched and failed", and the whole reason the work item's outcome
/// is nullable until something writes one. The run then records one count per outcome and takes the
/// status those counts imply (<c>PriceCollectorRunPolicy.StatusFor</c>), so "did the collector run
/// properly last night?" is answerable from the run row alone.</para>
///
/// <para><strong>An expiry does the same thing, and that is not a coincidence.</strong> A run left
/// open past its lifetime is closed the next time it is touched or when a later run is issued, with
/// the same effect on its unanswered searches - the difference is only the status it ends in, which
/// records that nobody reported rather than that somebody did.</para>
/// </summary>
public sealed class CompletePriceCollectorRun
{
    private readonly IPriceCollectorResultStore _results;
    private readonly IClock _clock;

    public CompletePriceCollectorRun(IPriceCollectorResultStore results, IClock clock)
    {
        _results = results;
        _clock = clock;
    }

    /// <summary>
    /// Closes the run, or answers <see langword="null"/> when no run has that id.
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The run had already finished, which includes having been expired.
    /// </exception>
    public async Task<PriceCollectorRunCompletion?> Handle(
        long runId,
        CancellationToken cancellationToken) =>
        await _results
            .CompleteRunAsync(runId, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
}
