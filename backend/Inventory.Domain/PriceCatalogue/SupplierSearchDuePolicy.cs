namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// Whether one <strong>product and supplier pair</strong> is owed a search (issue #1108, parent
/// #817): the rule <c>GET /api/price-collector/work?schedule=due</c> issues work by.
///
/// <para><strong>Per pair, never per supplier.</strong> The work list is bounded and rotates through
/// the catalogue (<see cref="PriceCollectorWorkListPolicy"/>), so one run can legitimately answer
/// some of a supplier's pairs and not others. A supplier-level "last checked" is exactly what this
/// issue removed: it let one partial run mark a whole supplier as done, and the products that run
/// never reached waited a full cycle for nothing. An answer about one product therefore says nothing
/// about any other product of the same supplier.</para>
///
/// <para><strong>The coverage boundary is the later of two instants</strong>
/// (<see cref="CoverageBoundary"/>): the start of the supplier's current occurrence, and the instant
/// its schedule last became effective (<c>SupplierPreset.ScheduleEffectiveFromUtc</c>). The second is
/// what stops an answer given under the old schedule from satisfying the new one - an answer from
/// Monday morning does not cover a schedule somebody changed on Monday afternoon, and turning
/// <em>Search prices</em> back on makes the pairs due again the same day.</para>
///
/// <para><strong>The three work-item states are deliberately different facts.</strong> A
/// <em>pending</em> item - issued and not yet finalised - in an unexpired open run blocks issuance,
/// whenever that run was issued, which is what stops a run still open after midnight, a delayed
/// scheduled run, or two <c>due</c> calls at once from issuing and paying for the same pair twice. An
/// item finalised with any <em>answered</em> outcome at or after the boundary covers the pair. An item
/// finalised as <see cref="PriceCollectorWorkItemOutcome.NotSearched"/> does neither: nothing looked,
/// so the pair may be retried at once even while that run is still open - and a pending item in an
/// <em>expired</em> run has already become one of those (issues #924 and #937), so expiry is what
/// makes a stranded pair due again rather than stuck.</para>
///
/// <para><strong>It decides nothing about money and reads no price.</strong> What it decides is
/// whether a search is issued; how old a price may be and still be compared is
/// <see cref="SupplierPriceFreshnessPolicy"/>'s, derived from the same schedule by
/// <see cref="SupplierSearchSchedulePolicy.FreshnessHoursFor"/>.</para>
/// </summary>
public static class SupplierSearchDuePolicy
{
    /// <summary>
    /// Whether <paramref name="outcome"/> is an <strong>answer</strong> - anything a collector or a
    /// run closure recorded except <see cref="PriceCollectorWorkItemOutcome.NotSearched"/>.
    ///
    /// <para>An empty outcome is not an answer either: that work item is pending, which is the
    /// blocking state rather than the covering one.</para>
    /// </summary>
    public static bool IsAnswered(PriceCollectorWorkItemOutcome? outcome) =>
        outcome is { } recorded
            && Enum.IsDefined(recorded)
            && recorded != PriceCollectorWorkItemOutcome.NotSearched;

    /// <summary>
    /// The instant an answer has to be at or after in order to cover a pair: the later of the current
    /// occurrence's start and the schedule's own effective-from instant.
    ///
    /// <para>Both are needed and neither alone is enough. Without the occurrence a pair answered once
    /// would never be searched again; without the effective-from instant a schedule somebody edited,
    /// or a supplier whose searching was switched back on, would be satisfied by an answer given
    /// before that decision.</para>
    /// </summary>
    public static DateTime CoverageBoundary(
        DateTime currentOccurrenceStartUtc,
        DateTime scheduleEffectiveFromUtc) =>
        currentOccurrenceStartUtc > scheduleEffectiveFromUtc
            ? currentOccurrenceStartUtc
            : scheduleEffectiveFromUtc;

    /// <summary>
    /// Whether one pair is due, given the start of its supplier's current occurrence, when that
    /// supplier's schedule became effective, when the pair was last answered, and whether it is
    /// already in flight.
    ///
    /// <para>In flight wins over everything: a pair somebody is already searching is not issued
    /// again, however stale it is. That ordering is the whole of the in-flight protection, and it is
    /// what makes two concurrent <c>due</c> calls issue one pair once rather than twice.</para>
    ///
    /// <para>An answer exactly <em>at</em> the boundary covers the pair, because the boundary is
    /// 00:00 of the occurrence's own day (or the instant the schedule was saved) and an answer at
    /// that instant is an answer under that schedule, on that day.</para>
    /// </summary>
    /// <param name="currentOccurrenceStartUtc">
    /// 00:00 of the current occurrence's date, in the schedule's own calendar, as a UTC instant.
    /// </param>
    /// <param name="scheduleEffectiveFromUtc">
    /// When the supplier's schedule last changed, or its searching was last switched on.
    /// </param>
    /// <param name="latestAnsweredAtUtc">
    /// When this pair was most recently <em>answered</em>, or <see langword="null"/> while it never
    /// has been - a newly tracked product's state, which is why it is due at once.
    /// </param>
    /// <param name="hasPendingWorkItem">
    /// Whether this pair has a work item that was issued and has not been finalised, in a run that is
    /// still open and not yet expired.
    /// </param>
    public static bool IsPairDue(
        DateTime currentOccurrenceStartUtc,
        DateTime scheduleEffectiveFromUtc,
        DateTime? latestAnsweredAtUtc,
        bool hasPendingWorkItem) =>
        IsPairDueAtBoundary(
            CoverageBoundary(currentOccurrenceStartUtc, scheduleEffectiveFromUtc),
            latestAnsweredAtUtc,
            hasPendingWorkItem);

    /// <summary>
    /// The same rule against an already-combined <paramref name="coverageBoundaryUtc"/>, for the one
    /// caller that has it: the write that re-reads each pair's latest answer and its in-flight state
    /// inside its own transaction, and is handed the boundaries the request already worked out.
    /// </summary>
    public static bool IsPairDueAtBoundary(
        DateTime coverageBoundaryUtc,
        DateTime? latestAnsweredAtUtc,
        bool hasPendingWorkItem)
    {
        if (hasPendingWorkItem)
        {
            return false;
        }

        return latestAnsweredAtUtc is not { } answered || answered < coverageBoundaryUtc;
    }
}
