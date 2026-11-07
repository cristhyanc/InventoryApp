using Inventory.Application.MachineStockSync;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// The Dashboard's hourly restock-fetch bound (issue #1122): one claim per business and credential
/// revision per hour, taken before the fetch so a concurrent visit cannot start a second one, and
/// given back only by the fetch that took it.
/// </summary>
public class MachineRestockFetchThrottleTests
{
    private static readonly DateTime Start = new(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly BusinessId Business = BusinessId.From(1);
    private static readonly BusinessId OtherBusiness = BusinessId.From(2);

    [Fact]
    public void The_first_visit_claims_the_hour_and_a_second_inside_it_does_not()
    {
        var clock = new FakeClock(Start);
        var throttle = new MachineRestockFetchThrottle(clock);

        Assert.True(throttle.TryClaim(Business, 4, out var claim));
        Assert.Equal(new MachineRestockFetchClaim(Business, 4, Start), claim);

        // Still running, or finished a minute ago: either way the hour is taken.
        clock.UtcNow = Start.AddMinutes(1);
        Assert.False(throttle.TryClaim(Business, 4, out _));

        clock.UtcNow = Start.AddMinutes(59).AddSeconds(59);
        Assert.False(throttle.TryClaim(Business, 4, out _));
    }

    [Fact]
    public void The_hour_ends_exactly_one_hour_after_the_claim()
    {
        var clock = new FakeClock(Start);
        var throttle = new MachineRestockFetchThrottle(clock);
        Assert.True(throttle.TryClaim(Business, 4, out _));

        clock.UtcNow = Start.Add(MachineRestockFetchThrottle.Window);

        Assert.True(throttle.TryClaim(Business, 4, out var next));
        Assert.Equal(clock.UtcNow, next.ClaimedAtUtc);
    }

    [Fact]
    public void A_credential_save_or_another_business_is_not_held_by_the_claim()
    {
        var throttle = new MachineRestockFetchThrottle(new FakeClock(Start));
        Assert.True(throttle.TryClaim(Business, 4, out _));

        Assert.True(throttle.TryClaim(Business, 5, out _));
        Assert.True(throttle.TryClaim(OtherBusiness, 4, out _));
    }

    [Fact]
    public void A_released_claim_lets_the_next_visit_fetch()
    {
        var clock = new FakeClock(Start);
        var throttle = new MachineRestockFetchThrottle(clock);
        Assert.True(throttle.TryClaim(Business, 4, out var claim));

        throttle.Release(claim);

        clock.UtcNow = Start.AddMinutes(1);
        Assert.True(throttle.TryClaim(Business, 4, out _));
    }

    /// <summary>A stale release can never remove a newer claim taken under the same key.</summary>
    [Fact]
    public void Releasing_an_old_claim_leaves_a_newer_one_in_place()
    {
        var clock = new FakeClock(Start);
        var throttle = new MachineRestockFetchThrottle(clock);
        Assert.True(throttle.TryClaim(Business, 4, out var old));

        clock.UtcNow = Start.AddHours(2);
        Assert.True(throttle.TryClaim(Business, 4, out _));

        throttle.Release(old);

        clock.UtcNow = Start.AddHours(2).AddMinutes(1);
        Assert.False(throttle.TryClaim(Business, 4, out _));
    }

    [Fact]
    public void Two_concurrent_visits_claim_once()
    {
        var throttle = new MachineRestockFetchThrottle(new FakeClock(Start));

        var claims = Enumerable.Range(0, 32)
            .AsParallel()
            .Select(_ => throttle.TryClaim(Business, 4, out var _))
            .Count(claimed => claimed);

        Assert.Equal(1, claims);
    }

    [Fact]
    public void Expired_claims_are_pruned_once_the_bound_is_reached()
    {
        var clock = new FakeClock(Start);
        var throttle = new MachineRestockFetchThrottle(clock);
        for (var revision = 0; revision < MachineRestockFetchThrottle.MaxTrackedKeys; revision++)
        {
            Assert.True(throttle.TryClaim(Business, revision, out _));
        }

        clock.UtcNow = Start.AddHours(1);
        Assert.True(throttle.TryClaim(OtherBusiness, 1, out _));

        Assert.Equal(1, throttle.TrackedKeys);
    }
}
