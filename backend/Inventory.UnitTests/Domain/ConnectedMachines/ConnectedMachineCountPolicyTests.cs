using Inventory.Domain.ConnectedMachines;
using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;
using Xunit;

namespace InventoryApi.Tests.Domain.ConnectedMachines;

/// <summary>
/// The connected-machine entitlement table, row by row (issue #978): the allowance, the count, how
/// well known it is, and how many machines are over the allowance.
/// </summary>
public class ConnectedMachineCountPolicyTests
{
    private static readonly DateTime Today = new(2026, 11, 10, 6, 30, 0, DateTimeKind.Utc);

    private static readonly DateTime TodayUtcDay = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Pro includes five connected machines and the count comes from today's snapshot of the current
    /// account series. The allowance is the plan catalogue's number, never a literal written here
    /// twice.
    /// </summary>
    [Fact]
    public void Todays_snapshot_of_the_current_series_is_the_current_count()
    {
        var entitlement = Decide(Snapshot(TodayUtcDay, series: 2, count: 7), currentSeries: 2);

        Assert.Equal(ConnectedMachineCountState.Current, entitlement.State);
        Assert.Equal(ConnectedMachineCountUnavailableReason.None, entitlement.UnavailableReason);
        Assert.Equal(PlanCatalogue.For(SubscriptionPlan.Pro).IncludedConnectedMachines, entitlement.Allowance);
        Assert.Equal(7, entitlement.Count);
        Assert.Equal(TodayUtcDay, entitlement.AsOfDateUtc);
        Assert.Equal(0, entitlement.AgeInDays);
        Assert.Equal(2, entitlement.MachinesOverAllowance);
    }

    /// <summary>
    /// An older snapshot stays the count and is reported stale with its age - the rule a failed,
    /// partial or paginated-incomplete read leaves in place, because such a read stores nothing.
    /// </summary>
    [Fact]
    public void An_older_snapshot_stays_the_count_and_is_reported_stale_with_its_age()
    {
        var entitlement = Decide(
            Snapshot(TodayUtcDay.AddDays(-3), series: 1, count: 4),
            currentSeries: 1);

        Assert.Equal(ConnectedMachineCountState.Stale, entitlement.State);
        Assert.Equal(4, entitlement.Count);
        Assert.Equal(3, entitlement.AgeInDays);
        Assert.Equal(TodayUtcDay.AddDays(-3), entitlement.AsOfDateUtc);
        Assert.Equal(0, entitlement.MachinesOverAllowance);
    }

    /// <summary>
    /// A day that holds snapshots from two account series is the account change of issue #978, and
    /// the day's count is the later series' snapshot - the account the business ended the day on.
    /// </summary>
    [Fact]
    public void On_a_day_with_two_series_the_later_series_is_the_days_count()
    {
        var entitlement = Decide(
            currentSeries: 2,
            Snapshot(TodayUtcDay, series: 1, count: 9),
            Snapshot(TodayUtcDay, series: 2, count: 3));

        Assert.Equal(ConnectedMachineCountState.Current, entitlement.State);
        Assert.Equal(3, entitlement.Count);
        Assert.Equal(0, entitlement.MachinesOverAllowance);
    }

    /// <summary>
    /// A snapshot of a superseded account is not this account's count: the previous series' rows stay
    /// exactly as they are and the new account is reported as not counted yet, rather than inheriting
    /// a figure from an account the business has left.
    /// </summary>
    [Fact]
    public void A_snapshot_of_a_superseded_series_is_not_the_current_accounts_count()
    {
        var entitlement = Decide(Snapshot(TodayUtcDay, series: 1, count: 9), currentSeries: 2);

        Assert.Equal(ConnectedMachineCountState.Unavailable, entitlement.State);
        Assert.Equal(
            ConnectedMachineCountUnavailableReason.AccountNotYetCounted,
            entitlement.UnavailableReason);
        Assert.Null(entitlement.Count);
        Assert.Null(entitlement.AsOfDateUtc);
        Assert.Null(entitlement.AgeInDays);
        Assert.Null(entitlement.MachinesOverAllowance);
    }

    /// <summary>A usable connection that has never been counted has no count, and no zero.</summary>
    [Fact]
    public void A_connection_that_has_never_been_counted_reports_no_count()
    {
        var entitlement = Decide(currentSeries: 1);

        Assert.Equal(ConnectedMachineCountState.Unavailable, entitlement.State);
        Assert.Equal(ConnectedMachineCountUnavailableReason.NeverCounted, entitlement.UnavailableReason);
        Assert.Null(entitlement.Count);
    }

    /// <summary>
    /// Disconnected and needs-attention credentials make no call and report "count unavailable" with
    /// the reason that tells them apart - and they do so whatever snapshots exist, because a broken
    /// or absent connection's old figure is not a current count.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured, ConnectedMachineCountUnavailableReason.NotConnected)]
    [InlineData(
        NayaxConnectionStatus.NeedsAttention,
        ConnectedMachineCountUnavailableReason.ConnectionNeedsAttention)]
    public void An_unusable_connection_reports_the_count_unavailable(
        NayaxConnectionStatus status,
        ConnectedMachineCountUnavailableReason expected)
    {
        var entitlement = ConnectedMachineCountPolicy.Decide(
            SubscriptionPlan.Pro,
            ConnectedMachineCountPolicy.NoPurchasedAdditionalMachines,
            status,
            [Snapshot(TodayUtcDay, series: 1, count: 7)],
            currentAccountSeries: 1,
            Today);

        Assert.Equal(ConnectedMachineCountState.Unavailable, entitlement.State);
        Assert.Equal(expected, entitlement.UnavailableReason);
        Assert.Null(entitlement.Count);
        Assert.Equal(PlanCatalogue.For(SubscriptionPlan.Pro).IncludedConnectedMachines, entitlement.Allowance);
    }

    /// <summary>
    /// The allowance is the plan's included machines plus the extras the business bought, and the
    /// over-allowance is measured against that total. A negative purchase is none: an allowance is
    /// never reduced by a record nobody could have written.
    /// </summary>
    [Theory]
    [InlineData(0, 5, 2)]
    [InlineData(2, 7, 0)]
    [InlineData(-3, 5, 2)]
    public void Purchased_extras_raise_the_allowance(int purchased, int expectedAllowance, int expectedOver)
    {
        var entitlement = ConnectedMachineCountPolicy.Decide(
            SubscriptionPlan.Pro,
            purchased,
            NayaxConnectionStatus.Ready,
            [Snapshot(TodayUtcDay, series: 1, count: 7)],
            currentAccountSeries: 1,
            Today);

        Assert.Equal(expectedAllowance, entitlement.Allowance);
        Assert.Equal(expectedOver, entitlement.MachinesOverAllowance);
        Assert.Equal(Math.Max(0, purchased), entitlement.PurchasedAdditional);
    }

    /// <summary>
    /// A plan with no Nayax connection has an allowance of none - a real allowance of zero rather
    /// than an unlimited one - and a count under it is entirely over the allowance. Being over is
    /// billed and never blocked, which is why this is an amount rather than a refusal.
    /// </summary>
    [Fact]
    public void A_plan_with_no_connected_machines_has_an_allowance_of_none()
    {
        var entitlement = ConnectedMachineCountPolicy.Decide(
            SubscriptionPlan.Starter,
            ConnectedMachineCountPolicy.NoPurchasedAdditionalMachines,
            NayaxConnectionStatus.Ready,
            [Snapshot(TodayUtcDay, series: 1, count: 2)],
            currentAccountSeries: 1,
            Today);

        Assert.Equal(0, entitlement.IncludedByPlan);
        Assert.Equal(0, entitlement.Allowance);
        Assert.Equal(2, entitlement.Count);
        Assert.Equal(2, entitlement.MachinesOverAllowance);
    }

    /// <summary>A count at the allowance is nothing over it, and never a negative amount.</summary>
    [Fact]
    public void A_count_at_or_below_the_allowance_is_nothing_over_it()
    {
        var entitlement = Decide(Snapshot(TodayUtcDay, series: 1, count: 1), currentSeries: 1);

        Assert.Equal(0, entitlement.MachinesOverAllowance);
    }

    /// <summary>An undeclared plan has no catalogue entry, so there is no allowance to invent.</summary>
    [Fact]
    public void An_undeclared_plan_has_no_allowance_to_report()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectedMachineCountPolicy.Decide(
            (SubscriptionPlan)9999,
            ConnectedMachineCountPolicy.NoPurchasedAdditionalMachines,
            NayaxConnectionStatus.Ready,
            [],
            currentAccountSeries: 1,
            Today));
    }

    private static ConnectedMachineEntitlement Decide(
        int currentSeries,
        params ConnectedMachineSnapshot[] snapshots) =>
        ConnectedMachineCountPolicy.Decide(
            SubscriptionPlan.Pro,
            ConnectedMachineCountPolicy.NoPurchasedAdditionalMachines,
            NayaxConnectionStatus.Ready,
            snapshots,
            currentSeries,
            Today);

    private static ConnectedMachineEntitlement Decide(
        ConnectedMachineSnapshot snapshot,
        int currentSeries) =>
        Decide(currentSeries, snapshot);

    private static ConnectedMachineSnapshot Snapshot(DateTime dayUtc, int series, int count) =>
        new(
            dayUtc,
            series,
            count,
            [.. Enumerable.Range(1, count).Select(id => (long)id)],
            dayUtc.AddHours(5),
            ReadWasComplete: true);
}
