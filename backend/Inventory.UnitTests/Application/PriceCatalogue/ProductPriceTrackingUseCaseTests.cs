using Inventory.Application.PriceCatalogue;
using Inventory.Application.Time;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Application.PriceCatalogue;

/// <summary>
/// The use cases behind the "Track supplier prices" switch (issue #938): the eligibility answer the
/// status read publishes, and the one thing the write adds to what the adapter decides - translating
/// the unique index's own refusal into the same answer the in-lock read gives.
///
/// <para>Over a fake store, because what is under test here is neither SQL nor tenancy: the relational
/// half - the stamp, the filters, the index, the in-lock read - is
/// <c>EfProductPriceTrackingStoreTests</c>, and the HTTP half is
/// <c>ProductPriceTrackingApiTests</c>.</para>
/// </summary>
public class ProductPriceTrackingUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 4, 0, 0, DateTimeKind.Utc);

    #region The status read

    /// <summary>
    /// A product that is not this business's is no answer at all, which is what the endpoint reports
    /// as <c>404</c>. The store does not distinguish it from a product that does not exist, and
    /// neither does this.
    /// </summary>
    [Fact]
    public async Task An_unknown_product_has_no_tracking_state()
    {
        var store = new FakeStore();

        Assert.Null(await new GetProductPriceTracking(store).Handle(7, CancellationToken.None));
    }

    /// <summary>
    /// An untracked product reads as a real state - off, with the eligibility answer beside it - so
    /// the switch can render itself rather than an error.
    /// </summary>
    [Fact]
    public async Task An_untracked_product_reads_as_off_and_trackable()
    {
        var store = new FakeStore
        {
            View = new ProductPriceTrackingView(7, "Coca-Cola Classic", "375mL", null, null),
        };

        var state = await new GetProductPriceTracking(store).Handle(7, CancellationToken.None);

        Assert.False(state!.Tracked);
        Assert.Null(state.Status);
        Assert.Null(state.SearchName);
        Assert.Null(state.MasterProduct);
        Assert.True(state.CanTrack);
        Assert.Null(state.TrackRefusal);
    }

    /// <summary>
    /// The criterion: the eligibility answer is the Domain rule's, so the generic unit label is
    /// reported with the rule's own sentence rather than one this layer wrote.
    /// </summary>
    [Theory]
    [InlineData("unit")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_product_with_no_real_unit_label_reads_as_not_trackable_with_the_rules_reason(
        string? unitLabel)
    {
        var store = new FakeStore
        {
            View = new ProductPriceTrackingView(7, "Coca-Cola Classic", unitLabel, null, null),
        };

        var state = await new GetProductPriceTracking(store).Handle(7, CancellationToken.None);

        Assert.False(state!.CanTrack);
        Assert.Equal(ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage, state.TrackRefusal);
    }

    /// <summary>
    /// A matched mapping reads with the master product's name and size, and with the search text the
    /// mapping holds - which is what makes "the search text was copied" readable rather than inferred.
    /// </summary>
    [Fact]
    public async Task A_matched_product_reads_with_its_status_search_text_and_master_product()
    {
        var store = new FakeStore
        {
            View = new ProductPriceTrackingView(
                7,
                "Coca-Cola Classic",
                "375mL",
                Mapping(ProductPriceTrackingStatus.AutoMatched),
                new MatchedMasterProduct("Classic", 375m, MasterProductSizeUnit.Millilitre)),
        };

        var state = await new GetProductPriceTracking(store).Handle(7, CancellationToken.None);

        Assert.True(state!.Tracked);
        Assert.Equal(ProductPriceTrackingStatus.AutoMatched, state.Status);
        Assert.Equal("Coca-Cola Classic", state.SearchName);
        Assert.Equal("375mL", state.UnitLabel);
        Assert.Equal("Classic", state.MasterProduct!.Name);
        Assert.Equal(375m, state.MasterProduct.SizeValue);
    }

    /// <summary>
    /// A product that is already tracked but whose unit label was since made generic reads as tracked
    /// <em>and</em> not trackable: the eligibility rule is asked of the product as it now stands, so a
    /// switch that is on is not hidden behind a product that could no longer be turned on.
    /// </summary>
    [Fact]
    public async Task A_tracked_product_whose_label_became_generic_reads_as_tracked_and_not_trackable()
    {
        var store = new FakeStore
        {
            View = new ProductPriceTrackingView(
                7, "Coca-Cola Classic", "unit", Mapping(ProductPriceTrackingStatus.Requested), null),
        };

        var state = await new GetProductPriceTracking(store).Handle(7, CancellationToken.None);

        Assert.True(state!.Tracked);
        Assert.False(state.CanTrack);
    }

    #endregion

    #region The write

    /// <summary>Turning the switch on is the store's one atomic operation, with the clock's instant.</summary>
    [Fact]
    public async Task Turning_tracking_on_calls_the_stores_one_write_with_the_clocks_instant()
    {
        var store = new FakeStore { Write = ProductPriceTrackingWrite.Tracked() };

        var result = await new SetProductPriceTracking(store, new FixedClock(Now))
            .Handle(7, tracked: true, CancellationToken.None);

        Assert.Equal(ProductPriceTrackingWriteOutcome.Tracked, result.Outcome);
        Assert.Equal((7L, Now), store.Tracked);
        Assert.Null(store.Untracked);
    }

    /// <summary>And turning it off is the other, which takes no instant: a removed row records none.</summary>
    [Fact]
    public async Task Turning_tracking_off_calls_the_stores_removal()
    {
        var store = new FakeStore { Write = ProductPriceTrackingWrite.Removed() };

        var result = await new SetProductPriceTracking(store, new FixedClock(Now))
            .Handle(7, tracked: false, CancellationToken.None);

        Assert.Equal(ProductPriceTrackingWriteOutcome.Removed, result.Outcome);
        Assert.Equal(7L, store.Untracked);
        Assert.Null(store.Tracked);
    }

    /// <summary>
    /// The one thing this use case decides: the unique index refusing a mapping the in-lock read could
    /// not see is answered "already tracked", the same answer that read gives - so a writer this
    /// process did not serialise with is invisible to the caller rather than a server error.
    /// </summary>
    [Fact]
    public async Task A_duplicate_mapping_the_index_refused_is_answered_as_already_tracked()
    {
        var store = new FakeStore
        {
            TrackThrows = new InvalidOperationException("UNIQUE constraint failed"),
            Duplicates = true,
        };

        var result = await new SetProductPriceTracking(store, new FixedClock(Now))
            .Handle(7, tracked: true, CancellationToken.None);

        Assert.Equal(ProductPriceTrackingWriteOutcome.AlreadyTracked, result.Outcome);
    }

    /// <summary>
    /// And any other failure is not translated: a database that is unreachable must not be reported to
    /// a caller as "already tracked", which would say the switch is on when nothing was written.
    /// </summary>
    [Fact]
    public async Task Any_other_failure_is_not_translated()
    {
        var store = new FakeStore
        {
            TrackThrows = new InvalidOperationException("something else"),
            Duplicates = false,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetProductPriceTracking(store, new FixedClock(Now))
                .Handle(7, tracked: true, CancellationToken.None));
    }

    /// <summary>
    /// A refusal is passed through with its sentence rather than turned into a different outcome: the
    /// unit-label rule is the store's own answer, decided in the same operation as the write.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_reported_with_the_rules_sentence()
    {
        var store = new FakeStore
        {
            Write = ProductPriceTrackingWrite.Refused(
                ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage),
        };

        var result = await new SetProductPriceTracking(store, new FixedClock(Now))
            .Handle(7, tracked: true, CancellationToken.None);

        Assert.Equal(ProductPriceTrackingWriteOutcome.Refused, result.Outcome);
        Assert.Equal(ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage, result.Refusal);
    }

    #endregion

    #region The tracked list

    /// <summary>
    /// The list the catalogue column is built from carries a product id and a status each, and nothing
    /// else: no search text, no waiting key and no master product, so the column cannot become a
    /// second, wider read of the shared catalogue.
    /// </summary>
    [Fact]
    public async Task The_tracked_list_carries_a_product_id_and_a_status_each()
    {
        var store = new FakeStore
        {
            Mappings =
            [
                Mapping(ProductPriceTrackingStatus.Requested, productId: 7),
                Mapping(ProductPriceTrackingStatus.Matched, productId: 9),
            ],
        };

        var listed = await new ListTrackedProducts(store).Handle(CancellationToken.None);

        Assert.Equal(
            [
                new TrackedProduct(7, ProductPriceTrackingStatus.Requested),
                new TrackedProduct(9, ProductPriceTrackingStatus.Matched),
            ],
            listed);
    }

    #endregion

    private static ProductPriceTrackingRecord Mapping(
        ProductPriceTrackingStatus status,
        long productId = 7) =>
        new(
            Id: 1,
            ProductId: productId,
            MasterProductId: ProductPriceTrackingPolicy.IsMatched(status) ? 3 : null,
            Status: status,
            MatchSource: ProductPriceTrackingPolicy.IsMatched(status)
                ? ProductPriceTrackingMatchSource.SuperAdmin
                : null,
            WaitingKey: PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"),
            SearchName: "Coca-Cola Classic",
            UnitLabel: "375mL",
            CreatedAtUtc: Now,
            UpdatedAtUtc: Now);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class FakeStore : IProductPriceTrackingStore
    {
        public ProductPriceTrackingView? View { get; init; }

        public ProductPriceTrackingWrite Write { get; init; } = ProductPriceTrackingWrite.Tracked();

        public Exception? TrackThrows { get; init; }

        public bool Duplicates { get; init; }

        public IReadOnlyList<ProductPriceTrackingRecord> Mappings { get; init; } = [];

        public (long ProductId, DateTime RequestedAtUtc)? Tracked { get; private set; }

        public long? Untracked { get; private set; }

        public Task<ProductPriceTrackingWrite> TrackAsync(
            long productId, DateTime requestedAtUtc, CancellationToken cancellationToken)
        {
            Tracked = (productId, requestedAtUtc);

            return TrackThrows is not null
                ? Task.FromException<ProductPriceTrackingWrite>(TrackThrows)
                : Task.FromResult(Write);
        }

        public Task<ProductPriceTrackingWrite> UntrackAsync(
            long productId, CancellationToken cancellationToken)
        {
            Untracked = productId;
            return Task.FromResult(Write);
        }

        public Task<ProductPriceTrackingView?> FindForProductAsync(
            long productId, CancellationToken cancellationToken) => Task.FromResult(View);

        public Task<IReadOnlyList<ProductPriceTrackingRecord>> ListAsync(
            CancellationToken cancellationToken) => Task.FromResult(Mappings);

        public Task<ProductPriceTrackingRecord?> FindAsync(
            long id, CancellationToken cancellationToken) =>
            Task.FromResult(Mappings.FirstOrDefault(mapping => mapping.Id == id));

        public bool IsDuplicateMapping(Exception exception) => Duplicates;
    }
}
