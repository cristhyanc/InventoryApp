using Inventory.Application.Time;
using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// Accepts one batch of collector match proposals (issue #941, parent #817): refuses a batch larger
/// than one request may carry, and hands the rest to the one transactional port that decides and
/// stores them.
///
/// <para><strong>The batch bound is refused here and the proposals are decided there</strong>, on
/// exactly the terms <see cref="RecordPriceCollectorResults"/> states: "this request is too big" is a
/// statement about the request, so nothing in it has been looked at, nothing is stored and the whole
/// batch is refused with <c>400</c>. Every other check is a statement about one proposal against the
/// master catalogue, the refusals a Super Admin left and the mappings that carry the key - which has
/// to be read in the same transaction the decision is written in - so it belongs where that
/// transaction is and is reported per proposal. A matcher that mis-read one of two hundred products
/// must not lose the other hundred and ninety-nine.</para>
/// </summary>
public sealed class RecordCollectorProductMatches
{
    private readonly IPriceCollectorMatchStore _matches;
    private readonly IClock _clock;

    public RecordCollectorProductMatches(IPriceCollectorMatchStore matches, IClock clock)
    {
        _matches = matches;
        _clock = clock;
    }

    /// <summary>
    /// Decides one batch, or answers <see langword="null"/> when no run has that id.
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainValidationException">
    /// The batch holds more proposals than one request may carry. Nothing is stored.
    /// </exception>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The run has finished or has expired. Nothing is stored.
    /// </exception>
    public async Task<ProductMatchReceipt?> Handle(
        ProductMatchBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        AutomaticProductMatchPolicy.RequireAcceptedBatchSize(batch.Proposals.Count);

        return await _matches
            .SubmitAsync(batch, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
    }
}
