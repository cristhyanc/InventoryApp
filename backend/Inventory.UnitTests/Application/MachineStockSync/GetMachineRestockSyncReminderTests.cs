using Inventory.Application.MachineStockSync;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// The Dashboard's machine restock-sync reminder use case (issue #891).
///
/// <para>The decisions under test are the ones that make the banner honest: a machine nobody can
/// show a successful fetch for is reported rather than assumed healthy, pending work never ages
/// out, coverage recorded under replaced credentials is not freshness, and the whole reminder is
/// withheld - with its reason - for a business it cannot apply to. None of it makes a Nayax
/// request, which the fakes here would have no way to serve anyway.</para>
/// </summary>
public class GetMachineRestockSyncReminderTests
{
    private const long AvenueHotelId = 11;
    private const long StationCafeId = 12;
    private const int BusinessKey = 1;
    private const int CurrentRevision = 4;

    /// <summary>Midnight UTC on 25 October 2026, which the business calendars below date from.</summary>
    private static readonly DateTime NowUtc = new(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_support_session_is_shown_no_reminder_and_nothing_is_read()
    {
        var connections = new FakeNayaxConnectionStore("op", "token", NayaxConnectionStatus.Ready);
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(Overdue(AvenueHotelId, "Avenue Hotel"));

        var result = await UseCase(connections, coverage, SupportSession()).Handle(CancellationToken.None);

        var hidden = Assert.IsType<HiddenMachineRestockSyncReminderDto>(result);
        Assert.False(hidden.Applicable);
        Assert.Equal(MachineRestockSyncReminderHiddenReasons.SupportView, hidden.Reason);

        // Not merely hidden: a support view never even asks, which is what keeps the reminder off
        // the one request shape that resolves no Nayax credentials at all.
        Assert.Equal(0, coverage.Reads);
        Assert.Equal(0, connections.ConnectionReads);
    }

    [Fact]
    public async Task A_business_with_no_stored_connection_is_shown_no_reminder()
    {
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(Overdue(AvenueHotelId, "Avenue Hotel"));

        var result = await UseCase(new FakeNayaxConnectionStore(), coverage).Handle(CancellationToken.None);

        Assert.Equal(
            MachineRestockSyncReminderHiddenReasons.NayaxNotConnected,
            Assert.IsType<HiddenMachineRestockSyncReminderDto>(result).Reason);
        Assert.Equal(0, coverage.Reads);
    }

    /// <summary>
    /// The one place "usable" is decided is <see cref="NayaxConnectionStatusGate"/>, so a connection
    /// it refuses hides the reminder and the two it allows do not.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured, false)]
    [InlineData(NayaxConnectionStatus.NeedsAttention, false)]
    [InlineData(NayaxConnectionStatus.Ready, true)]
    [InlineData(NayaxConnectionStatus.PendingPermissions, true)]
    public async Task The_status_gate_decides_whether_the_reminder_applies(
        NayaxConnectionStatus status, bool expectedApplicable)
    {
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(Overdue(AvenueHotelId, "Avenue Hotel"));

        var result = await UseCase(
                new FakeNayaxConnectionStore("op", "token", status, CurrentRevision), coverage)
            .Handle(CancellationToken.None);

        Assert.Equal(expectedApplicable, result.Applicable);
    }

    [Fact]
    public async Task A_business_whose_provider_names_no_machine_is_shown_no_reminder()
    {
        var result = await UseCase(ReadyConnection(), new FakeMachineRestockSyncCoverageStore())
            .Handle(CancellationToken.None);

        Assert.Equal(
            MachineRestockSyncReminderHiddenReasons.NoProviderMachines,
            Assert.IsType<HiddenMachineRestockSyncReminderDto>(result).Reason);
    }

    [Fact]
    public async Task A_machine_with_no_recorded_sync_is_listed_as_such()
    {
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(
            new MachineRestockSyncCoverageRecord(
                AvenueHotelId, "Avenue Hotel", null, null, 0, null));

        var reminder = await ApplicableReminder(coverage);

        var machine = Assert.Single(reminder.Machines);
        Assert.Equal(AvenueHotelId, machine.LocalMachineId);
        Assert.Equal("Avenue Hotel", machine.MachineName);
        Assert.Null(machine.LastSuccessfulSyncAtUtc);
        Assert.Null(machine.LastSuccessfulSyncBusinessDate);
        Assert.Null(machine.DaysSinceLastSuccessfulSync);
        Assert.True(machine.IsFetchOverdue);
    }

    /// <summary>The exact boundary, through the use case and not only the policy.</summary>
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(7, true)]
    public async Task A_fetch_is_overdue_from_three_business_days(int daysAgo, bool expectedListed)
    {
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(
            new MachineRestockSyncCoverageRecord(
                AvenueHotelId, "Avenue Hotel", NowUtc.AddDays(-daysAgo), CurrentRevision, 0, null));

        var reminder = await ApplicableReminder(coverage);

        if (!expectedListed)
        {
            Assert.Empty(reminder.Machines);
            return;
        }

        var machine = Assert.Single(reminder.Machines);
        Assert.True(machine.IsFetchOverdue);
        Assert.Equal(daysAgo, machine.DaysSinceLastSuccessfulSync);
    }

    /// <summary>
    /// The business calendar, not UTC, decides the dates. This calendar puts a UTC instant on the
    /// <em>next</em> business date, as a zone ahead of UTC does for a late-evening instant: a fetch
    /// two UTC days old is one business day old, which is not overdue.
    /// </summary>
    [Fact]
    public async Task The_day_count_is_taken_from_the_business_calendar()
    {
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(
            new MachineRestockSyncCoverageRecord(
                AvenueHotelId, "Avenue Hotel", NowUtc.AddDays(-3), CurrentRevision, 0, null));

        var reminder = Assert.IsType<MachineRestockSyncReminderDto>(
            await UseCase(ReadyConnection(), coverage, calendar: new ShiftedBusinessCalendar(shiftDays: 1))
                .Handle(CancellationToken.None));

        // Today shifts with the fetch, so the difference is unchanged - but the published dates are
        // the business ones, which is what a reader is shown.
        Assert.Equal(new DateTime(2026, 10, 26), reminder.BusinessDate);
        Assert.Equal(new DateTime(2026, 10, 23), Assert.Single(reminder.Machines).LastSuccessfulSyncBusinessDate);
    }

    /// <summary>
    /// A recent successful fetch clears the overdue condition and nothing else: the machine's own
    /// unresolved events keep it on the banner.
    /// </summary>
    [Fact]
    public async Task Pending_events_are_a_reminder_even_when_the_fetch_is_recent()
    {
        var oldestPending = NowUtc.AddHours(-5);
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(
            new MachineRestockSyncCoverageRecord(
                AvenueHotelId, "Avenue Hotel", NowUtc.AddHours(-1), CurrentRevision, 2, oldestPending));

        var machine = Assert.Single((await ApplicableReminder(coverage)).Machines);

        Assert.False(machine.IsFetchOverdue);
        Assert.Equal(2, machine.KnownPendingEventCount);
        Assert.Equal(oldestPending, machine.OldestKnownPendingEventAtUtc);
        Assert.False(machine.HasPendingWorkBeforeDefaultFrom);
    }

    /// <summary>
    /// Ageing out of the three-day window must not clear pending work, and the row has to say that
    /// the dialog's default From date would hide it.
    /// </summary>
    [Fact]
    public async Task Older_pending_events_stay_visible_and_are_flagged_as_hidden_by_the_default_from_date()
    {
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(
            new MachineRestockSyncCoverageRecord(
                AvenueHotelId, "Avenue Hotel", NowUtc.AddHours(-1), CurrentRevision, 1, NowUtc.AddDays(-9)));

        var reminder = await ApplicableReminder(coverage);
        var machine = Assert.Single(reminder.Machines);

        Assert.Equal(1, machine.KnownPendingEventCount);
        Assert.True(machine.HasPendingWorkBeforeDefaultFrom);
        Assert.Equal(new DateTime(2026, 10, 22), reminder.DefaultFromBusinessDate);
        Assert.Equal(MachineRestockSyncReminderPolicy.OverdueAfterDays, reminder.OverdueAfterDays);
    }

    /// <summary>
    /// A machine synced inside the window with nothing unresolved is not listed. Applied and
    /// manually reconciled events are excluded by the store's own definition of pending, which is
    /// what a zero count here stands for.
    /// </summary>
    [Fact]
    public async Task A_recently_synced_machine_with_no_pending_events_is_not_listed()
    {
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(
            new MachineRestockSyncCoverageRecord(
                AvenueHotelId, "Avenue Hotel", NowUtc.AddHours(-2), CurrentRevision, 0, null),
            new MachineRestockSyncCoverageRecord(
                StationCafeId, "Station Cafe", NowUtc.AddDays(-4), CurrentRevision, 0, null));

        var reminder = await ApplicableReminder(coverage);

        Assert.Equal(StationCafeId, Assert.Single(reminder.Machines).LocalMachineId);
    }

    /// <summary>
    /// Coverage recorded under credentials that have since been replaced is read as no recorded
    /// sync at all - not as a fetch from the instant it happens to carry.
    /// </summary>
    [Fact]
    public async Task Coverage_from_a_superseded_credential_revision_is_no_recorded_sync()
    {
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(
            new MachineRestockSyncCoverageRecord(
                AvenueHotelId, "Avenue Hotel", NowUtc.AddMinutes(-5), CurrentRevision - 1, 0, null));

        var machine = Assert.Single((await ApplicableReminder(coverage)).Machines);

        Assert.Null(machine.LastSuccessfulSyncAtUtc);
        Assert.Null(machine.DaysSinceLastSuccessfulSync);
        Assert.True(machine.IsFetchOverdue);
    }

    /// <summary>
    /// What the stored connection can and cannot say about the machine alerts read, with no Nayax
    /// call made. Only a <c>Ready</c> connection whose machine-scoped checks were verified confirms
    /// it; <c>PendingPermissions</c>, and a <c>Ready</c> connection whose machine checks are still
    /// pending, do not - so the banner says the sync may be unavailable instead of offering it as a
    /// working action.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.Ready, false, MachineRestockSyncAvailability.Confirmed)]
    [InlineData(NayaxConnectionStatus.Ready, true, MachineRestockSyncAvailability.NotConfirmed)]
    [InlineData(NayaxConnectionStatus.PendingPermissions, false, MachineRestockSyncAvailability.NotConfirmed)]
    [InlineData(NayaxConnectionStatus.PendingPermissions, true, MachineRestockSyncAvailability.NotConfirmed)]
    public async Task The_published_availability_is_what_a_completed_check_confirmed(
        NayaxConnectionStatus status, bool machineChecksPending, string expected)
    {
        var connections = new FakeNayaxConnectionStore(
            "op", "token", status, CurrentRevision, machineChecksPending);
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(Overdue(AvenueHotelId, "Avenue Hotel"));

        var reminder = Assert.IsType<MachineRestockSyncReminderDto>(
            await UseCase(connections, coverage).Handle(CancellationToken.None));

        Assert.Equal(expected, reminder.SyncAvailability);
    }

    /// <summary>
    /// A read that fails is an error the caller sees, never an empty reminder: "no machine needs
    /// attention" is a statement about machines this read never got.
    /// </summary>
    [Fact]
    public async Task A_failed_aggregate_read_propagates_rather_than_reporting_nothing_pending()
    {
        var coverage = new FakeMachineRestockSyncCoverageStore { ReadFailure = new InvalidOperationException("db") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UseCase(ReadyConnection(), coverage).Handle(CancellationToken.None));
    }

    /// <summary>Nothing about this read writes: no coverage is recorded by looking at it.</summary>
    [Fact]
    public async Task Reading_the_reminder_acknowledges_nothing()
    {
        var coverage = new FakeMachineRestockSyncCoverageStore().WithMachines(
            new MachineRestockSyncCoverageRecord(
                AvenueHotelId, "Avenue Hotel", NowUtc.AddDays(-9), CurrentRevision, 3, NowUtc.AddDays(-9)));

        await ApplicableReminder(coverage);

        Assert.Empty(coverage.Attempts);
        Assert.Null(coverage.CoverageOf(AvenueHotelId));
    }

    private static async Task<MachineRestockSyncReminderDto> ApplicableReminder(
        FakeMachineRestockSyncCoverageStore coverage) =>
        Assert.IsType<MachineRestockSyncReminderDto>(
            await UseCase(ReadyConnection(), coverage).Handle(CancellationToken.None));

    private static GetMachineRestockSyncReminder UseCase(
        FakeNayaxConnectionStore connections,
        FakeMachineRestockSyncCoverageStore coverage,
        IBusinessScope? businessScope = null,
        IBusinessCalendar? calendar = null) =>
        new(
            new FakeClock(NowUtc),
            calendar ?? new FakeBusinessCalendar(NowUtc),
            businessScope ?? ResolvedMember(),
            connections,
            coverage);

    private static FakeNayaxConnectionStore ReadyConnection() =>
        new("op", "token", NayaxConnectionStatus.Ready, CurrentRevision);

    private static MachineRestockSyncCoverageRecord Overdue(long localMachineId, string name) =>
        new(localMachineId, name, NowUtc.AddDays(-7), CurrentRevision, 0, null);

    private static IBusinessScope ResolvedMember()
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(BusinessKey));
        return scope;
    }

    private static IBusinessScope SupportSession()
    {
        var scope = new BusinessScope();
        scope.ResolveForSupportSession(BusinessId.From(BusinessKey));
        return scope;
    }

    /// <summary>
    /// A business calendar whose dates sit a whole number of days away from the UTC date, so a test
    /// can prove the use case dates everything through <see cref="IBusinessCalendar"/> rather than
    /// reading <c>DateTime.Date</c> off a UTC instant itself.
    /// </summary>
    private sealed class ShiftedBusinessCalendar : IBusinessCalendar
    {
        private readonly int _shiftDays;

        public ShiftedBusinessCalendar(int shiftDays) => _shiftDays = shiftDays;

        public DateTime Today => ToBusinessDate(NowUtc);

        public DateTime ToBusinessDate(DateTime utcInstant) => utcInstant.Date.AddDays(_shiftDays);

        public DateTime StartOfBusinessDayUtc(DateTime businessDate) =>
            DateTime.SpecifyKind(businessDate.Date.AddDays(-_shiftDays), DateTimeKind.Utc);
    }
}
