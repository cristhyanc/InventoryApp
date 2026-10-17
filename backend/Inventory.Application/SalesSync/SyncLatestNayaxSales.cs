using Inventory.Application.Nayax;
using Inventory.Application.Providers;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Providers;

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
///
/// <para><strong>A fully successful run records what it covered</strong> (issue #730, parent #329
/// decision P9), through <see cref="IProviderDataCoverageStore"/>. That record is the only thing
/// that lets a report tell "no sales" apart from "no sales data", so it is written last and only
/// once everything above succeeded: a failed, cancelled or partly read run leaves no coverage, and
/// the gap it leaves is reported as unverified rather than as a measured zero.</para>
/// </summary>
public sealed class SyncLatestNayaxSales
{
    private readonly INayaxLynxClient _nayax;
    private readonly ILatestNayaxSalesStore _store;
    private readonly NayaxSalesSyncRateLimiter _rateLimiter;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IProviderDataCoverageStore _coverage;
    private readonly IClock _clock;

    public SyncLatestNayaxSales(
        INayaxLynxClient nayax,
        ILatestNayaxSalesStore store,
        NayaxSalesSyncRateLimiter rateLimiter,
        ICurrentBusinessProvider currentBusiness,
        IProviderDataCoverageStore coverage,
        IClock clock)
    {
        _nayax = nayax;
        _store = store;
        _rateLimiter = rateLimiter;
        _currentBusiness = currentBusiness;
        _coverage = coverage;
        _clock = clock;
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

        // Taken before the first read, so the range this run may claim never reaches past an instant
        // a sale could still have been authorized at while the run was reading.
        var readStartedAtUtc = _clock.UtcNow;

        var machines = await _nayax.GetMachinesAsync(cancellationToken);

        // Every machine's latest sales are read before anything is persisted, so the batch is stored
        // in one save: a machine read that fails part-way through does not leave half a refresh
        // imported, exactly as the single save at the end of the former private method did. It also
        // means a machine that fails leaves no coverage at all (issue #730): the exception
        // propagates from here, before the coverage write below.
        var sales = new List<NayaxLastSalesReport>();
        var windowStarts = new List<DateTime>();
        var everyReadSaleHasAnInstant = true;
        foreach (var machine in machines)
        {
            var machineSales = await _nayax.GetMachineLastSalesAsync(machine.MachineID, cancellationToken);
            sales.AddRange(machineSales);

            var instants = machineSales.Select(sale => sale.AuthorizationInstantUtc).ToList();
            everyReadSaleHasAnInstant &= instants.TrueForAll(instant => instant.HasValue);

            // The earliest sale this machine's rolling window returned is the furthest back the
            // window demonstrably reaches for it. A machine that returned nothing constrains the
            // claim not at all: "the last N sales" being empty is the provider saying this machine
            // has no sales to miss.
            var earliest = instants.Where(instant => instant.HasValue).Select(instant => instant!.Value).ToList();
            if (earliest.Count > 0)
                windowStarts.Add(earliest.Min());
        }

        var persisted = await _store.PersistLatestSalesAsync(sales, cancellationToken);
        if (persisted.EarliestCompletedSaleByProductId.Count > 0)
            await _store.RebuildInventoryCostsAsync(
                persisted.EarliestCompletedSaleByProductId, cancellationToken);

        await RecordCoverageAsync(windowStarts, everyReadSaleHasAnInstant, readStartedAtUtc, cancellationToken);

        return NayaxSalesSyncOutcome.Completed;
    }

    /// <summary>
    /// Records what this run accounted for (issue #730), and only ever after every machine was read
    /// and the whole batch was persisted and replayed - a run that threw anywhere above never gets
    /// here, which is exactly the rule that a partial run records no coverage.
    ///
    /// <para>The range claimed is <c>[the latest machine window start, the instant reading began)</c>.
    /// Every machine's window has to reach back at least that far for the business-wide claim "every
    /// sale in this range is now stored" to hold, so the <em>latest</em> of the per-machine window
    /// starts is the only defensible lower bound. Successive runs overlap, because each window
    /// normally reaches back further than the previous run's end, and the availability policy merges
    /// them into one contiguous coverage.</para>
    ///
    /// <para>Two cases claim nothing rather than guessing. A payload item carrying no authoritative
    /// instant is not imported at all by the store, so a batch containing one did not account for
    /// its own range. And a run in which no machine returned a single dated sale has no evidence of
    /// how far any window reaches - it knows only that the windows were empty, not what period that
    /// covers - so it records nothing and the period stays unverified rather than falsely complete.</para>
    /// </summary>
    private async Task RecordCoverageAsync(
        List<DateTime> windowStarts,
        bool everyReadSaleHasAnInstant,
        DateTime readStartedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!everyReadSaleHasAnInstant || windowStarts.Count == 0)
            return;

        var from = windowStarts.Max();
        if (from >= readStartedAtUtc)
            return;

        await _coverage.RecordAsync(
            ExternalProvider.Nayax, ProviderDataSource.SalesSync, from, readStartedAtUtc, cancellationToken);
    }
}
