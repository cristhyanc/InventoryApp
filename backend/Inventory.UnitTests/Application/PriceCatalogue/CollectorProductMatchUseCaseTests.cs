using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace Inventory.UnitTests.Application.PriceCatalogue;

/// <summary>
/// The use case behind <c>POST /api/price-collector/matches</c> (issue #941): the one thing it decides
/// itself, which is that a batch larger than one request may carry is refused whole.
///
/// <para>Over a fake store, because everything else is a statement about one proposal against the
/// master catalogue, the refusals a Super Admin left and the mappings that carry a key - all of which
/// has to be read in the transaction the decision is written in, and is therefore
/// <c>PriceCollectorMatchTests</c>'. The rule itself is
/// <c>AutomaticProductMatchPolicyTests</c>'.</para>
/// </summary>
public class CollectorProductMatchUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Two hundred proposals are handed on, with the clock's instant: the use case passes the batch to
    /// the one port that decides it and adds nothing of its own.
    /// </summary>
    [Fact]
    public async Task A_batch_within_the_bound_is_handed_on_with_the_clocks_instant()
    {
        var store = new FakeStore();

        var receipt = await Handle(store, Batch(AutomaticProductMatchPolicy.MaxProposalsPerBatch));

        Assert.NotNull(receipt);
        Assert.Equal(AutomaticProductMatchPolicy.MaxProposalsPerBatch, store.Submitted!.Proposals.Count);
        Assert.Equal(Now, store.AsOfUtc);
    }

    /// <summary>
    /// One proposal past the bound refuses the whole batch, and the port is not reached at all:
    /// "this request is too big" is a statement about the request, so nothing in it is looked at and
    /// nothing is stored.
    /// </summary>
    [Fact]
    public async Task A_batch_past_the_bound_is_refused_whole_and_never_reaches_the_store()
    {
        var store = new FakeStore();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => Handle(store, Batch(AutomaticProductMatchPolicy.MaxProposalsPerBatch + 1)));

        Assert.Equal(AutomaticProductMatchPolicy.BatchTooLargeMessage, refusal.Message);
        Assert.Null(store.Submitted);
    }

    /// <summary>A run nothing issued is answered with nothing, which the endpoint reports as 404.</summary>
    [Fact]
    public async Task An_unknown_run_answers_nothing()
    {
        var store = new FakeStore { Receipt = null };

        Assert.Null(await Handle(store, Batch(1)));
    }

    private static Task<ProductMatchReceipt?> Handle(FakeStore store, ProductMatchBatch batch) =>
        new RecordCollectorProductMatches(store, new FakeClock(Now)).Handle(
            batch,
            CancellationToken.None);

    private static ProductMatchBatch Batch(int proposals) =>
        new(
            42,
            [.. Enumerable.Range(0, proposals).Select(index => new ProductMatchProposal(
                PriceCatalogueWaitingKey.For($"Product {index}", "375mL"),
                index + 1,
                SuggestedBrand: null,
                SuggestedName: null,
                SuggestedSizeValue: null,
                SuggestedSizeUnit: null,
                ProductMatchConfidence.High,
                "The brand, the variant and the size all agree."))]);

    private sealed class FakeStore : IPriceCollectorMatchStore
    {
        public ProductMatchBatch? Submitted { get; private set; }

        public DateTime? AsOfUtc { get; private set; }

        public ProductMatchReceipt? Receipt { get; init; } = new(42, [], [], []);

        public Task<ProductMatchReceipt?> SubmitAsync(
            ProductMatchBatch batch,
            DateTime asOfUtc,
            CancellationToken cancellationToken)
        {
            Submitted = batch;
            AsOfUtc = asOfUtc;

            return Task.FromResult(Receipt);
        }
    }
}
