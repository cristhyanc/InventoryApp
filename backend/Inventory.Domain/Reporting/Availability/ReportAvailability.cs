namespace Inventory.Domain.Reporting.Availability;

/// <summary>
/// How well a report period's provider-sourced figures are accounted for by recorded coverage
/// (issue #730, parent #329 decision P9).
///
/// <para>The four states are distinct answers, never labels on each other, and the numbering is the
/// severity order the combination rule uses: the higher the value, the more complete the evidence.
/// Only <see cref="Complete"/> may present a zero as a real zero; the other three mean "we do not
/// know that this is all of it", which is not the same as "there is none".</para>
///
/// <para>Numbered from 0 at <see cref="Missing"/> deliberately: the least-informed answer is the one
/// an unset value lands on, so a state that was never decided can never read as complete.</para>
/// </summary>
public enum ReportAvailabilityState
{
    /// <summary>
    /// No coverage was recorded for any part of the period and the period holds no data. Nothing is
    /// known about it: the figures are not zero, they are absent.
    /// </summary>
    Missing = 0,

    /// <summary>
    /// Coverage does not account for the period's data: data predates coverage recording, or a
    /// failed or partial run left a gap that recorded coverage does not close. Figures shown are
    /// real stored data, but they are not known to be all of it.
    /// </summary>
    Unverified = 1,

    /// <summary>
    /// Coverage runs without a gap from the period start to a point before the period end - the
    /// shape a provider that stopped being read leaves, whether it was disconnected or simply never
    /// synchronized again. The figures are complete up to <see cref="ReportAvailability.AsOfUtc"/>
    /// and say nothing about the rest of the period.
    /// </summary>
    Stale = 2,

    /// <summary>
    /// Coverage spans the whole period. This is the only state in which a zero is a real zero.
    /// </summary>
    Complete = 3,
}

/// <summary>
/// One recorded range of provider data a fully successful run or committed file accounted for, as
/// the availability policy reads it: a half-open <c>[FromUtc, ToUtc)</c> interval of sale or
/// settlement instants, in the same UTC space the reports filter their rows by.
/// </summary>
public readonly record struct ReportCoverageRange(DateTime FromUtc, DateTime ToUtc)
{
    /// <summary>A range that accounts for no time at all, which no coverage decision may rest on.</summary>
    public bool IsEmpty => ToUtc <= FromUtc;
}

/// <summary>
/// The decided availability of one family of provider-sourced report figures.
/// </summary>
/// <param name="State">Which of the four distinct states applies.</param>
/// <param name="AsOfUtc">
/// For <see cref="ReportAvailabilityState.Stale"/>, the instant coverage reaches - the figures are
/// "as of" it. <see langword="null"/> in every other state, because no other state has a single
/// point up to which the period is accounted for.
/// </param>
public readonly record struct ReportAvailability(ReportAvailabilityState State, DateTime? AsOfUtc)
{
    public bool IsComplete => State == ReportAvailabilityState.Complete;
}
