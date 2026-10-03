using System.Globalization;
using Inventory.Application.Products;
using Inventory.Application.Sites;
using Inventory.Application.Time;
using Inventory.Domain.Sites;
using InventoryApi.Tests.Application.Sites;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Products;

public class ResolveMachineProductPricingTests
{
    /// <summary>
    /// The business date the commission/fee configuration is selected for. These cases are not about
    /// which date that is - <see cref="Handle_SelectsTheSydneyEffectiveConfiguration_WhenTheUtcDateDiffers"/>
    /// is - so any fixed business date serves them.
    /// </summary>
    private static readonly IBusinessCalendar Calendar = new FakeBusinessCalendar(new DateTime(2026, 3, 12));

    [Fact]
    public async Task Handle_ReturnsNoSuggestions_WhenTheMachineHasNoSite()
    {
        var useCase = new ResolveMachineProductPricing(
            new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), 0.2m), Calendar);

        var results = await useCase.Handle(
            siteId: null,
            facts: [new MachineProductPricingFact(200, 10m, 2m)],
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Null(result.SuggestedNetValue);
        Assert.Null(result.SuggestedPriceValue);
    }

    [Fact]
    public async Task Handle_ReturnsNoSuggestions_WhenTheCommissionConfigurationIsUnavailable()
    {
        var facts = new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), 0.2m, configurationUnavailable: true);
        var useCase = new ResolveMachineProductPricing(facts, Calendar);

        var results = await useCase.Handle(91, [new MachineProductPricingFact(200, 10m, 2m)], CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Null(result.SuggestedNetValue);
        Assert.Null(result.SuggestedPriceValue);
    }

    /// <summary>
    /// A vending machine can stock the same catalogue product in more than one slot at a different
    /// price. Results must stay positional (not deduplicated by product id), or one slot's suggested
    /// values would silently overwrite the other's.
    /// </summary>
    [Fact]
    public async Task Handle_ComputesEachFactIndependently_WhenTheSameProductAppearsTwiceAtDifferentPrices()
    {
        var commissionByPrice = new Dictionary<decimal, decimal> { [10m] = 1.0m, [20m] = 4.0m, [1m] = 0.1m };
        var store = new FakeSiteFactsStore(commissionByPrice, 0.2m);
        var useCase = new ResolveMachineProductPricing(store, Calendar);

        var results = await useCase.Handle(
            siteId: 91,
            facts:
            [
                new MachineProductPricingFact(200, MachinePrice: 10m, AverageUnitCost: 2m),
                new MachineProductPricingFact(200, MachinePrice: 20m, AverageUnitCost: 2m),
            ],
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(6.78m, results[0].SuggestedNetValue);
        Assert.Equal(5.55m, results[0].SuggestedPriceValue);
        Assert.Equal(13.78m, results[1].SuggestedNetValue);
        Assert.Equal(5.55m, results[1].SuggestedPriceValue);
    }

    [Fact]
    public async Task Handle_TreatsNoAgreementsAsZeroCommission_NotAsUnavailable()
    {
        var store = new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), 0.2m, configurationUnavailable: false);
        var useCase = new ResolveMachineProductPricing(store, Calendar);

        var results = await useCase.Handle(91, [new MachineProductPricingFact(200, 10m, 2m)], CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(7.78m, result.SuggestedNetValue);
    }

    /// <summary>
    /// Issue #310: the effective-dated commission and Nayax fee configuration is selected for the
    /// <c>Australia/Sydney</c> business date, not the host's UTC date. Each case is an instant where
    /// the two differ, including both daylight-saving transition days: the store answers with a
    /// different configuration per date, so a use case that asked for the UTC date would price the
    /// slot from the wrong day's commission and fee rate (4.45 net and 8.50 suggested, here) instead
    /// of failing on an argument assertion alone.
    /// </summary>
    [Theory]
    [InlineData("2026-03-11T14:30:00Z", "2026-03-12")]
    [InlineData("2026-04-04T13:30:00Z", "2026-04-05")]
    [InlineData("2026-10-03T14:00:00Z", "2026-10-04")]
    public async Task Handle_SelectsTheSydneyEffectiveConfiguration_WhenTheUtcDateDiffers(
        string nowUtc, string expectedBusinessDate)
    {
        var time = new FixedSydneyTime(DateTime.Parse(
            nowUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
        var businessDate = DateTime.Parse(expectedBusinessDate, CultureInfo.InvariantCulture);
        var store = new EffectiveDatedSiteFactsStore(new Dictionary<DateTime, SiteFinancialConfigurationOnDate>
        {
            [businessDate] = new(new Dictionary<decimal, decimal> { [10m] = 1.0m, [1m] = 0.1m }, 0.20m),
            [time.NowUtc.Date] = new(new Dictionary<decimal, decimal> { [10m] = 3.0m, [1m] = 0.2m }, 0.50m),
        });
        var useCase = new ResolveMachineProductPricing(store, time.Calendar);

        var result = Assert.Single(
            await useCase.Handle(91, [new MachineProductPricingFact(200, 10m, 2m)], CancellationToken.None));

        Assert.Equal(businessDate, time.BusinessToday);
        Assert.NotEqual(businessDate, time.NowUtc.Date);
        Assert.All(store.AsOfDates, asOfDate => Assert.Equal(businessDate, asOfDate));

        // 10.00 retail - 2.00 cost - 1.00 commission - 0.22 fee including GST.
        Assert.Equal(6.78m, result.SuggestedNetValue);
        // (2.00 cost + 0.22 fee including GST) / (0.5 - 0.10 commission per dollar).
        Assert.Equal(5.55m, result.SuggestedPriceValue);
    }

    /// <summary>
    /// Both reads go through one scoped <see cref="ISiteFactsStore"/>, whose EF adapter shares a single
    /// <c>AppDbContext</c>, and a <c>DbContext</c> permits only one operation at a time. The commission
    /// and fee calls must therefore be awaited one at a time: starting them together with
    /// <c>Task.WhenAll</c> overlaps them, which SQLite's synchronous async implementation hides in
    /// tests while a real asynchronous provider rejects it. The recorder yields inside each call, so an
    /// overlapping implementation deterministically produces an interleaved trace and a concurrency
    /// count above one.
    /// </summary>
    [Fact]
    public async Task Handle_AwaitsTheCommissionAndFeeReadsOneAtATime()
    {
        var recorder = new CallSequenceRecordingSiteFactsStore(new Dictionary<decimal, decimal> { [10m] = 1m }, 0.2m);
        var useCase = new ResolveMachineProductPricing(recorder, Calendar);

        await useCase.Handle(91, [new MachineProductPricingFact(200, 10m, 2m)], CancellationToken.None);

        Assert.Equal(
            new[] { "start:commission", "end:commission", "start:fee", "end:fee" },
            recorder.Trace);
        Assert.Equal(1, recorder.MaxConcurrentCalls);
    }

    /// <summary>
    /// Records when each port call starts and finishes, and how many were ever in flight at once. Each
    /// call yields before completing, so a caller that started both before awaiting either is visible
    /// as an interleaved trace rather than as a passing test.
    /// </summary>
    private sealed class CallSequenceRecordingSiteFactsStore : ISiteFactsStore
    {
        private readonly IReadOnlyDictionary<decimal, decimal> _commissionByPrice;
        private readonly decimal? _feeExGst;
        private int _inFlight;

        public CallSequenceRecordingSiteFactsStore(
            IReadOnlyDictionary<decimal, decimal> commissionByPrice, decimal? feeExGst)
        {
            _commissionByPrice = commissionByPrice;
            _feeExGst = feeExGst;
        }

        public List<string> Trace { get; } = [];

        public int MaxConcurrentCalls { get; private set; }

        public Task<IReadOnlyList<SiteProductActivityFact>> GetProductActivityAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SiteCompletedSaleFact>> GetRecentCompletedSalesAsync(
            IReadOnlyCollection<long> machineIds, DateTime since, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<long, SiteProductCostBasis>> GetProductCostBasisAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SiteCardCommissionResolution> ResolveCardCommissionAsync(
            long siteId, DateTime asOfDate, IReadOnlyCollection<decimal> candidateRetailPrices, CancellationToken cancellationToken) =>
            RecordAsync("commission", new SiteCardCommissionResolution(false, _commissionByPrice));

        public Task<decimal?> ResolveEffectiveFeeExGstAsync(DateTime asOfDate, CancellationToken cancellationToken) =>
            RecordAsync("fee", _feeExGst);

        private async Task<T> RecordAsync<T>(string name, T result)
        {
            Trace.Add($"start:{name}");
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, ++_inFlight);

            await Task.Yield();

            _inFlight--;
            Trace.Add($"end:{name}");
            return result;
        }
    }
}
