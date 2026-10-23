using Inventory.Application.SalesSync;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// The latest-sales freshness memory of issue #843 on its own: what it remembers, what it refuses to
/// remember, and the two halves of its key.
///
/// <see cref="SyncLatestNayaxSales"/>'s own tests cover the decision it feeds; these cover the
/// boundaries that decision depends on - the exact edge of the window, a key nothing has recorded, a
/// clock that moved backwards, and that two businesses and two credential revisions are four
/// separate answers rather than one shared one.
/// </summary>
public class NayaxSalesSyncFreshnessTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);

    private static readonly BusinessId First = BusinessId.From(1);
    private static readonly BusinessId Second = BusinessId.From(2);

    [Fact]
    public void A_key_with_no_recorded_run_is_not_recent()
    {
        var freshness = new NayaxSalesSyncFreshness(new FakeClock(Now));

        Assert.Null(freshness.FindRecentSync(First, credentialRevision: 1));
    }

    [Fact]
    public void A_run_is_recent_for_the_whole_window_and_not_at_its_edge()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);

        Assert.Equal(Now, freshness.RecordSuccessfulSync(First, credentialRevision: 1));

        Assert.Equal(Now, freshness.FindRecentSync(First, 1));

        clock.UtcNow = Now + NayaxSalesSyncFreshness.RecentWindow - TimeSpan.FromTicks(1);
        Assert.Equal(Now, freshness.FindRecentSync(First, 1));

        // Exactly at the window it is no longer recent, so "less than three minutes ago" is the
        // rule rather than "at most three minutes ago".
        clock.UtcNow = Now + NayaxSalesSyncFreshness.RecentWindow;
        Assert.Null(freshness.FindRecentSync(First, 1));
    }

    [Fact]
    public void The_window_is_the_three_minutes_the_owner_decided()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), NayaxSalesSyncFreshness.RecentWindow);
    }

    [Fact]
    public void A_later_run_replaces_the_instant_the_window_is_measured_from()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        freshness.RecordSuccessfulSync(First, 1);

        clock.UtcNow = Now.AddMinutes(2);
        freshness.RecordSuccessfulSync(First, 1);

        clock.UtcNow = Now.AddMinutes(4);
        Assert.Equal(Now.AddMinutes(2), freshness.FindRecentSync(First, 1));
    }

    /// <summary>
    /// The isolation the singleton makes necessary: a business is not told its sales are current
    /// because somebody else's were read.
    /// </summary>
    [Fact]
    public void One_businesss_run_is_not_another_businesss()
    {
        var freshness = new NayaxSalesSyncFreshness(new FakeClock(Now));
        freshness.RecordSuccessfulSync(First, 1);

        Assert.Equal(Now, freshness.FindRecentSync(First, 1));
        Assert.Null(freshness.FindRecentSync(Second, 1));
    }

    /// <summary>
    /// A sync is only done for the credentials it was done with: the revision the connection was
    /// saved at is half the key, so replacing it is a key nothing has recorded.
    /// </summary>
    [Fact]
    public void A_run_under_one_credential_revision_is_not_recent_under_another()
    {
        var freshness = new NayaxSalesSyncFreshness(new FakeClock(Now));
        freshness.RecordSuccessfulSync(First, credentialRevision: 7);

        Assert.Equal(Now, freshness.FindRecentSync(First, 7));
        Assert.Null(freshness.FindRecentSync(First, 8));
    }

    /// <summary>
    /// A clock moved backwards leaves an instant that appears to be in the future. That is not
    /// evidence of anything, so it means "run in full" rather than "recent": a skip claims the sales
    /// in front of the operator are current, and nothing unexplained may make that claim.
    /// </summary>
    [Fact]
    public void An_instant_in_the_future_is_not_recent()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        freshness.RecordSuccessfulSync(First, 1);

        clock.UtcNow = Now.AddMinutes(-1);

        Assert.Null(freshness.FindRecentSync(First, 1));
    }

    /// <summary>
    /// Clearing out drops only the keys that already answered "run in full", so it can neither grant
    /// nor deny a skip: the live key is still recent afterwards, and the passed ones are forgotten.
    /// </summary>
    [Fact]
    public void Clearing_out_forgets_the_passed_keys_and_keeps_the_live_one()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        for (var revision = 1; revision <= NayaxSalesSyncFreshness.MaxTrackedKeys + 1; revision++)
        {
            freshness.RecordSuccessfulSync(First, revision);
        }

        Assert.Equal(NayaxSalesSyncFreshness.MaxTrackedKeys + 1, freshness.TrackedKeys);

        clock.UtcNow = Now + NayaxSalesSyncFreshness.RecentWindow;
        freshness.RecordSuccessfulSync(Second, 1);

        Assert.Equal(1, freshness.TrackedKeys);
        Assert.Equal(clock.UtcNow, freshness.FindRecentSync(Second, 1));
        Assert.Null(freshness.FindRecentSync(First, 1));
    }

    /// <summary>
    /// Clearing out never drops a key whose window is still open, however many are tracked: that
    /// would reset the business's freshness and cost it a full run it had just completed.
    /// </summary>
    [Fact]
    public void Clearing_out_never_drops_a_key_that_is_still_recent()
    {
        var clock = new FakeClock(Now);
        var freshness = new NayaxSalesSyncFreshness(clock);
        for (var revision = 1; revision <= NayaxSalesSyncFreshness.MaxTrackedKeys + 2; revision++)
        {
            freshness.RecordSuccessfulSync(First, revision);
        }

        Assert.Equal(NayaxSalesSyncFreshness.MaxTrackedKeys + 2, freshness.TrackedKeys);
        Assert.Equal(Now, freshness.FindRecentSync(First, 1));
    }
}
