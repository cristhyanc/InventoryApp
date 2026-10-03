using Inventory.Application.Sites;
using Inventory.Domain.Sites;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// Yielding, call-recording fake of <see cref="ISiteFactsStore"/> for the Sites use cases (issue #313).
/// Every call records when it starts and finishes, how many calls were ever in flight at once, and the
/// arguments it received, and yields before completing. The real adapter
/// (<c>InventoryApi.Adapters.Persistence.EfSiteFactsStore</c>) serves every one of these calls from a
/// single scoped <c>AppDbContext</c>, which supports only one operation at a time, so a use case that
/// starts two of them before awaiting either is visible here as an interleaved trace and a concurrency
/// count above one - a defect SQLite's synchronous async implementation hides in tests while a real
/// asynchronous provider rejects it. It follows the same recorder pattern as
/// <c>ResolveMachineProductPricingTests</c>'s call-sequence recorder.
/// </summary>
public sealed class RecordingSiteFactsStore : ISiteFactsStore
{
    private readonly IReadOnlyList<SiteProductActivityFact> _productActivity;
    private readonly IReadOnlyList<SiteCompletedSaleFact> _sales;
    private readonly IReadOnlyDictionary<long, SiteProductCostBasis> _costBasis;
    private readonly IReadOnlyDictionary<decimal, decimal> _commissionByPrice;
    private readonly decimal? _feeExGst;
    private readonly bool _commissionConfigurationUnavailable;
    private readonly Exception? _completedSalesFailure;
    private int _inFlight;

    public RecordingSiteFactsStore(
        IReadOnlyList<SiteProductActivityFact>? productActivity = null,
        IReadOnlyList<SiteCompletedSaleFact>? sales = null,
        IReadOnlyDictionary<long, SiteProductCostBasis>? costBasis = null,
        IReadOnlyDictionary<decimal, decimal>? commissionByPrice = null,
        decimal? feeExGst = null,
        bool commissionConfigurationUnavailable = false,
        Exception? completedSalesFailure = null)
    {
        _productActivity = productActivity ?? [];
        _sales = sales ?? [];
        _costBasis = costBasis ?? new Dictionary<long, SiteProductCostBasis>();
        _commissionByPrice = commissionByPrice ?? new Dictionary<decimal, decimal>();
        _feeExGst = feeExGst;
        _commissionConfigurationUnavailable = commissionConfigurationUnavailable;
        _completedSalesFailure = completedSalesFailure;
    }

    /// <summary>The ordered <c>start:</c>/<c>end:</c> markers of every port call.</summary>
    public List<string> Trace { get; } = [];

    public int MaxConcurrentCalls { get; private set; }

    /// <summary>The machine id set and lookback bound each completed-sales read was asked for.</summary>
    public List<(IReadOnlyCollection<long> MachineIds, DateTime Since)> CompletedSalesRequests { get; } = [];

    public Task<IReadOnlyList<SiteProductActivityFact>> GetProductActivityAsync(CancellationToken cancellationToken) =>
        RecordAsync("activity", _productActivity);

    public Task<IReadOnlyList<SiteCompletedSaleFact>> GetRecentCompletedSalesAsync(
        IReadOnlyCollection<long> machineIds, DateTime since, CancellationToken cancellationToken)
    {
        CompletedSalesRequests.Add((machineIds.ToList(), since));

        // Filters exactly as the EF adapter does, so a use case that forgets a site's machines sees
        // that site's sales missing rather than an unconditional fixture.
        IReadOnlyList<SiteCompletedSaleFact> sales = _sales
            .Where(sale => machineIds.Contains(sale.MachineId) && sale.MachineAuthorizationTime > since)
            .ToList();

        return _completedSalesFailure is null
            ? RecordAsync("sales", sales)
            : FailAsync<IReadOnlyList<SiteCompletedSaleFact>>("sales", _completedSalesFailure);
    }

    public Task<IReadOnlyDictionary<long, SiteProductCostBasis>> GetProductCostBasisAsync(CancellationToken cancellationToken) =>
        RecordAsync("costBasis", _costBasis);

    public Task<SiteCardCommissionResolution> ResolveCardCommissionAsync(
        long siteId, DateTime asOfDate, IReadOnlyCollection<decimal> candidateRetailPrices, CancellationToken cancellationToken) =>
        RecordAsync("commission", new SiteCardCommissionResolution(_commissionConfigurationUnavailable, _commissionByPrice));

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

    private async Task<T> FailAsync<T>(string name, Exception failure)
    {
        Trace.Add($"start:{name}");
        MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, ++_inFlight);

        await Task.Yield();

        _inFlight--;
        Trace.Add($"fail:{name}");
        throw failure;
    }
}
