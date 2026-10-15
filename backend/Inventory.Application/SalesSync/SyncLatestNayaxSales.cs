using Inventory.Application.Nayax;
using Inventory.Application.Tenancy;

namespace Inventory.Application.SalesSync;

/// <summary>
/// The outcome of asking for a latest-sales synchronization (issue #681): it ran, or this business
/// has run too many too recently.
///
/// The rate limit is reported as a result rather than thrown as an exception, exactly as
/// <c>NayaxConnectionTestOutcome</c> reports the connection test's: it is an expected answer the HTTP
/// boundary turns into a specific status and <c>Retry-After</c> header, not a failure any other
/// caller would want to catch.
/// </summary>
public sealed record NayaxSalesSyncOutcome
{
    private NayaxSalesSyncOutcome(TimeSpan? retryAfter)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// How long until this business may synchronize again, or <see langword="null"/> when the
    /// synchronization ran.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    // Cast because a record's own copy constructor makes a bare `new(null)` ambiguous.
    public static NayaxSalesSyncOutcome Completed { get; } = new((TimeSpan?)null);

    public static NayaxSalesSyncOutcome RateLimited(TimeSpan retryAfter) => new(retryAfter);
}

/// <summary>
/// The explicit latest-Nayax-sales synchronization use case (issue #187), extracted from the former
/// hidden <c>MachineService.SaveMachinesLastSalesAsync</c> side effect. It discovers the machines
/// through the <see cref="INayaxLynxClient"/> port, reads each machine's last sales through that same
/// port, and hands the whole batch to <see cref="ILatestNayaxSalesStore"/> to deduplicate, match,
/// classify, cost and persist, rebuilding inventory costs only for the products a completed sale
/// actually affected.
///
/// The home dashboard invokes it once before loading Sites and Machines, so both sections calculate
/// from the same freshness boundary; it never calculates machine or site figures itself.
///
/// <para><strong>It is rate-limited per business</strong> (issue #681). One run is one Nayax call
/// per machine plus one, and it writes - so it is both the most expensive call in the application
/// and the one a browser repeats most readily. <see cref="NayaxSalesSyncRateLimiter"/> holds the
/// rule; the allowance is claimed before the first Nayax call, so a refused run leaves the upstream
/// and the database untouched. The business is resolved from the authenticated actor's membership,
/// never from the request, because the allowance belongs to the business whose Nayax account and
/// rows the run would spend.</para>
/// </summary>
public sealed class SyncLatestNayaxSales
{
    private readonly INayaxLynxClient _nayax;
    private readonly ILatestNayaxSalesStore _store;
    private readonly NayaxSalesSyncRateLimiter _rateLimiter;
    private readonly ICurrentBusinessProvider _currentBusiness;

    public SyncLatestNayaxSales(
        INayaxLynxClient nayax,
        ILatestNayaxSalesStore store,
        NayaxSalesSyncRateLimiter rateLimiter,
        ICurrentBusinessProvider currentBusiness)
    {
        _nayax = nayax;
        _store = store;
        _rateLimiter = rateLimiter;
        _currentBusiness = currentBusiness;
    }

    public async Task<NayaxSalesSyncOutcome> Handle(CancellationToken cancellationToken = default)
    {
        // Resolved before anything else, and never from the request: the allowance is the business's.
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken);

        if (!_rateLimiter.TryClaim(businessId, out var retryAfter))
        {
            // Refused before the first Nayax call, so a refused request reads nothing from the
            // upstream and writes nothing here.
            return NayaxSalesSyncOutcome.RateLimited(retryAfter);
        }

        var machines = await _nayax.GetMachinesAsync(cancellationToken);

        // Every machine's latest sales are read before anything is persisted, so the batch is stored
        // in one save: a machine read that fails part-way through does not leave half a refresh
        // imported, exactly as the single save at the end of the former private method did.
        var sales = new List<NayaxLastSalesReport>();
        foreach (var machine in machines)
            sales.AddRange(await _nayax.GetMachineLastSalesAsync(machine.MachineID, cancellationToken));

        var persisted = await _store.PersistLatestSalesAsync(sales, cancellationToken);
        if (persisted.EarliestCompletedSaleByProductId.Count == 0)
            return NayaxSalesSyncOutcome.Completed;

        await _store.RebuildInventoryCostsAsync(
            persisted.EarliestCompletedSaleByProductId, cancellationToken);

        return NayaxSalesSyncOutcome.Completed;
    }
}
