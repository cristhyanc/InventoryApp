using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.Time;

/// <summary>
/// The one composition of the collection calendar and a supplier's search schedule (issue #1200): the
/// instant the <strong>next scheduled search day after a given instant</strong> ends.
///
/// <para><strong>It exists so the two surfaces that judge how old a reading is cannot disagree.</strong>
/// The cheapest-supplier comparison and the Super Admin review both ask the same question about the
/// same stored facts - which day of the collection calendar was this read on, which day is the
/// supplier searched on next, and when does that day end - and two copies of those three steps would
/// be two answers to "is this price still current" (AGENTS.md § Architecture rules). The rule that
/// then reads the answer is the Domain's.</para>
///
/// <para><strong>It decides nothing itself.</strong> Which dates are occurrences is
/// <see cref="SupplierSearchSchedulePolicy"/>'s, the allowance after that day is the Domain's, and the
/// conversion between a date of the collection calendar and a true instant - including the two days a
/// year the zone's offset changes - is <see cref="ISupplierSearchScheduleCalendar"/>'s. What this adds
/// is the order of the three, in one place.</para>
/// </summary>
public static class SupplierSearchScheduleWindow
{
    /// <summary>
    /// When the first scheduled search day after the day <paramref name="observedAtUtc"/> falls on
    /// ends: 00:00 of the date after that day, in the collection calendar, as a true instant.
    ///
    /// <para>Strictly after the observation's own day, because the search that read the price is not
    /// the search that was meant to refresh it.</para>
    /// </summary>
    /// <param name="calendar">The platform collection calendar, which owns the zone.</param>
    /// <param name="schedule">The supplier master record's own stored schedule.</param>
    /// <param name="observedAtUtc">The instant the reading was observed at.</param>
    /// <exception cref="Inventory.Domain.Exceptions.DomainValidationException">
    /// The stored schedule is not a storable one, so no occurrence can be derived from it.
    /// </exception>
    public static DateTime NextSearchDayEndUtc(
        ISupplierSearchScheduleCalendar calendar,
        SupplierSearchSchedule schedule,
        DateTime observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        var observedOn = calendar.DateOf(observedAtUtc);
        var nextSearchDay = SupplierSearchSchedulePolicy.NextOccurrenceAfter(schedule, observedOn);

        return calendar.StartOfDayUtc(nextSearchDay.AddDays(1));
    }
}
