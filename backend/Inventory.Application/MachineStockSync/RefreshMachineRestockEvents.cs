using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Subscriptions;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;

namespace Inventory.Application.MachineStockSync;

/// <summary>What a Dashboard restock fetch did (issue #1122).</summary>
public enum MachineRestockFetchResult
{
    /// <summary>
    /// The fetch does not apply to this caller: a support session, a plan without the sales
    /// provider, or no connection the status gate allows. No Nayax call was made.
    /// </summary>
    NotApplicable = 1,

    /// <summary>This business was already fetched (or is being fetched) inside the hour.</summary>
    RecentlyFetched = 2,

    /// <summary>Every provider-named machine was attempted.</summary>
    Fetched = 3,

    /// <summary>
    /// Nayax refused the business as a whole - not connected or rejected credentials, a plan
    /// refusal, a permission not granted, or a support session - so no further machine was called.
    /// </summary>
    Stopped = 4,
}

/// <summary>The outcome of one <see cref="RefreshMachineRestockEvents"/> run.</summary>
public sealed record MachineRestockFetchOutcome(
    MachineRestockFetchResult Result, int MachinesFetched, int MachinesFailed)
{
    public static MachineRestockFetchOutcome NotApplicable { get; } = new(MachineRestockFetchResult.NotApplicable, 0, 0);

    public static MachineRestockFetchOutcome RecentlyFetched { get; } = new(MachineRestockFetchResult.RecentlyFetched, 0, 0);
}

/// <summary>
/// The Dashboard's hourly restock-event fetch (issue #1122, by the owner's decision of 7 November
/// 2026): opening the Dashboard fetches and imports every provider-named machine's new Nayax restock
/// alerts, at most once per business per hour, so the reminder beside it reports what is really
/// waiting instead of guessing from sales (#1090).
///
/// <para><strong>It imports and applies nothing.</strong> Each machine goes through
/// <see cref="IMachineRestockEventFetch"/> - <see cref="SyncMachineStockFromNayax"/>, the import path
/// Sync Restock already uses - so an alert is deduplicated, classified and product-mapped exactly as
/// it would be from the machine's own page, and coverage keeps the #1018 rule: a machine is recorded
/// as synced only when nothing is left pending after its fetch. No inventory quantity changes until
/// an operator applies events in Sync Restock.</para>
///
/// <para><strong>The same gates as the reminder, before anything is claimed.</strong> A support
/// session, a plan without the sales provider, and a connection the status gate refuses are each
/// answered <see cref="MachineRestockFetchResult.NotApplicable"/> without a Nayax call and without
/// taking the hour.</para>
///
/// <para><strong>A refusal of the whole business stops the run; anything else skips one
/// machine.</strong> A not-connected or rejected credential (a 401 moves the connection to
/// <c>NeedsAttention</c> and is reported as <see cref="NayaxNotConnectedException"/>), a plan refusal,
/// a permission not granted and a support session would refuse every remaining machine too, so the
/// run stops there. Any other failure is that machine's alone: it is counted and the next machine is
/// fetched. A machine that could not be fetched keeps whatever the reminder says from local data,
/// which is the "Restock check due" fallback.</para>
///
/// <para><strong>A cancelled fetch gives the hour back.</strong> The visitor who started it left,
/// so the next visit fetches; see <see cref="MachineRestockFetchThrottle"/>.</para>
/// </summary>
public sealed class RefreshMachineRestockEvents
{
    private readonly IBusinessScope _businessScope;
    private readonly CurrentPlan _plan;
    private readonly INayaxConnectionStore _connections;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly MachineRestockFetchThrottle _throttle;
    private readonly IMachineRestockSyncCoverageStore _coverage;
    private readonly INayaxMachineLinkStore _machineLinks;
    private readonly IMachineRestockEventFetch _fetch;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _businessCalendar;

    public RefreshMachineRestockEvents(
        IBusinessScope businessScope,
        CurrentPlan plan,
        INayaxConnectionStore connections,
        ICurrentBusinessProvider currentBusiness,
        MachineRestockFetchThrottle throttle,
        IMachineRestockSyncCoverageStore coverage,
        INayaxMachineLinkStore machineLinks,
        IMachineRestockEventFetch fetch,
        IClock clock,
        IBusinessCalendar businessCalendar)
    {
        _businessScope = businessScope;
        _plan = plan;
        _connections = connections;
        _currentBusiness = currentBusiness;
        _throttle = throttle;
        _coverage = coverage;
        _machineLinks = machineLinks;
        _fetch = fetch;
        _clock = clock;
        _businessCalendar = businessCalendar;
    }

    public async Task<MachineRestockFetchOutcome> Handle(CancellationToken cancellationToken)
    {
        if (_businessScope.State == BusinessScopeState.SupportReadOnly)
        {
            return MachineRestockFetchOutcome.NotApplicable;
        }

        if (!await _plan
                .AllowsAsync(PlanFeature.SalesProviderIntegration, cancellationToken)
                .ConfigureAwait(false))
        {
            return MachineRestockFetchOutcome.NotApplicable;
        }

        var connection = await _connections.FindAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null || !NayaxConnectionStatusGate.AllowsOrdinaryOperations(connection.Status))
        {
            return MachineRestockFetchOutcome.NotApplicable;
        }

        var business = await _currentBusiness.RequireBusinessIdAsync(cancellationToken).ConfigureAwait(false);
        if (!_throttle.TryClaim(business, connection.CredentialRevision, out var claim))
        {
            return MachineRestockFetchOutcome.RecentlyFetched;
        }

        try
        {
            return await FetchEveryMachineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _throttle.Release(claim);
            throw;
        }
    }

    private async Task<MachineRestockFetchOutcome> FetchEveryMachineAsync(CancellationToken cancellationToken)
    {
        // The same provider-named machines the reminder lists; the pending window only bounds the
        // counts that read also returns, which this run does not use.
        var pendingFromUtc = MachineRestockSyncPendingWindow.StartUtc(_businessCalendar, _clock.UtcNow);
        var machines = await _coverage
            .GetProviderMachineCoverageAsync(pendingFromUtc, cancellationToken)
            .ConfigureAwait(false);

        var fetched = 0;
        var failed = 0;
        foreach (var machine in machines)
        {
            var nayaxMachineId = await _machineLinks
                .GetNayaxMachineIdAsync(machine.LocalMachineId, cancellationToken)
                .ConfigureAwait(false);
            if (nayaxMachineId is null)
            {
                continue;
            }

            try
            {
                await _fetch
                    .FetchAsync(new MachineIdentity(machine.LocalMachineId, nayaxMachineId), cancellationToken)
                    .ConfigureAwait(false);
                fetched++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (RefusesTheWholeBusiness(exception))
            {
                return new MachineRestockFetchOutcome(MachineRestockFetchResult.Stopped, fetched, failed + 1);
            }
            catch (Exception)
            {
                // This machine's alone; see the remarks above. The reminder keeps its local answer.
                failed++;
            }
        }

        return new MachineRestockFetchOutcome(MachineRestockFetchResult.Fetched, fetched, failed);
    }

    private static bool RefusesTheWholeBusiness(Exception exception) =>
        exception is NayaxNotConnectedException
            or NayaxPermissionNotGrantedException
            or SupportSessionNayaxUnavailableException;
}
