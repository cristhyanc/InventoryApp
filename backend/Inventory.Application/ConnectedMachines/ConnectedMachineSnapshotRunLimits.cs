namespace Inventory.Application.ConnectedMachines;

/// <summary>
/// What bounds one run of the daily connected-machine snapshot job (issue #978).
///
/// <para>It is stated here, beside the use case the job invokes, rather than in the command that
/// schedules it: the bound is a property of the work - how many of a business's accounts this
/// application is willing to read in one pass - and a number an operator could type on a command
/// line would be a bound that is not one.</para>
/// </summary>
public static class ConnectedMachineSnapshotRunLimits
{
    /// <summary>
    /// The most businesses one run will read the fleet for.
    ///
    /// <para>One business costs exactly one whole-fleet Lynx call, so this is also the most remote
    /// calls a run makes. The cap exists because a run is unattended: a deployment that suddenly has
    /// far more counted businesses than this should make a bounded, visible pass and <strong>say so
    /// in the log</strong> rather than spend an unbounded number of calls on somebody's behalf and
    /// contend with interactive requests for the single SQLite writer (docs/architecture.md § SQLite
    /// operating assumptions). The businesses a capped run did not reach keep their last complete
    /// snapshot, which is reported as stale with its age rather than as a smaller fleet, and a
    /// further run on the same day reaches them: a business already counted today costs no call, so
    /// it does not consume the cap a second time.</para>
    /// </summary>
    public const int MaxFleetReadsPerRun = 100;
}
