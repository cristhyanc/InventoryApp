using System.Globalization;
using Inventory.Application.ConnectedMachines;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>What one business's attempt did, as the run reports it (issue #978).</summary>
/// <param name="BusinessId">Which business. A business key is platform operational data, not a secret.</param>
/// <param name="Outcome">
/// What recording did, or <see langword="null"/> when the attempt failed before it could decide.
/// </param>
/// <param name="FleetWasRead">Whether this attempt spent a Nayax call.</param>
/// <param name="Failed">
/// Whether the attempt failed - an upstream Nayax failure, a refused credential, a transport error.
/// Nothing was stored for it, and its last complete snapshot remains its count.
/// </param>
public sealed record ConnectedMachineSnapshotJobEntry(
    int BusinessId,
    ConnectedMachineSnapshotOutcome? Outcome,
    bool FleetWasRead,
    bool Failed);

/// <summary>What one run of the job did (issue #978).</summary>
/// <param name="Refusal">
/// Why the run did nothing at all, or <see langword="null"/> when it ran. The one refusal is a schema
/// that is not at the expected shape.
/// </param>
/// <param name="Businesses">Every business the run considered, in business-id order.</param>
/// <param name="FleetReads">How many Nayax calls the run made.</param>
/// <param name="CapReached">
/// Whether the run stopped at <see cref="ConnectedMachineSnapshotRunLimits.MaxFleetReadsPerRun"/>,
/// leaving businesses it did not reach. It is logged as a warning as well as reported, because a run
/// nobody watched must not be able to hit its bound silently.
/// </param>
public sealed record ConnectedMachineSnapshotJobReport(
    string? Refusal,
    IReadOnlyList<ConnectedMachineSnapshotJobEntry> Businesses,
    int FleetReads,
    bool CapReached)
{
    public bool Succeeded => Refusal is null;

    public int Recorded =>
        Businesses.Count(entry => entry.Outcome == ConnectedMachineSnapshotOutcome.Recorded);

    public int Skipped => Businesses.Count(entry =>
        entry.Outcome is ConnectedMachineSnapshotOutcome.AlreadyRecorded
            or ConnectedMachineSnapshotOutcome.PlanDoesNotCount
            or ConnectedMachineSnapshotOutcome.ConnectionUnavailable);

    public int Failures => Businesses.Count(entry => entry.Failed);
}

/// <summary>
/// Records today's connected-machine snapshot for every business that is counted and has not been
/// counted yet today (issue #978).
///
/// <para>It is the data half of <see cref="ConnectedMachineSnapshotCommand"/>, separated from it so
/// the bounds, the skipping and the per-business isolation can be tested against a real relational
/// database without a web host or a console.</para>
///
/// <para>The four properties that make it safe to run unattended:
/// <list type="bullet">
///   <item><b>It is bounded.</b> At most
///   <see cref="ConnectedMachineSnapshotRunLimits.MaxFleetReadsPerRun"/> Nayax calls, one per
///   business, and reaching that bound is reported <em>and logged</em> rather than passed over.</item>
///   <item><b>It reads only what it must.</b> Every decision that can skip a business - its plan,
///   its connection, and whether today is already claimed for its current account series - is made
///   before the call, by the one Application use case the refresh path uses, so a business that is
///   not counted costs no call at all. There is no retry here: the Nayax client's own bounded
///   resilience is the only one in the path.</item>
///   <item><b>One business's failure is one business's failure.</b> Each attempt is caught on its
///   own, logged value-free, and followed by the next business; nothing is stored for the failed one,
///   and its last complete snapshot stays its count, reported as stale with its age.</item>
///   <item><b>It is self-healing and idempotent.</b> Running it twice on one day records nothing the
///   second time, and a missed run simply leaves yesterday's snapshot as a stale count until the next
///   one succeeds (docs/architecture.md § Azure Functions and background workloads - failure
///   recovery mode: self-healing).</item>
/// </list></para>
///
/// <para><strong>It is deliberately not an unrestricted context</strong>, exactly as
/// <see cref="BusinessPlanGrandfatherer"/> and <see cref="NayaxConnectionMigrator"/> are not
/// (AGENTS.md § Tenant ownership and data isolation). The <c>Businesses</c> table carries no tenant
/// query filter, so the enumeration reads it under a <em>denied</em> scope; every snapshot is then
/// recorded, and every credential resolved, through a context scoped to the one business it belongs
/// to, with the central query filters and <c>BusinessOwnershipEnforcer</c> fully in force. No
/// business id is ever taken from a command line, and the job resolves each business's scope and
/// credentials exactly the way a request does.</para>
///
/// <para>Only <see cref="BusinessStatus.Active"/> businesses are considered: a pending application
/// has nothing to count, and a deactivated business is not trading, so spending its Nayax credentials
/// on a count nobody will bill is not something an unattended run should do. Its existing snapshots
/// stay exactly as they are.</para>
/// </summary>
public sealed class ConnectedMachineSnapshotJob
{
    private readonly AppDbContext _tenancyDb;
    private readonly Func<int, IConnectedMachineSnapshotRecorder> _recorderFactory;
    private readonly Action<string> _log;

    /// <param name="tenancyDb">
    /// A context used only to enumerate the businesses. It needs no tenant scope and deliberately is
    /// not given an unrestricted one: <c>Businesses</c> carries no tenant query filter, so a denied
    /// context reads it while reaching no tenant-owned row at all.
    /// </param>
    /// <param name="recorderFactory">
    /// Produces the snapshot recorder for one business, built over a context scoped to it - which is
    /// how each count stays inside the central ownership boundary and authenticates with that
    /// business's own credentials.
    /// </param>
    /// <param name="log">
    /// Where the run's own operational lines go - the cap warning and each failure. It is a sink
    /// rather than an <c>ILogger</c> so this half stays testable as what it is; the command passes
    /// the host's logger.
    /// </param>
    public ConnectedMachineSnapshotJob(
        AppDbContext tenancyDb,
        Func<int, IConnectedMachineSnapshotRecorder> recorderFactory,
        Action<string> log)
    {
        _tenancyDb = tenancyDb;
        _recorderFactory = recorderFactory;
        _log = log;
    }

    public async Task<ConnectedMachineSnapshotJobReport> RunAsync(CancellationToken cancellationToken)
    {
        var pendingCount = (await _tenancyDb.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false))
            .Count();

        if (pendingCount > 0)
        {
            return new ConnectedMachineSnapshotJobReport(
                "The database has "
                    + pendingCount.ToString(CultureInfo.InvariantCulture)
                    + " pending migration(s), so the connected-machine snapshot table may not exist "
                    + "or may not be at the expected shape. Apply the schema first, verify it, then "
                    + "run this job again. Nothing was read or written.",
                Businesses: [],
                FleetReads: 0,
                CapReached: false);
        }

        var businessIds = await ActiveBusinessIdsAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<ConnectedMachineSnapshotJobEntry>(businessIds.Count);
        var fleetReads = 0;
        var capReached = false;

        foreach (var businessId in businessIds)
        {
            if (fleetReads >= ConnectedMachineSnapshotRunLimits.MaxFleetReadsPerRun)
            {
                capReached = true;
                _log(
                    "Connected-machine snapshot run stopped at its cap of "
                        + ConnectedMachineSnapshotRunLimits.MaxFleetReadsPerRun.ToString(
                            CultureInfo.InvariantCulture)
                        + " fleet reads. The businesses it did not reach keep their last complete "
                        + "snapshot, which is reported as stale with its age.");
                break;
            }

            var entry = await AttemptAsync(businessId, cancellationToken).ConfigureAwait(false);

            if (entry.FleetWasRead)
            {
                fleetReads++;
            }

            entries.Add(entry);
        }

        return new ConnectedMachineSnapshotJobReport(Refusal: null, entries, fleetReads, capReached);
    }

    /// <summary>
    /// One business's attempt. A failure is this business's failure: it is logged without any of its
    /// data, counted as a call spent, and followed by the next business.
    /// </summary>
    private async Task<ConnectedMachineSnapshotJobEntry> AttemptAsync(
        int businessId,
        CancellationToken cancellationToken)
    {
        try
        {
            var attempt = await _recorderFactory(businessId)
                .ReadFleetAndRecordAsync(cancellationToken)
                .ConfigureAwait(false);

            return new ConnectedMachineSnapshotJobEntry(
                businessId,
                attempt.Outcome,
                attempt.FleetWasRead,
                Failed: false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The business id and the failure, and nothing else: no operator id, no token, no machine
            // and no count. The next run is the retry.
            _log(
                "Connected-machine snapshot failed for business "
                    + businessId.ToString(CultureInfo.InvariantCulture)
                    + " and was abandoned; nothing was stored and its last complete snapshot remains "
                    + "its count. Reason: "
                    + exception.GetType().Name);

            return new ConnectedMachineSnapshotJobEntry(
                businessId,
                Outcome: null,
                FleetWasRead: true,
                Failed: true);
        }
    }

    /// <summary>
    /// Every active business, in business-id order so a report, a rerun and a test all see them the
    /// same way.
    /// </summary>
    private async Task<List<int>> ActiveBusinessIdsAsync(CancellationToken cancellationToken) =>
        await _tenancyDb.Businesses
            .AsNoTracking()
            .Where(business => business.Status == BusinessStatus.Active)
            .OrderBy(business => business.Id)
            .Select(business => business.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
