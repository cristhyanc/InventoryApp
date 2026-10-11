using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Tenancy;

/// <summary>
/// "At most once per hour per membership" (issue #524).
///
/// The rule exists because the refresh is scheduled by <em>every</em> request a member makes: a
/// member clicking around the application makes dozens, and each one carries the same two claims.
/// Without the throttle, a cosmetic write would be queued dozens of times a minute against a
/// single-writer SQLite file for data that changes perhaps once in a person's employment.
/// </summary>
public class MemberProfileRefreshThrottleTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 1, 0, 0, DateTimeKind.Utc);

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";
    private const string OtherOid = "33333333-3333-3333-3333-333333333333";

    private static ActorIdentity Actor(string oid = Oid)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, oid, out var actor));
        return actor!;
    }

    [Fact]
    public void The_first_request_claims_the_slot()
    {
        var throttle = new MemberProfileRefreshThrottle(new FakeClock(Now));

        Assert.True(throttle.TryClaim(Actor()));
    }

    /// <summary>
    /// Every later request inside the hour is refused, however many there are - which is the point
    /// of the rule, since each of them carries the same claims as the one that was stored.
    /// </summary>
    [Fact]
    public void Every_further_request_inside_the_hour_is_refused()
    {
        var clock = new FakeClock(Now);
        var throttle = new MemberProfileRefreshThrottle(clock);

        Assert.True(throttle.TryClaim(Actor()));

        foreach (var elapsed in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(59.9) })
        {
            clock.UtcNow = Now + elapsed;
            Assert.False(throttle.TryClaim(Actor()));
        }
    }

    [Fact]
    public void The_slot_is_free_again_once_the_interval_has_elapsed()
    {
        var clock = new FakeClock(Now);
        var throttle = new MemberProfileRefreshThrottle(clock);

        Assert.True(throttle.TryClaim(Actor()));

        clock.UtcNow = Now + MemberProfileRefreshThrottle.Interval;
        Assert.True(throttle.TryClaim(Actor()));

        // And claiming it again restarts the hour from the second claim rather than the first.
        Assert.False(throttle.TryClaim(Actor()));
    }

    /// <summary>
    /// The slot belongs to one membership, not to the process: one member's refresh must not keep
    /// another member's out for an hour.
    /// </summary>
    [Fact]
    public void Each_identity_has_its_own_slot()
    {
        var throttle = new MemberProfileRefreshThrottle(new FakeClock(Now));

        Assert.True(throttle.TryClaim(Actor()));
        Assert.True(throttle.TryClaim(Actor(OtherOid)));
        Assert.False(throttle.TryClaim(Actor()));
        Assert.False(throttle.TryClaim(Actor(OtherOid)));
    }

    /// <summary>
    /// The same identity spelled differently is the same identity - <c>ActorIdentity</c> normalises
    /// both halves - so a token whose GUID casing differs does not get a second hourly slot.
    /// </summary>
    [Fact]
    public void A_differently_spelled_identity_does_not_get_a_second_slot()
    {
        var throttle = new MemberProfileRefreshThrottle(new FakeClock(Now));

        Assert.True(throttle.TryClaim(Actor()));
        Assert.True(ActorIdentity.TryCreate(Tid.ToLowerInvariant(), Oid.ToLowerInvariant(), out var lowercase));
        Assert.False(throttle.TryClaim(lowercase!));
    }

    /// <summary>
    /// A clock that jumps backwards - an NTP correction - must not open the slot early. The
    /// fail-safe direction for this rule is "do not refresh".
    /// </summary>
    [Fact]
    public void A_clock_moving_backwards_does_not_open_the_slot()
    {
        var clock = new FakeClock(Now);
        var throttle = new MemberProfileRefreshThrottle(clock);

        Assert.True(throttle.TryClaim(Actor()));

        clock.UtcNow = Now - TimeSpan.FromHours(5);
        Assert.False(throttle.TryClaim(Actor()));
    }

    /// <summary>
    /// What the throttle remembers is bounded. Only an identity that resolved an active membership
    /// ever reaches it, so the live set is the people who signed in within the hour, but the
    /// bookkeeping still prunes what has expired rather than keeping an entry per identity for the
    /// lifetime of the process.
    /// </summary>
    [Fact]
    public void Expired_entries_do_not_accumulate()
    {
        var clock = new FakeClock(Now);
        var throttle = new MemberProfileRefreshThrottle(clock);

        for (var i = 0; i < MemberProfileRefreshThrottle.MaxTrackedIdentities + 1; i++)
        {
            Assert.True(throttle.TryClaim(Actor($"33333333-3333-3333-3333-{i:D12}")));
        }

        Assert.Equal(MemberProfileRefreshThrottle.MaxTrackedIdentities + 1, throttle.TrackedIdentities);

        // An hour later every one of them has expired, and the next claim clears them out.
        clock.UtcNow = Now + MemberProfileRefreshThrottle.Interval;
        Assert.True(throttle.TryClaim(Actor()));

        Assert.True(throttle.TrackedIdentities <= 1);
    }
}
