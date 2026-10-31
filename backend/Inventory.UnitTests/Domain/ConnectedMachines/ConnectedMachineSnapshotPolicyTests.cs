using Inventory.Domain.ConnectedMachines;
using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;
using Xunit;

namespace InventoryApi.Tests.Domain.ConnectedMachines;

/// <summary>
/// Which businesses are counted, which connection may be read, which snapshot is a day's count, and
/// what a UTC snapshot day is (issue #978).
/// </summary>
public class ConnectedMachineSnapshotPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 6, 30, 0, DateTimeKind.Utc);

    private static readonly DateTime Day = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Pro has its machines counted; the plans with no Nayax connection do not. It is asked as "at
    /// least Pro" so a plan declared above Pro later is counted rather than silently dropping out of
    /// billing evidence.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free, false)]
    [InlineData(SubscriptionPlan.Starter, false)]
    [InlineData(SubscriptionPlan.Pro, true)]
    public void Only_a_plan_with_connected_machines_is_counted(SubscriptionPlan plan, bool expected)
    {
        Assert.Equal(expected, ConnectedMachineSnapshotPolicy.RecordsSnapshots(plan));
    }

    /// <summary>An undeclared plan is not a plan, so it is not counted either.</summary>
    [Fact]
    public void An_undeclared_plan_is_not_counted()
    {
        Assert.False(ConnectedMachineSnapshotPolicy.RecordsSnapshots((SubscriptionPlan)9999));
    }

    /// <summary>
    /// The plan on record is the entitlement rule's own answer with the rollout gates set aside:
    /// evidence has to accrue before enforcement is switched on, because until then "snapshots may be
    /// recorded but nothing is shown or billed".
    /// </summary>
    [Fact]
    public void The_plan_on_record_ignores_the_rollout_gates()
    {
        var grants = new[]
        {
            new ManualPlanGrantState(
                SubscriptionPlan.Pro,
                ManualPlanGrantSource.Grandfathered,
                new EntitlementPeriod(Now.AddDays(-30), null)),
        };

        Assert.Equal(
            SubscriptionPlan.Pro,
            ConnectedMachineSnapshotPolicy.PlanOnRecord(grants, billingSubscription: null, Now));
    }

    /// <summary>
    /// And it is the same rule in every other respect: an expired grant entitles nothing, so the
    /// business is on Free and is not counted. No second entitlement table exists here.
    /// </summary>
    [Fact]
    public void An_expired_record_leaves_the_business_uncounted()
    {
        var grants = new[]
        {
            new ManualPlanGrantState(
                SubscriptionPlan.Pro,
                ManualPlanGrantSource.Complimentary,
                new EntitlementPeriod(Now.AddDays(-30), Now.AddDays(-1))),
        };

        var plan = ConnectedMachineSnapshotPolicy.PlanOnRecord(grants, billingSubscription: null, Now);

        Assert.Equal(SubscriptionPlan.Free, plan);
        Assert.False(ConnectedMachineSnapshotPolicy.RecordsSnapshots(plan));
    }

    /// <summary>
    /// A paid Pro subscription is counted on exactly the same terms as a grant, because the rule is
    /// "the higher of the two records" and neither record is consulted to validate the other.
    /// </summary>
    [Fact]
    public void A_paid_pro_subscription_is_counted()
    {
        var subscription = new BillingSubscriptionState(
            SubscriptionPlan.Pro,
            BillingSubscriptionStatus.Active,
            new EntitlementPeriod(Now.AddDays(-5), Now.AddDays(25)));

        Assert.Equal(
            SubscriptionPlan.Pro,
            ConnectedMachineSnapshotPolicy.PlanOnRecord([], subscription, Now));
    }

    /// <summary>
    /// A cancelled subscription entitles nothing from the moment it is recorded, so its machines are
    /// not counted - the same fail-closed direction the entitlement rule takes.
    /// </summary>
    [Fact]
    public void A_cancelled_subscription_leaves_the_business_uncounted()
    {
        var subscription = new BillingSubscriptionState(
            SubscriptionPlan.Pro,
            BillingSubscriptionStatus.Cancelled,
            new EntitlementPeriod(Now.AddDays(-5), Now.AddDays(25)));

        Assert.Equal(
            SubscriptionPlan.Free,
            ConnectedMachineSnapshotPolicy.PlanOnRecord([], subscription, Now));
    }

    /// <summary>
    /// The fleet may be read exactly where every other ordinary Nayax call may run: the one status
    /// gate, not a second rule written for counting.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.Ready, true)]
    [InlineData(NayaxConnectionStatus.PendingPermissions, true)]
    [InlineData(NayaxConnectionStatus.NeedsAttention, false)]
    [InlineData(NayaxConnectionStatus.NotConfigured, false)]
    public void The_fleet_is_read_only_where_an_ordinary_nayax_call_may_run(
        NayaxConnectionStatus status,
        bool expected)
    {
        Assert.Equal(expected, ConnectedMachineSnapshotPolicy.MayReadFleet(status));
        Assert.Equal(NayaxConnectionStatusGate.AllowsOrdinaryOperations(status), expected);
    }

    /// <summary>The later UTC day is the later evidence.</summary>
    [Fact]
    public void The_later_day_is_the_later_evidence()
    {
        var latest = ConnectedMachineSnapshotPolicy.Latest(
        [
            Snapshot(Day.AddDays(-1), series: 3, count: 1),
            Snapshot(Day, series: 1, count: 2),
        ]);

        Assert.Equal(Day, latest!.DateUtc);
        Assert.Equal(1, latest.AccountSeries);
    }

    /// <summary>
    /// And on one day the later account series wins - the day's count after an account change is the
    /// account the business ended the day on.
    /// </summary>
    [Fact]
    public void On_one_day_the_later_series_is_the_days_count()
    {
        var latest = ConnectedMachineSnapshotPolicy.Latest(
        [
            Snapshot(Day, series: 2, count: 5),
            Snapshot(Day, series: 1, count: 9),
        ]);

        Assert.Equal(2, latest!.AccountSeries);
        Assert.Equal(5, latest.MachineCount);
    }

    /// <summary>No snapshots is no evidence, which is not a count of zero.</summary>
    [Fact]
    public void No_snapshots_is_no_evidence()
    {
        Assert.Null(ConnectedMachineSnapshotPolicy.Latest([]));
    }

    /// <summary>
    /// A snapshot day is the UTC day of the instant, at midnight and marked UTC, whatever time of day
    /// the read happened - so one business's day cannot drift from another's.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(13, 45, 1)]
    [InlineData(23, 59, 1)]
    public void A_snapshot_day_is_the_utc_day_of_the_instant(int hour, int minute, int expectedDay)
    {
        var day = ConnectedMachineSnapshotDay.For(
            new DateTime(2026, 11, expectedDay, hour, minute, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 11, expectedDay, 0, 0, 0, DateTimeKind.Utc), day);
        Assert.Equal(DateTimeKind.Utc, day.Kind);
    }

    private static ConnectedMachineSnapshot Snapshot(DateTime dayUtc, int series, int count) =>
        new(
            dayUtc,
            series,
            count,
            [.. Enumerable.Range(1, count).Select(id => (long)id)],
            dayUtc.AddHours(5),
            ReadWasComplete: true);
}
