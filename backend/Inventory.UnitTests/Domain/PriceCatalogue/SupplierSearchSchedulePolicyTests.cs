using System.Text.Json;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Domain.PriceCatalogue;

/// <summary>
/// Issue #1108's search schedule: what this application will store, which dates are occurrences, and
/// the freshness limit a schedule derives.
///
/// <para>The freshness cases are <strong>not written here</strong>. They live in
/// <c>docs/test-vectors/supplier-search-schedule.json</c>, which this suite reads and the Super Admin
/// page's own tests (#1109) read as well: a case written twice is a case the two suites can disagree
/// about, and the owner's numbers are the kind of fact that must have exactly one copy.</para>
/// </summary>
public class SupplierSearchSchedulePolicyTests
{
    #region The freshness derivation, from the shared cases

    /// <summary>
    /// Every case in the shared file, derived rather than looked up: the schedule goes in and the
    /// stated longest gap and freshness limit come out.
    /// </summary>
    [Fact]
    public void Every_shared_case_derives_the_freshness_limit_it_states()
    {
        var cases = SharedCases();

        // The file is read rather than trusted: an empty or unreadable one would make this whole
        // assertion silently vacuous, which is the one way a shared corpus can fail safely-looking.
        Assert.True(cases.Count >= 6, "The shared schedule case file should hold the stated cases.");

        foreach (var shared in cases)
        {
            var schedule = shared.Schedule();

            Assert.Equal(shared.LongestGapDays, SupplierSearchSchedulePolicy.LongestGapDays(schedule));
            Assert.Equal(
                shared.FreshnessHours,
                SupplierSearchSchedulePolicy.FreshnessHoursFor(schedule));
        }
    }

    /// <summary>
    /// The shared file's own arithmetic holds: every case's limit is its longest gap in days times 24
    /// plus the margin. Without this the file could state a pair of numbers that agreed with a wrong
    /// implementation.
    /// </summary>
    [Fact]
    public void Every_shared_case_states_the_gap_times_twenty_four_plus_the_margin()
    {
        foreach (var shared in SharedCases())
        {
            Assert.Equal(
                (shared.LongestGapDays * 24) + SupplierPriceFreshnessPolicy.MarginHours,
                shared.FreshnessHours);
        }
    }

    /// <summary>
    /// The owner's four shipped numbers, named here as well as in the file, so a case accidentally
    /// deleted from the shared corpus still fails something.
    /// </summary>
    [Fact]
    public void The_shipped_cadences_derive_the_owners_own_numbers()
    {
        Assert.Equal(30, SupplierSearchSchedulePolicy.FreshnessHoursFor(
            SupplierSearchSchedulePolicy.Daily));
        Assert.Equal(174, SupplierSearchSchedulePolicy.FreshnessHoursFor(
            Weekly(DayOfWeek.Thursday)));
        Assert.Equal(102, SupplierSearchSchedulePolicy.FreshnessHoursFor(
            Weekly(DayOfWeek.Thursday, DayOfWeek.Sunday)));
        Assert.Equal(342, SupplierSearchSchedulePolicy.FreshnessHoursFor(
            Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Thursday)));
    }

    #endregion

    #region What is storable

    [Fact]
    public void A_weekly_schedule_with_no_day_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(() =>
            SupplierSearchSchedulePolicy.Require(
                new SupplierSearchSchedule(SupplierSearchFrequency.Weekly, [], null)));

        Assert.Equal(SupplierSearchSchedulePolicy.WeekdayRequiredMessage, refusal.Message);
    }

    [Fact]
    public void A_fortnightly_schedule_with_no_day_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(() =>
            SupplierSearchSchedulePolicy.Require(new SupplierSearchSchedule(
                SupplierSearchFrequency.Fortnightly,
                [],
                new DateOnly(2026, 11, 9))));

        Assert.Equal(SupplierSearchSchedulePolicy.WeekdayRequiredMessage, refusal.Message);
    }

    [Fact]
    public void A_fortnightly_schedule_with_no_start_date_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(() =>
            SupplierSearchSchedulePolicy.Require(new SupplierSearchSchedule(
                SupplierSearchFrequency.Fortnightly,
                [DayOfWeek.Monday],
                null)));

        Assert.Equal(SupplierSearchSchedulePolicy.FortnightStartDateRequiredMessage, refusal.Message);
    }

    [Theory]
    [InlineData(SupplierSearchFrequency.Daily)]
    [InlineData(SupplierSearchFrequency.Weekly)]
    public void A_start_date_on_any_other_frequency_is_refused(SupplierSearchFrequency frequency)
    {
        IReadOnlyList<DayOfWeek> days =
            frequency == SupplierSearchFrequency.Daily ? [] : [DayOfWeek.Thursday];

        var refusal = Assert.Throws<DomainValidationException>(() =>
            SupplierSearchSchedulePolicy.Require(new SupplierSearchSchedule(
                frequency,
                days,
                new DateOnly(2026, 11, 9))));

        Assert.Equal(
            SupplierSearchSchedulePolicy.FortnightStartDateNotAllowedMessage,
            refusal.Message);
    }

    [Fact]
    public void A_daily_schedule_that_names_a_weekday_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(() =>
            SupplierSearchSchedulePolicy.Require(new SupplierSearchSchedule(
                SupplierSearchFrequency.Daily,
                [DayOfWeek.Thursday],
                null)));

        Assert.Equal(SupplierSearchSchedulePolicy.DailyNamesNoWeekdayMessage, refusal.Message);
    }

    [Fact]
    public void An_undeclared_frequency_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(() =>
            SupplierSearchSchedulePolicy.Require(new SupplierSearchSchedule(
                (SupplierSearchFrequency)99,
                [],
                null)));

        Assert.Equal(SupplierSearchSchedulePolicy.UnknownFrequencyMessage, refusal.Message);
    }

    [Theory]
    [InlineData("MONDAY")]
    [InlineData("M")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unrecognised_weekday_code_is_refused(string? code)
    {
        var refusal = Assert.Throws<DomainValidationException>(() =>
            SupplierSearchSchedulePolicy.RequireWeekday(code));

        Assert.Equal(SupplierSearchSchedulePolicy.UnknownWeekdayMessage, refusal.Message);
    }

    [Theory]
    [InlineData(" mon ", DayOfWeek.Monday)]
    [InlineData("Thu", DayOfWeek.Thursday)]
    [InlineData("SUN", DayOfWeek.Sunday)]
    public void A_weekday_code_is_read_whatever_case_or_padding_it_arrives_in(
        string code,
        DayOfWeek expected)
    {
        Assert.Equal(expected, SupplierSearchSchedulePolicy.RequireWeekday(code));
    }

    /// <summary>
    /// The stored days have one canonical spelling per set: de-duplicated, and Monday first whatever
    /// order they were submitted in. That is what lets a save decide whether the schedule changed by
    /// comparing columns.
    /// </summary>
    [Fact]
    public void The_stored_days_are_canonical_whatever_order_they_arrive_in()
    {
        Assert.Equal(
            "WED,SAT",
            SupplierSearchSchedulePolicy.StoredDaysOfWeek(
                [DayOfWeek.Saturday, DayOfWeek.Wednesday, DayOfWeek.Saturday]));

        Assert.Equal(string.Empty, SupplierSearchSchedulePolicy.StoredDaysOfWeek([]));
    }

    [Fact]
    public void The_stored_days_read_back_as_the_same_weekdays()
    {
        Assert.Equal(
            [DayOfWeek.Wednesday, DayOfWeek.Saturday],
            SupplierSearchSchedulePolicy.ReadDaysOfWeek("SAT,wed"));

        Assert.Empty(SupplierSearchSchedulePolicy.ReadDaysOfWeek(""));
        Assert.Empty(SupplierSearchSchedulePolicy.ReadDaysOfWeek(null));
    }

    #endregion

    #region Which dates are occurrences

    [Fact]
    public void A_daily_schedule_is_scheduled_on_every_date()
    {
        for (var day = 0; day < 14; day++)
        {
            var date = new DateOnly(2026, 11, 1).AddDays(day);

            Assert.True(SupplierSearchSchedulePolicy.IsScheduledOn(
                SupplierSearchSchedulePolicy.Daily,
                date));
            Assert.Equal(
                date,
                SupplierSearchSchedulePolicy.CurrentOccurrenceOn(
                    SupplierSearchSchedulePolicy.Daily,
                    date));
        }
    }

    /// <summary>
    /// A weekly supplier is searched on its day and not on the others, and its current occurrence on
    /// a day that is not one of its own is the most recent one that was - which is the catch-up rule
    /// as a date.
    /// </summary>
    [Fact]
    public void A_weekly_schedule_is_only_scheduled_on_its_own_day()
    {
        var schedule = Weekly(DayOfWeek.Thursday);

        // Thursday 5 November 2026 through Wednesday 11 November 2026.
        var thursday = new DateOnly(2026, 11, 5);

        Assert.True(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, thursday));
        Assert.Equal(thursday, SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, thursday));

        for (var day = 1; day <= 6; day++)
        {
            var other = thursday.AddDays(day);

            Assert.False(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, other));
            Assert.Equal(thursday, SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, other));
        }

        // And the day after that is the next Thursday's own occurrence.
        Assert.Equal(
            thursday.AddDays(7),
            SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, thursday.AddDays(7)));
    }

    [Fact]
    public void A_schedule_on_wednesday_and_saturday_is_scheduled_on_both_and_on_nothing_else()
    {
        var schedule = Weekly(DayOfWeek.Wednesday, DayOfWeek.Saturday);

        var wednesday = new DateOnly(2026, 11, 4);
        var saturday = new DateOnly(2026, 11, 7);

        Assert.True(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, wednesday));
        Assert.True(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, saturday));

        // Thursday and Friday fall back to Wednesday; Sunday to Tuesday fall back to Saturday.
        Assert.Equal(
            wednesday,
            SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, new DateOnly(2026, 11, 5)));
        Assert.Equal(
            wednesday,
            SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, new DateOnly(2026, 11, 6)));
        Assert.Equal(
            saturday,
            SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, new DateOnly(2026, 11, 8)));
        Assert.Equal(
            saturday,
            SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, new DateOnly(2026, 11, 10)));
    }

    /// <summary>
    /// A fortnightly schedule is on in the start date's own week and off in the next, and the
    /// alternation runs backwards from the start date exactly as it runs forwards - so a date before
    /// the start date is on or off by the same rule rather than excluded.
    /// </summary>
    [Fact]
    public void A_fortnightly_schedule_alternates_on_and_off_weeks_in_both_directions()
    {
        // Monday 9 November 2026 is the anchor week.
        var schedule = Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Thursday);

        Assert.True(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, new DateOnly(2026, 11, 12)));
        Assert.False(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, new DateOnly(2026, 11, 19)));
        Assert.True(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, new DateOnly(2026, 11, 26)));

        // Backwards: the Thursday before the anchor week is an off week, and the one before that is on.
        Assert.False(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, new DateOnly(2026, 11, 5)));
        Assert.True(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, new DateOnly(2026, 10, 29)));
    }

    /// <summary>
    /// And across a year end, where a naive week-number calculation would restart the alternation.
    /// 31 December 2026 is a Thursday; the alternation continues straight through into 2027.
    /// </summary>
    [Fact]
    public void A_fortnightly_schedule_alternates_across_a_year_end()
    {
        var schedule = Fortnightly(new DateOnly(2026, 12, 28), DayOfWeek.Thursday);

        Assert.True(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, new DateOnly(2026, 12, 31)));
        Assert.False(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, new DateOnly(2027, 1, 7)));
        Assert.True(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, new DateOnly(2027, 1, 14)));
        Assert.False(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, new DateOnly(2027, 1, 21)));
    }

    /// <summary>
    /// A mid-week start date selects the same weeks as its own Monday, which is what "normalised to
    /// the Monday of its week" means, and is why re-saving a different day of the same week is not a
    /// schedule change.
    /// </summary>
    [Fact]
    public void A_mid_week_start_date_selects_the_same_weeks_as_its_monday()
    {
        var fromMonday = Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Thursday);
        var fromFriday = Fortnightly(new DateOnly(2026, 11, 13), DayOfWeek.Thursday);
        var fromSunday = Fortnightly(new DateOnly(2026, 11, 15), DayOfWeek.Thursday);

        for (var day = 0; day < 42; day++)
        {
            var date = new DateOnly(2026, 10, 1).AddDays(day);

            Assert.Equal(
                SupplierSearchSchedulePolicy.IsScheduledOn(fromMonday, date),
                SupplierSearchSchedulePolicy.IsScheduledOn(fromFriday, date));
            Assert.Equal(
                SupplierSearchSchedulePolicy.IsScheduledOn(fromMonday, date),
                SupplierSearchSchedulePolicy.IsScheduledOn(fromSunday, date));
        }

        Assert.True(SupplierSearchSchedulePolicy.AreEquivalent(fromMonday, fromFriday));
        Assert.True(SupplierSearchSchedulePolicy.AreEquivalent(fromMonday, fromSunday));
    }

    /// <summary>
    /// The current occurrence of a fortnightly schedule in an off week is the previous on week's own
    /// day, which is how a fortnightly supplier stays covered for a fortnight rather than becoming
    /// due the moment its week ends.
    /// </summary>
    [Fact]
    public void The_current_occurrence_in_an_off_week_is_the_previous_on_weeks_day()
    {
        var schedule = Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Thursday);

        Assert.Equal(
            new DateOnly(2026, 11, 12),
            SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, new DateOnly(2026, 11, 20)));
        Assert.Equal(
            new DateOnly(2026, 11, 12),
            SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, new DateOnly(2026, 11, 25)));
        Assert.Equal(
            new DateOnly(2026, 11, 26),
            SupplierSearchSchedulePolicy.CurrentOccurrenceOn(schedule, new DateOnly(2026, 11, 26)));
    }

    /// <summary>
    /// The next occurrence of a daily supplier is always the following day, which is what makes a
    /// daily price current until 06:00 the day after tomorrow (issue #1200).
    /// </summary>
    [Fact]
    public void The_next_occurrence_of_a_daily_schedule_is_the_following_day()
    {
        for (var day = 0; day < 14; day++)
        {
            var date = new DateOnly(2026, 11, 1).AddDays(day);

            Assert.Equal(
                date.AddDays(1),
                SupplierSearchSchedulePolicy.NextOccurrenceAfter(
                    SupplierSearchSchedulePolicy.Daily,
                    date));
        }
    }

    /// <summary>
    /// The owner's own example: Coles is searched on Wednesday and Sunday, so a Wednesday price's
    /// next search is Sunday and a Sunday price's next search is the following Wednesday. Both gaps
    /// are answered from the date itself rather than from the longest of them.
    /// </summary>
    [Fact]
    public void The_next_occurrence_is_the_next_searched_day_and_never_the_days_own_run()
    {
        var schedule = Weekly(DayOfWeek.Wednesday, DayOfWeek.Sunday);

        // Wednesday 11 November 2026 and Sunday 15 November 2026.
        Assert.Equal(
            new DateOnly(2026, 11, 15),
            SupplierSearchSchedulePolicy.NextOccurrenceAfter(schedule, new DateOnly(2026, 11, 11)));
        Assert.Equal(
            new DateOnly(2026, 11, 18),
            SupplierSearchSchedulePolicy.NextOccurrenceAfter(schedule, new DateOnly(2026, 11, 15)));
    }

    /// <summary>
    /// A price read on a day the supplier is not searched on - a manual run on a Tuesday - is decided
    /// by the next scheduled day after it, which is Wednesday's run and not the following Sunday's.
    /// </summary>
    [Fact]
    public void A_date_that_is_not_an_occurrence_is_answered_by_the_next_one_after_it()
    {
        var schedule = Weekly(DayOfWeek.Wednesday, DayOfWeek.Sunday);

        Assert.Equal(
            new DateOnly(2026, 11, 11),
            SupplierSearchSchedulePolicy.NextOccurrenceAfter(schedule, new DateOnly(2026, 11, 10)));
        Assert.Equal(
            new DateOnly(2026, 11, 15),
            SupplierSearchSchedulePolicy.NextOccurrenceAfter(schedule, new DateOnly(2026, 11, 12)));
    }

    /// <summary>
    /// A supplier searched on one weekday waits the whole week, and a fortnightly one the whole
    /// fortnight - including from a date in its off week, where the next on week's day is the answer.
    /// </summary>
    [Fact]
    public void A_weekly_or_fortnightly_schedule_waits_a_whole_cycle_between_occurrences()
    {
        // Thursday 5 November 2026.
        Assert.Equal(
            new DateOnly(2026, 11, 12),
            SupplierSearchSchedulePolicy.NextOccurrenceAfter(
                Weekly(DayOfWeek.Thursday),
                new DateOnly(2026, 11, 5)));

        // BIG W every second Saturday: the on weeks start Monday 9 November 2026, so Saturday the
        // 14th's next search is Saturday the 28th.
        var fortnightly = Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Saturday);

        Assert.Equal(
            new DateOnly(2026, 11, 28),
            SupplierSearchSchedulePolicy.NextOccurrenceAfter(
                fortnightly,
                new DateOnly(2026, 11, 14)));

        // And from a date in the off week in between, the same on-week Saturday.
        Assert.Equal(
            new DateOnly(2026, 11, 28),
            SupplierSearchSchedulePolicy.NextOccurrenceAfter(
                fortnightly,
                new DateOnly(2026, 11, 18)));
    }

    /// <summary>
    /// Every date's next occurrence really is the first scheduled date after it, over a whole
    /// fortnight of both kinds of schedule: stated as the property rather than as a list, because
    /// what the freshness rule needs is that no scheduled day in between is ever skipped.
    /// </summary>
    [Fact]
    public void The_next_occurrence_skips_no_scheduled_date()
    {
        var schedules = new[]
        {
            SupplierSearchSchedulePolicy.Daily,
            Weekly(DayOfWeek.Wednesday, DayOfWeek.Sunday),
            Weekly(DayOfWeek.Thursday),
            Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Saturday),
            Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Monday, DayOfWeek.Friday),
        };

        foreach (var schedule in schedules)
        {
            for (var day = 0; day < 28; day++)
            {
                var date = new DateOnly(2026, 11, 1).AddDays(day);
                var next = SupplierSearchSchedulePolicy.NextOccurrenceAfter(schedule, date);

                Assert.True(next > date);
                Assert.True(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, next));

                for (var between = date.AddDays(1); between < next; between = between.AddDays(1))
                {
                    Assert.False(SupplierSearchSchedulePolicy.IsScheduledOn(schedule, between));
                }
            }
        }
    }

    [Fact]
    public void Monday_is_the_first_day_of_the_week()
    {
        // Sunday 8 November 2026 belongs to the week starting Monday 2 November, not to the next one.
        Assert.Equal(
            new DateOnly(2026, 11, 2),
            SupplierSearchSchedulePolicy.MondayOf(new DateOnly(2026, 11, 8)));
        Assert.Equal(
            new DateOnly(2026, 11, 9),
            SupplierSearchSchedulePolicy.MondayOf(new DateOnly(2026, 11, 9)));
    }

    #endregion

    #region Equivalence

    [Fact]
    public void Two_schedules_differing_only_in_the_order_of_their_days_are_the_same_schedule()
    {
        Assert.True(SupplierSearchSchedulePolicy.AreEquivalent(
            Weekly(DayOfWeek.Saturday, DayOfWeek.Wednesday),
            Weekly(DayOfWeek.Wednesday, DayOfWeek.Saturday)));
    }

    [Fact]
    public void A_different_frequency_a_different_day_or_a_different_week_is_a_different_schedule()
    {
        Assert.False(SupplierSearchSchedulePolicy.AreEquivalent(
            Weekly(DayOfWeek.Thursday),
            Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Thursday)));

        Assert.False(SupplierSearchSchedulePolicy.AreEquivalent(
            Weekly(DayOfWeek.Thursday),
            Weekly(DayOfWeek.Thursday, DayOfWeek.Sunday)));

        // A start date in a different week selects the opposite weeks, so it is a real change.
        Assert.False(SupplierSearchSchedulePolicy.AreEquivalent(
            Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Thursday),
            Fortnightly(new DateOnly(2026, 11, 16), DayOfWeek.Thursday)));
    }

    #endregion

    #region The stored columns

    [Fact]
    public void A_schedule_read_from_its_stored_columns_is_the_one_that_was_stored()
    {
        var schedule = Fortnightly(new DateOnly(2026, 11, 9), DayOfWeek.Monday, DayOfWeek.Friday);

        var read = SupplierSearchSchedulePolicy.Read(
            schedule.Frequency,
            SupplierSearchSchedulePolicy.StoredDaysOfWeek(schedule.DaysOfWeek),
            schedule.FortnightStartDate);

        Assert.True(SupplierSearchSchedulePolicy.AreEquivalent(schedule, read));
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], read.DaysOfWeek);
    }

    /// <summary>
    /// A stored combination this application does not write is refused rather than read as something
    /// near it: fail-closed, because a silently repaired schedule would decide when a supplier is
    /// searched.
    /// </summary>
    [Fact]
    public void A_stored_combination_this_application_does_not_write_is_refused()
    {
        Assert.Throws<DomainValidationException>(() => SupplierSearchSchedulePolicy.Read(
            SupplierSearchFrequency.Weekly,
            storedDaysOfWeek: "",
            fortnightStartDate: null));
    }

    [Fact]
    public void The_stored_days_column_is_wide_enough_for_every_day()
    {
        Assert.Equal(
            SupplierSearchSchedulePolicy.MaxStoredDaysOfWeekLength,
            SupplierSearchSchedulePolicy
                .StoredDaysOfWeek([.. SupplierSearchSchedulePolicy.Weekdays.Select(day => day.Day)])
                .Length);
    }

    #endregion

    private static SupplierSearchSchedule Weekly(params DayOfWeek[] days) =>
        SupplierSearchSchedulePolicy.Require(
            new SupplierSearchSchedule(SupplierSearchFrequency.Weekly, days, null));

    private static SupplierSearchSchedule Fortnightly(DateOnly startDate, params DayOfWeek[] days) =>
        SupplierSearchSchedulePolicy.Require(
            new SupplierSearchSchedule(SupplierSearchFrequency.Fortnightly, days, startDate));

    #region The shared case file

    /// <summary>
    /// The shared cases, read from the repository rather than embedded, which is the point of them:
    /// the frontend tests of #1109 read the same file.
    /// </summary>
    private static IReadOnlyList<SharedCase> SharedCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SharedCaseFile));

        return [.. document.RootElement
            .GetProperty("cases")
            .EnumerateArray()
            .Select(element => new SharedCase(
                element.GetProperty("name").GetString()!,
                Enum.Parse<SupplierSearchFrequency>(element.GetProperty("frequency").GetString()!),
                [.. element.GetProperty("daysOfWeek")
                    .EnumerateArray()
                    .Select(day => SupplierSearchSchedulePolicy.RequireWeekday(day.GetString()))],
                element.GetProperty("fortnightStartDate") is { ValueKind: JsonValueKind.String } date
                    ? DateOnly.Parse(date.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                    : null,
                element.GetProperty("longestGapDays").GetInt32(),
                element.GetProperty("freshnessHours").GetInt32()))];
    }

    private sealed record SharedCase(
        string Name,
        SupplierSearchFrequency Frequency,
        IReadOnlyList<DayOfWeek> DaysOfWeek,
        DateOnly? FortnightStartDate,
        int LongestGapDays,
        int FreshnessHours)
    {
        internal SupplierSearchSchedule Schedule() =>
            SupplierSearchSchedulePolicy.Require(
                new SupplierSearchSchedule(Frequency, DaysOfWeek, FortnightStartDate));
    }

    private static readonly string SharedCaseFile = FindSharedCaseFile();

    /// <summary>
    /// Walks up from the test assembly to the repository root, the way
    /// <c>TimeAcquisitionTests</c> finds the backend folder: the tests run from
    /// <c>bin/</c>, and a path relative to the working directory would depend on how they were
    /// started.
    /// </summary>
    private static string FindSharedCaseFile()
    {
        const string relative = "docs/test-vectors/supplier-search-schedule.json";

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate '{relative}'. It is the shared supplier search schedule corpus and "
                + "both this suite and the Super Admin page's tests read it.");
    }

    #endregion
}
