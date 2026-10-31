using Inventory.Application.Time;
using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// Accepts one batch of collector results (issue #937, parent #817): refuses a batch larger than
/// one request may carry, and hands the rest to the one transactional port that decides and stores
/// the rows.
///
/// <para><strong>The batch bound is refused here and the rows are decided there, and the split is
/// deliberate.</strong> "This request is too big" is a statement about the request: nothing in it
/// has been looked at, nothing is stored, and the whole batch is refused with <c>400</c>. Every
/// other check is a statement about one row against one listing, which needs the run, the work
/// item, the listing and that listing's last accepted price read in the same transaction the
/// decision is written in - so it belongs where that transaction is, and it is reported per row
/// rather than failing the batch. A collector that mis-reads one of two hundred packs must not lose
/// the other hundred and ninety-nine.</para>
///
/// <para><strong>Nothing a business owns is read or written.</strong> The whole surface is the
/// shared price catalogue, under the denied <c>BusinessScope</c> a collector request carries - see
/// <see cref="IPriceCollectorResultStore"/>.</para>
/// </summary>
public sealed class RecordPriceCollectorResults
{
    private readonly IPriceCollectorResultStore _results;
    private readonly IClock _clock;

    public RecordPriceCollectorResults(IPriceCollectorResultStore results, IClock clock)
    {
        _results = results;
        _clock = clock;
    }

    /// <summary>
    /// Decides one batch, or answers <see langword="null"/> when no run has that id.
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainValidationException">
    /// The batch holds more rows than one request may carry. Nothing is stored.
    /// </exception>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The run has finished or has expired. Nothing is stored.
    /// </exception>
    public async Task<PriceCollectorResultReceipt?> Handle(
        PriceCollectorResultBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        PriceCollectorResultPolicy.RequireAcceptedBatchSize(batch.Rows.Count);

        return await _results
            .SubmitAsync(batch, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
    }
}
