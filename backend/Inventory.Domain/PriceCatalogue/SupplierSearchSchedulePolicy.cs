using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// How often one supplier's prices are searched (issue #1108, the owner's decision of 5 November
/// 2026): every day, on chosen weekdays, or on chosen weekdays every second week.
///
/// <para>Numbered from 1, so a stored <c>0</c> is never read as the first declared frequency.</para>
/// </summary>
public enum SupplierSearchFrequency
{
    /// <summary>Every day. A daily supplier names no weekday, because every day is one.</summary>
    Daily = 1,

    /// <summary>The chosen weekdays, every week.</summary>
    Weekly = 2,

    /// <summary>The chosen weekdays, in every second week.</summary>
    Fortnightly = 3,
}

/// <summary>
/// One supplier's search schedule as the rules below read it (issue #1108).
/// </summary>
/// <param name="Frequency">How often the supplier is searched.</param>
/// <param name="DaysOfWeek">
/// The weekdays it is searched on, in <see cref="SupplierSearchSchedulePolicy.Weekdays"/>'s own
/// Monday-to-Sunday order and without repeats, and empty for <see cref="SupplierSearchFrequency.Daily"/>.
/// </param>
/// <param name="FortnightStartDate">
/// The date whose week is an <em>on</em> week, for <see cref="SupplierSearchFrequency.Fortnightly"/>
/// and for nothing else. A date only - no instant and no zone - because it names a day of the
/// schedule's own calendar rather than a moment.
/// </param>
public sealed record SupplierSearchSchedule(
    SupplierSearchFrequency Frequency,
    IReadOnlyList<DayOfWeek> DaysOfWeek,
    DateOnly? FortnightStartDate);

/// <summary>
/// When one supplier's prices are searched, and how old one of its prices may be as a consequence
/// (issue #1108, the owner's decision of 5 November 2026, parent #817).
///
/// <para><strong>The schedule lives on the supplier master record, and the freshness limit is derived
/// from it.</strong> Until this issue the two were separate stored numbers: how often a supplier was
/// searched depended only on how often somebody started a run, and
/// <c>SupplierPreset.PriceCheckFreshnessHours</c> was a hand-kept value beside it that nothing
/// stopped from disagreeing. <see cref="FreshnessHoursFor"/> is now the only way that column is
/// written, so a supplier's schedule and its freshness limit cannot say different things about the
/// same cadence.</para>
///
/// <para><strong>Weeks run Monday to Sunday, and the dates are the schedule's own calendar's.</strong>
/// Every date and weekday here is <see cref="TimeZoneId"/>'s - the platform's single collection
/// calendar, which is a different thing from a business's own reporting zone (<c>Business.TimeZoneId</c>,
/// AGENTS.md § Reporting dates): a supplier preset belongs to no business, so there is no business
/// calendar to derive its days in. This file holds no zone and resolves none: it decides dates and
/// weekdays, and <c>Inventory.Application.Time.ISupplierSearchScheduleCalendar</c> is the one place a
/// date of this calendar becomes an instant.</para>
///
/// <para><strong>A fortnight's start date only says which weeks are on.</strong> It is normalised to
/// the Monday of its own week, that week is an <em>on</em> week, and the weeks alternate on and off
/// from there in both directions - so a date before the start date follows the same alternation
/// rather than being held back. It is deliberately not a "collection starts here" marker: a schedule
/// that quietly collected nothing until a date in the future would look identical to one that was
/// working.</para>
///
/// <para><strong>Nothing here decides money.</strong> No price, GST rate or basis passes through it.
/// What it decides is when a search happens and how long one of its answers stays comparable, which
/// <see cref="SupplierPriceFreshnessPolicy"/> and <see cref="CheapestUnitOfferPolicy"/> then read.</para>
/// </summary>
public static class SupplierSearchSchedulePolicy
{
    /// <summary>
    /// The IANA zone every date and weekday of a search schedule is read in: the platform's one
    /// collection calendar.
    ///
    /// <para>It is a declared platform constant rather than a fallback for a missing value, which is
    /// the thing AGENTS.md § Reporting dates refuses: a business whose own zone is unknown fails
    /// closed and is never dated in this one, and nothing here is ever used to date a business's
    /// financial history. A supplier preset has no business and no stored zone of its own, so there
    /// is exactly one calendar a collection day can mean.</para>
    /// </summary>
    public const string TimeZoneId = "Australia/Sydney";

    /// <summary>
    /// The weekdays and their stored codes, Monday first, because the weeks of this schedule run
    /// Monday to Sunday.
    ///
    /// <para>The order is the canonical stored order as well as the declared one: one set of
    /// weekdays has exactly one stored spelling, which is what makes two schedules comparable by
    /// their columns (<see cref="StoredDaysOfWeek"/>).</para>
    /// </summary>
    public static readonly IReadOnlyList<(DayOfWeek Day, string Code)> Weekdays =
    [
        (DayOfWeek.Monday, "MON"),
        (DayOfWeek.Tuesday, "TUE"),
        (DayOfWeek.Wednesday, "WED"),
        (DayOfWeek.Thursday, "THU"),
        (DayOfWeek.Friday, "FRI"),
        (DayOfWeek.Saturday, "SAT"),
        (DayOfWeek.Sunday, "SUN"),
    ];

    /// <summary>
    /// The longest value <see cref="StoredDaysOfWeek"/> can produce - all seven codes with their
    /// separators - computed from <see cref="Weekdays"/> rather than guessed, so a column sized by it
    /// cannot become too narrow for a value this policy would write.
    /// </summary>
    public static readonly int MaxStoredDaysOfWeekLength =
        Weekdays.Sum(weekday => weekday.Code.Length) + Weekdays.Count - 1;

    /// <summary>
    /// The schedule a supplier has unless somebody records another one: every day, no weekday named,
    /// no fortnight start date. It is what a create that states no schedule stores, and what the
    /// migration's own fallback mapped a stored freshness it did not recognise to.
    /// </summary>
    public static readonly SupplierSearchSchedule Daily =
        new(SupplierSearchFrequency.Daily, [], null);

    /// <summary>What a caller whose frequency is not one of the declared ones is told.</summary>
    public static readonly string UnknownFrequencyMessage =
        "A supplier's search frequency must be one of "
            + string.Join(", ", Enum.GetNames<SupplierSearchFrequency>())
            + ".";

    /// <summary>What a caller whose weekday code is not one of the seven is told.</summary>
    public static readonly string UnknownWeekdayMessage =
        "A supplier's search days must be chosen from "
            + string.Join(", ", Weekdays.Select(weekday => weekday.Code))
            + ".";

    /// <summary>
    /// What a caller who chose a weekly or fortnightly schedule and named no weekday is told. A
    /// schedule with no day is a supplier that is never searched, which is what the <em>Search
    /// prices</em> switch is for.
    /// </summary>
    public const string WeekdayRequiredMessage =
        "A weekly or fortnightly search schedule names at least one day of the week.";

    /// <summary>
    /// What a caller who named a weekday on a daily schedule is told. "Every day" and "these days"
    /// are different schedules, and storing both would leave two answers to which days are searched.
    /// </summary>
    public const string DailyNamesNoWeekdayMessage =
        "A daily search schedule names no day of the week, because every day is one.";

    /// <summary>What a caller whose fortnightly schedule carries no start date is told.</summary>
    public const string FortnightStartDateRequiredMessage =
        "A fortnightly search schedule needs a start date, which decides which weeks are searched.";

    /// <summary>
    /// What a caller who sent a fortnight start date with a daily or weekly schedule is told. Those
    /// schedules have no off week for it to place, so a stored date would be a value nothing reads.
    /// </summary>
    public const string FortnightStartDateNotAllowedMessage =
        "Only a fortnightly search schedule carries a fortnight start date.";

    /// <summary>
    /// <paramref name="schedule"/> as it is to be stored, with its weekdays de-duplicated and in
    /// <see cref="Weekdays"/>'s own order, or the refusal for a combination this application does not
    /// store.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The frequency is not a declared one, a weekly or fortnightly schedule names no weekday, a
    /// daily one names some, a fortnightly one carries no start date, or another one carries one.
    /// Nothing is stored.
    /// </exception>
    public static SupplierSearchSchedule Require(SupplierSearchSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (!Enum.IsDefined(schedule.Frequency))
        {
            throw new DomainValidationException(UnknownFrequencyMessage);
        }

        var days = Normalise(schedule.DaysOfWeek);

        if (schedule.Frequency == SupplierSearchFrequency.Daily)
        {
            if (days.Count != 0)
            {
                throw new DomainValidationException(DailyNamesNoWeekdayMessage);
            }
        }
        else if (days.Count == 0)
        {
            throw new DomainValidationException(WeekdayRequiredMessage);
        }

        if (schedule.Frequency == SupplierSearchFrequency.Fortnightly)
        {
            if (schedule.FortnightStartDate is null)
            {
                throw new DomainValidationException(FortnightStartDateRequiredMessage);
            }
        }
        else if (schedule.FortnightStartDate is not null)
        {
            throw new DomainValidationException(FortnightStartDateNotAllowedMessage);
        }

        return new SupplierSearchSchedule(schedule.Frequency, days, schedule.FortnightStartDate);
    }

    /// <summary>
    /// The weekday a submitted code names, whatever case or padding it arrived in.
    /// </summary>
    /// <exception cref="DomainValidationException">It is not one of the seven codes.</exception>
    public static DayOfWeek RequireWeekday(string? code)
    {
        var trimmed = code?.Trim();

        foreach (var (day, declared) in Weekdays)
        {
            if (string.Equals(trimmed, declared, StringComparison.OrdinalIgnoreCase))
            {
                return day;
            }
        }

        throw new DomainValidationException(UnknownWeekdayMessage);
    }

    /// <summary>The stored code of <paramref name="day"/>.</summary>
    public static string CodeFor(DayOfWeek day)
    {
        foreach (var (declared, code) in Weekdays)
        {
            if (declared == day)
            {
                return code;
            }
        }

        throw new DomainValidationException(UnknownWeekdayMessage);
    }

    /// <summary>
    /// The weekdays as one stored value: the codes in <see cref="Weekdays"/>'s order, separated by
    /// commas, and the empty string when there are none. The canonical order is what makes one set of
    /// days have exactly one stored spelling, so two schedules can be compared by their columns.
    /// </summary>
    public static string StoredDaysOfWeek(IReadOnlyList<DayOfWeek> daysOfWeek) =>
        string.Join(',', Normalise(daysOfWeek).Select(CodeFor));

    /// <summary>
    /// The inverse of <see cref="StoredDaysOfWeek"/>: the weekdays a stored value names, in the same
    /// canonical order. A blank value names none, which is a daily schedule's whole answer.
    /// </summary>
    /// <exception cref="DomainValidationException">A stored code is not one of the seven.</exception>
    public static IReadOnlyList<DayOfWeek> ReadDaysOfWeek(string? storedDaysOfWeek) =>
        string.IsNullOrWhiteSpace(storedDaysOfWeek)
            ? []
            : Normalise([.. storedDaysOfWeek
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(RequireWeekday)]);

    /// <summary>
    /// The schedule a supplier master record's three columns hold, read with this policy's own rules
    /// rather than reassembled by whichever adapter happened to read the row.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The stored combination is not one this application writes.
    /// </exception>
    public static SupplierSearchSchedule Read(
        SupplierSearchFrequency frequency,
        string? storedDaysOfWeek,
        DateOnly? fortnightStartDate) =>
        Require(new SupplierSearchSchedule(
            frequency,
            ReadDaysOfWeek(storedDaysOfWeek),
            fortnightStartDate));

    /// <summary>
    /// Whether two schedules are the same schedule - which is what decides whether a save moved the
    /// coverage boundary (<c>SupplierPreset.ScheduleEffectiveFromUtc</c>).
    ///
    /// <para>The fortnight start date is compared as <strong>the Monday of its week</strong>, because
    /// that is all of it this policy reads: re-saving Wednesday 11 November where Monday 9 November
    /// was stored selects exactly the same weeks, so it is not a schedule change and must not make
    /// every pair due again.</para>
    /// </summary>
    public static bool AreEquivalent(SupplierSearchSchedule left, SupplierSearchSchedule right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.Frequency == right.Frequency
            && Normalise(left.DaysOfWeek).SequenceEqual(Normalise(right.DaysOfWeek))
            && FortnightAnchorOf(left) == FortnightAnchorOf(right);
    }

    /// <summary>The Monday of the week <paramref name="date"/> falls in, weeks running Monday to Sunday.</summary>
    public static DateOnly MondayOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>
    /// Whether <paramref name="date"/> is one of <paramref name="schedule"/>'s scheduled occurrences:
    /// every date for a daily schedule, each chosen weekday for a weekly one, and each chosen weekday
    /// in an <em>on</em> week for a fortnightly one.
    /// </summary>
    public static bool IsScheduledOn(SupplierSearchSchedule schedule, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return schedule.Frequency switch
        {
            SupplierSearchFrequency.Daily => true,
            SupplierSearchFrequency.Weekly => schedule.DaysOfWeek.Contains(date.DayOfWeek),
            SupplierSearchFrequency.Fortnightly =>
                schedule.DaysOfWeek.Contains(date.DayOfWeek) && IsOnWeek(schedule, date),
            _ => throw new DomainValidationException(UnknownFrequencyMessage),
        };
    }

    /// <summary>
    /// Whether <paramref name="date"/> falls in one of a fortnightly schedule's <em>on</em> weeks:
    /// the week of the start date's own Monday, and every second week from it in both directions.
    /// </summary>
    /// <exception cref="DomainValidationException">The schedule is not a fortnightly one.</exception>
    public static bool IsOnWeek(SupplierSearchSchedule schedule, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (FortnightAnchorOf(schedule) is not { } anchor)
        {
            throw new DomainValidationException(FortnightStartDateRequiredMessage);
        }

        // A whole number of weeks, because both ends are a Monday - so the alternation is exact and
        // reads the same backwards as forwards, which is what makes a date before the start date
        // follow it rather than be excluded by it.
        var weeks = (MondayOf(date).DayNumber - anchor.DayNumber) / 7;

        return Math.Abs(weeks) % 2 == 0;
    }

    /// <summary>
    /// The <strong>current occurrence</strong> for <paramref name="date"/>: the latest scheduled
    /// occurrence on or before it.
    ///
    /// <para>It is "on or before" rather than "on" because the due rule is a catch-up rule: a pair
    /// its own occurrence's run never reached is still owed that search the next day, and the next
    /// week, until something answers it. A supplier searched on Thursdays therefore has last Thursday
    /// as its current occurrence all the way to the following Wednesday.</para>
    /// </summary>
    public static DateOnly CurrentOccurrenceOn(SupplierSearchSchedule schedule, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        // A validated schedule always has one within its own cycle: daily on the day itself, weekly
        // within seven days, fortnightly within fourteen.
        var cycleDays = CycleDaysOf(Require(schedule).Frequency);

        for (var back = 0; back < cycleDays; back++)
        {
            var candidate = date.AddDays(-back);

            if (IsScheduledOn(schedule, candidate))
            {
                return candidate;
            }
        }

        // Unreachable for a validated schedule, and a refusal rather than a guess if it ever is not:
        // an invented occurrence would silently decide that every pair of this supplier is covered.
        throw new DomainValidationException(WeekdayRequiredMessage);
    }

    /// <summary>
    /// The longest gap, in days, between two consecutive occurrences over one whole cycle - seven
    /// days for a weekly schedule, fourteen for a fortnightly one, wrapping round to the next cycle's
    /// first occurrence.
    ///
    /// <para>It is the <em>longest</em> gap rather than the average or the shortest, because a
    /// freshness limit has to cover the longest wait a price can legitimately have had: Wednesday and
    /// Saturday is three days one way and four the other, and a limit built on three would mark every
    /// Wednesday morning's comparison stale.</para>
    /// </summary>
    public static int LongestGapDays(SupplierSearchSchedule schedule)
    {
        var validated = Require(schedule);

        if (validated.Frequency == SupplierSearchFrequency.Daily)
        {
            return 1;
        }

        var cycleDays = CycleDaysOf(validated.Frequency);
        var offsets = validated.DaysOfWeek
            .Select(day => ((int)day + 6) % 7)
            .Order()
            .ToList();

        var longest = 0;

        for (var index = 0; index < offsets.Count; index++)
        {
            // The last occurrence's gap wraps to the first occurrence of the next cycle, which is
            // what makes a single chosen day come out as the whole cycle (seven days, or fourteen)
            // instead of zero.
            var next = index + 1 < offsets.Count
                ? offsets[index + 1]
                : offsets[0] + cycleDays;

            longest = Math.Max(longest, next - offsets[index]);
        }

        return longest;
    }

    /// <summary>
    /// The freshness limit a supplier on <paramref name="schedule"/> carries, in hours: the longest
    /// gap between two consecutive occurrences in days, times 24, plus
    /// <see cref="SupplierPriceFreshnessPolicy.MarginHours"/>.
    ///
    /// <para>This is the one way <c>SupplierPreset.PriceCheckFreshnessHours</c> is written since issue
    /// #1108, so the stored schedule and the stored limit cannot disagree. The shipped answers are
    /// 30 hours daily, 102 twice a week, 174 once a week and 342 once a fortnight - the owner's own
    /// numbers of 27 October and 1 November 2026, now derived rather than kept by hand. The cases are
    /// stored once, in <c>docs/test-vectors/supplier-search-schedule.json</c>, which the backend tests
    /// and the Super Admin page's tests both read.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The schedule is not a storable one, or the limit it produces is beyond
    /// <see cref="SupplierPriceFreshnessPolicy.MaxLimitHours"/>.
    /// </exception>
    public static int FreshnessHoursFor(SupplierSearchSchedule schedule) =>
        SupplierPriceFreshnessPolicy.LimitForCheckInterval(LongestGapDays(schedule) * 24);

    /// <summary>
    /// The Monday that decides a fortnightly schedule's <em>on</em> weeks, or <see langword="null"/>
    /// for a schedule that has no off weeks.
    /// </summary>
    private static DateOnly? FortnightAnchorOf(SupplierSearchSchedule schedule) =>
        schedule.Frequency == SupplierSearchFrequency.Fortnightly
            && schedule.FortnightStartDate is { } start
            ? MondayOf(start)
            : null;

    /// <summary>How many days one whole cycle of <paramref name="frequency"/> spans.</summary>
    private static int CycleDaysOf(SupplierSearchFrequency frequency) =>
        frequency switch
        {
            SupplierSearchFrequency.Daily => 1,
            SupplierSearchFrequency.Weekly => 7,
            SupplierSearchFrequency.Fortnightly => 14,
            _ => throw new DomainValidationException(UnknownFrequencyMessage),
        };

    /// <summary>
    /// The weekdays de-duplicated and in <see cref="Weekdays"/>'s own order, which is the one
    /// canonical form everything here compares and stores.
    /// </summary>
    private static IReadOnlyList<DayOfWeek> Normalise(IReadOnlyList<DayOfWeek>? daysOfWeek) =>
        daysOfWeek is null
            ? []
            : [.. Weekdays
                .Where(weekday => daysOfWeek.Contains(weekday.Day))
                .Select(weekday => weekday.Day)];
}
