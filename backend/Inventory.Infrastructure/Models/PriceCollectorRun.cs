using Inventory.Domain.PriceCatalogue;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One run of the supplier price collector (issue #925, parent #817): when it started, when it
/// finished, how it finished and how much it accounted for.
///
/// <para><strong>A platform table, on the terms <see cref="MasterProduct"/> states.</strong> A
/// collection run reads suppliers' shelves for every business at once, so it is about no business:
/// this row carries no <c>BusinessId</c>, has no query filter and takes no ownership stamp.</para>
///
/// <para><strong>It is the provenance of every price it recorded.</strong> Each
/// <see cref="SupplierPriceObservation"/> names the run that read it, which is what makes "where did
/// this price come from, and was that read trustworthy?" answerable afterwards - and what lets a
/// failed or partial run be told apart from a supplier that genuinely publishes no price. The
/// reference restricts, so a run cannot be deleted from under the observations that cite it.</para>
///
/// <para><strong>A failed run removes nothing.</strong> Its counts record what it did and did not
/// manage; the listings it never reached keep their last accepted observation and their last
/// successful check instant, and go stale on their own freshness limits
/// (<see cref="SupplierPriceFreshnessPolicy"/>) rather than being emptied. That is the owner's
/// constraint of 27 October 2026: a listing missing from a day's results keeps its last good price.
/// </para>
///
/// <para>Issue #924 is what first writes this table: issuing the collector's work list creates one
/// run, with its own <see cref="PriceCollectorWorkItem"/> rows, and expires any run still open. The
/// collector import of #937 is what later records the counts and the finished status.</para>
/// </summary>
public class PriceCollectorRun
{
    /// <summary>
    /// The run's own key, assigned by the database. This <em>is</em> the run id every observation
    /// cites: a run is created by this application rather than reported by a remote service, so there
    /// is no external identifier to keep beside it.
    /// </summary>
    public long Id { get; set; }

    /// <summary>When the run started, from the application clock.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// When the run finished, or <see langword="null"/> while it is still going.
    /// <c>PriceCollectorRunPolicy</c> keeps the two consistent: exactly the finished statuses have an
    /// end instant, so "still running" can never read as "finished and the time was lost".
    /// </summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// When this run stops being open unless the collector reports back (issue #924):
    /// <c>PriceCollectorRunPolicy.ExpiresAt</c> of the start instant and the deployment's configured
    /// run lifetime, which defaults to six hours.
    ///
    /// <para>It is stored rather than derived from the start instant and today's setting, so a run
    /// says for itself how long it was given: editing the setting must not retroactively expire or
    /// revive work that was already issued. It is nullable only because the runs #925 shipped the
    /// schema for carry no lifetime - the migration invents none for them, exactly as every other
    /// additive migration here refuses to - and a run with no expiry is one nothing expires
    /// automatically.</para>
    /// </summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>
    /// How the run ended. <see cref="PriceCollectorRunStatus.Running"/> until it does, and then one
    /// of four distinct outcomes - a run that failed is not a run that found nothing, and an expired
    /// run is one that never reported at all (issue #924).
    /// </summary>
    public PriceCollectorRunStatus Status { get; set; } = PriceCollectorRunStatus.Running;

    /// <summary>
    /// How many supplier listings this run checked. The database refuses a negative count, as
    /// <c>PriceCollectorRunPolicy</c> does.
    /// </summary>
    public int ListingsCheckedCount { get; set; }

    /// <summary>How many observations it accepted and stored.</summary>
    public int ObservationsRecordedCount { get; set; }

    /// <summary>
    /// How many checks it could not complete. Kept beside the two counts above rather than derived
    /// from their difference: a listing that was checked and rejected and one that was never reached
    /// are different failures, and subtraction would merge them.
    /// </summary>
    public int FailureCount { get; set; }
}
