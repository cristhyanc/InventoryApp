namespace Inventory.Application.Time;

/// <summary>
/// The platform's <strong>collection calendar</strong> (issue #1108): the current date of the zone a
/// supplier search schedule's days are read in, and the UTC instant one of those dates starts at.
///
/// <para><strong>It is not <see cref="IBusinessCalendar"/> and must never be used as one.</strong>
/// That port is the <em>current business's</em> own zone and it fails closed when a business has none,
/// because dating a business's financial history in somebody else's calendar is the mistake it exists
/// to prevent (AGENTS.md § Reporting dates). This port dates no business and no financial record: a
/// supplier preset belongs to no business, so "which day is this supplier searched on" has exactly
/// one calendar it can mean, and that calendar is the declared platform constant
/// <c>Inventory.Domain.PriceCatalogue.SupplierSearchSchedulePolicy.TimeZoneId</c>. Nothing here is a
/// fallback for a business whose zone is missing.</para>
///
/// <para><strong>It decides nothing about the schedule.</strong> Which dates are occurrences, which
/// one is current and what freshness they derive are
/// <c>SupplierSearchSchedulePolicy</c>'s; what this port adds is the one conversion between that
/// calendar's dates and true instants, which is a host-time-zone-database fact rather than a rule.
/// </para>
/// </summary>
public interface ISupplierSearchScheduleCalendar
{
    /// <summary>The current date in the collection calendar, derived from the current instant.</summary>
    DateOnly Today { get; }

    /// <summary>
    /// The date <paramref name="instantUtc"/> falls on in the collection calendar - which day of the
    /// schedule a price was observed on, or an answer was given on (issue #1200).
    ///
    /// <para>It is the same conversion <see cref="Today"/> makes of the current instant, over an
    /// instant the caller already has: a price read at 20:00 UTC on a Wednesday was read on the
    /// Thursday in Sydney, and deciding its freshness from the UTC date would move it a day.</para>
    /// </summary>
    DateOnly DateOf(DateTime instantUtc);

    /// <summary>
    /// The UTC instant of midnight at the start of <paramref name="scheduleDate"/> in the collection
    /// calendar - the start of a scheduled occurrence, and therefore one half of a pair's coverage
    /// boundary (<c>SupplierSearchDuePolicy.CoverageBoundary</c>).
    ///
    /// <para>It is a real conversion rather than a fixed offset, so the two dates a year on which the
    /// zone's offset changes produce the instants they actually start at: a schedule that lost or
    /// gained an hour on those days would decide coverage from the wrong moment.</para>
    /// </summary>
    DateTime StartOfDayUtc(DateOnly scheduleDate);
}
