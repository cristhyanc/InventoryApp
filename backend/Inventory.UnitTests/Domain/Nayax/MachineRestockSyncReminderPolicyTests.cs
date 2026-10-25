using Inventory.Domain.Nayax;
using Xunit;

namespace Inventory.UnitTests.Domain.Nayax;

/// <summary>
/// The deterministic rules behind the Dashboard's machine restock-sync reminder (issue #891).
///
/// The boundary cases are the point of this suite: three business calendar days is a date
/// difference, not 72 elapsed hours, so a daylight-saving transition must not move it, and a
/// machine with no recorded successful sync must never read as healthy.
/// </summary>
public class MachineRestockSyncReminderPolicyTests
{
    private static readonly DateTime BusinessToday = new(2026, 10, 25);

    [Fact]
    public void Three_calendar_days_is_the_overdue_boundary()
    {
        Assert.False(MachineRestockSyncReminderPolicy.IsFetchOverdue(BusinessToday, BusinessToday));
        Assert.False(MachineRestockSyncReminderPolicy.IsFetchOverdue(BusinessToday.AddDays(-1), BusinessToday));
        Assert.False(MachineRestockSyncReminderPolicy.IsFetchOverdue(BusinessToday.AddDays(-2), BusinessToday));
        Assert.True(MachineRestockSyncReminderPolicy.IsFetchOverdue(BusinessToday.AddDays(-3), BusinessToday));
        Assert.True(MachineRestockSyncReminderPolicy.IsFetchOverdue(BusinessToday.AddDays(-4), BusinessToday));
    }

    /// <summary>
    /// A machine nothing records a successful sync for is reported, not assumed healthy. It is also
    /// not asserted never to have synced: legacy machines synced before this metadata existed.
    /// </summary>
    [Fact]
    public void No_recorded_sync_is_overdue_and_has_no_elapsed_day_count()
    {
        Assert.True(MachineRestockSyncReminderPolicy.IsFetchOverdue(null, BusinessToday));
        Assert.Null(MachineRestockSyncReminderPolicy.DaysSinceLastSuccessfulSync(null, BusinessToday));
    }

    [Fact]
    public void Elapsed_days_counts_business_calendar_dates()
    {
        Assert.Equal(0, MachineRestockSyncReminderPolicy.DaysSinceLastSuccessfulSync(BusinessToday, BusinessToday));
        Assert.Equal(
            7, MachineRestockSyncReminderPolicy.DaysSinceLastSuccessfulSync(BusinessToday.AddDays(-7), BusinessToday));
    }

    /// <summary>
    /// A clock skew or a repaired business timezone can leave a recorded sync dated after today.
    /// That is not "minus one day old": it is reported as 0 and never as overdue.
    /// </summary>
    [Fact]
    public void A_sync_dated_after_today_is_neither_overdue_nor_negative()
    {
        Assert.False(MachineRestockSyncReminderPolicy.IsFetchOverdue(BusinessToday.AddDays(1), BusinessToday));
        Assert.Equal(
            0, MachineRestockSyncReminderPolicy.DaysSinceLastSuccessfulSync(BusinessToday.AddDays(1), BusinessToday));
    }

    /// <summary>
    /// The times of day are deliberately ignored: these are business calendar dates, and a date
    /// difference cannot be changed by a daylight-saving transition that makes a day 23 or 25 hours
    /// long. A fetch at 23:30 and a today of 00:30 three dates later is overdue even though barely
    /// more than 49 hours passed.
    /// </summary>
    [Fact]
    public void The_boundary_is_a_date_difference_rather_than_elapsed_hours()
    {
        var lastSynced = new DateTime(2026, 4, 2, 23, 30, 0, DateTimeKind.Unspecified);
        var today = new DateTime(2026, 4, 5, 0, 30, 0, DateTimeKind.Unspecified);

        Assert.True(MachineRestockSyncReminderPolicy.IsFetchOverdue(lastSynced, today));
        Assert.Equal(3, MachineRestockSyncReminderPolicy.DaysSinceLastSuccessfulSync(lastSynced, today));
    }

    /// <summary>
    /// The Sync Restock dialog's From date default is three calendar days before today (issue
    /// #206, shortened by #242). The reminder states the same date rather than a second one, so
    /// "clear or move the From date back" can name it.
    /// </summary>
    [Fact]
    public void The_default_from_date_is_three_calendar_days_before_today()
    {
        Assert.Equal(
            new DateTime(2026, 10, 22), MachineRestockSyncReminderPolicy.DefaultFromBusinessDate(BusinessToday));
    }

    [Fact]
    public void Pending_work_older_than_the_default_from_date_is_flagged()
    {
        Assert.False(
            MachineRestockSyncReminderPolicy.HasPendingWorkBeforeDefaultFrom(BusinessToday.AddDays(-2), BusinessToday));
        Assert.False(
            MachineRestockSyncReminderPolicy.HasPendingWorkBeforeDefaultFrom(BusinessToday.AddDays(-3), BusinessToday));
        Assert.True(
            MachineRestockSyncReminderPolicy.HasPendingWorkBeforeDefaultFrom(BusinessToday.AddDays(-4), BusinessToday));
        Assert.False(MachineRestockSyncReminderPolicy.HasPendingWorkBeforeDefaultFrom(null, BusinessToday));
    }

    /// <summary>
    /// Either condition alone is a reminder: an overdue fetch, or pending events of any age. The
    /// pending half deliberately has no date bound - work that ages out of the three-day window
    /// must not silently clear.
    /// </summary>
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, true)]
    [InlineData(false, 1, true)]
    [InlineData(true, 4, true)]
    public void A_reminder_is_an_overdue_fetch_or_any_known_pending_event(
        bool isFetchOverdue, int knownPendingEventCount, bool expected) =>
        Assert.Equal(
            expected,
            MachineRestockSyncReminderPolicy.NeedsAttention(isFetchOverdue, knownPendingEventCount));

    /// <summary>
    /// Coverage recorded against credentials that have since been replaced says nothing about the
    /// credentials in use now, so it is read as no recorded sync at all.
    /// </summary>
    [Theory]
    [InlineData(null, 4, false)]
    [InlineData(3, 4, false)]
    [InlineData(4, 4, true)]
    [InlineData(5, 4, false)]
    public void Coverage_is_current_only_for_the_revision_in_use(
        int? recordedRevision, int currentRevision, bool expected) =>
        Assert.Equal(
            expected,
            MachineRestockSyncReminderPolicy.IsCoverageCurrent(recordedRevision, currentRevision));
}
